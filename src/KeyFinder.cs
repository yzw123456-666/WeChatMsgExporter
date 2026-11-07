using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WeChatMsgExporter;

// ============ 从微信进程内存提取数据库密钥 ============
//
// v3 (WeChat.exe / WeChatWin.dll):
//   1) 快路径: WX_OFFS.json 中按版本偏移，从 WeChatWin.dll 基址+偏移处读指针再读 32 字节
//   2) 慢路径: 在 WeChatWin.dll 内存中搜索 "iphone\0"/"android\0"/"ipad\0" 特征串，
//      从特征地址向前 2000 字节内按 8 字节步进读指针，读出候选 32 字节密钥并用 MicroMsg.db 验证
//
// v4 (Weixin.exe / WCDB):
//   WCDB 为每个库缓存 SQL 文本: x'<64位hex密钥><32位hex盐>'
//   全内存扫描该模式，用盐值匹配数据库文件并做 HMAC-SHA512 快速验证（每库独立密钥）

public static class WxKeyFinder
{
    // ---------- Win32 ----------

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr h, long addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualQueryEx(IntPtr h, long addr, ref MEMORY_BASIC_INFORMATION mbi, uint len);
    [DllImport("psapi.dll", SetLastError = true)]
    static extern bool EnumProcessModules(IntPtr h, IntPtr[] mods, uint cb, out uint needed);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetModuleFileNameEx(IntPtr h, IntPtr mod, StringBuilder name, uint size);

    const uint PROCESS_QUERY_INFORMATION = 0x0400;
    const uint PROCESS_VM_READ = 0x0010;
    const uint MEM_COMMIT = 0x1000;
    const uint MEM_PRIVATE = 0x20000;

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORY_BASIC_INFORMATION
    {
        public long BaseAddress;        // 0
        public long AllocationBase;     // 8
        public uint AllocationProtect;  // 16
        public uint _partitionId;       // 20 (x64: 4 字节对齐填充)
        public long RegionSize;         // 24
        public uint State;              // 32
        public uint Protect;            // 36
        public uint Type;               // 40
        public uint _pad2;              // 44 → 总长 48
    }

    static readonly uint[] ReadableProtect = { 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80 };

    public class ProcInfo
    {
        public int Pid;
        public string Name;        // WeChat.exe / Weixin.exe
        public string ExePath;
        public string Version;     // 文件版本
        public long WorkingSet;    // 工作集（用于挑主进程）
        public bool IsV4 => Name.Equals("Weixin.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static List<ProcInfo> FindWeChatProcesses()
    {
        var list = new List<ProcInfo>();
        var seen = new HashSet<int>();
        foreach (var name in new[] { "WeChat", "Weixin" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (!seen.Add(p.Id)) continue;
                    long ws = 0;
                    try { ws = p.WorkingSet64; } catch { }
                    string path = "";
                    try { path = p.MainModule?.FileName ?? ""; } catch { }
                    var pi = new ProcInfo { Pid = p.Id, Name = p.ProcessName + ".exe", ExePath = path, WorkingSet = ws };
                    if (!string.IsNullOrEmpty(path))
                    {
                        try { pi.Version = p.MainModule?.FileVersionInfo?.FileVersion ?? FileVersionInfo.GetVersionInfo(path).FileVersion; }
                        catch { try { pi.Version = FileVersionInfo.GetVersionInfo(path).FileVersion; } catch { } }
                    }
                    list.Add(pi);
                }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        // 主进程（占用内存最大）排最前，辅助进程在后
        return list.OrderByDescending(x => x.IsV4).ThenByDescending(x => x.WorkingSet).ToList();
    }

    static string QueryExePath(int pid) => "";

    // ---------- 对外入口 ----------

    public class ScanResult
    {
        public string KeyHex = "";                  // v3: 全局密钥；v4: ""（用 PerDbKeys）
        public Dictionary<string, string> PerDbKeys = new();  // v4: db路径 -> enc_key hex
        public int IvOff = 4016;                    // v4: 页尾 IV 偏移（保留段探测结果）
        public string Detail = "";
        public bool Found => !string.IsNullOrEmpty(KeyHex) || PerDbKeys.Count > 0;
    }

    /// <summary>扫描微信进程，返回密钥。dbPage1: 已找到的候选库 (路径->page1字节)，用于验证。</summary>
    public static ScanResult Scan(ProcInfo proc, Dictionary<string, byte[]> dbPage1, Action<string> log = null)
    {
        log?.Invoke($"打开进程 {proc.Name} (PID={proc.Pid})...");
        IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, proc.Pid);
        if (h == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开微信进程（请以相同权限运行本软件与微信）");

        try
        {
            if (proc.IsV4) return ScanV4(h, dbPage1, log);

            // ---- v3 ----
            var mm = dbPage1.FirstOrDefault(kv => Path.GetFileName(kv.Key).Equals("MicroMsg.db", StringComparison.OrdinalIgnoreCase)
                                                  || kv.Key.EndsWith("MicroMsg.db", StringComparison.OrdinalIgnoreCase));
            byte[] mmPage1 = mm.Value ?? dbPage1.Values.FirstOrDefault();
            if (mmPage1 == null) throw new Exception("未找到 MicroMsg.db，无法验证密钥");

            // 1) 版本偏移快路径
            var offs = LoadOffs();
            var (mBase0, _, modPath) = EnumModules(h).FirstOrDefault(m => m.Item3.Contains("WeChatWin.dll", StringComparison.OrdinalIgnoreCase));
            var version = proc.Version;
            if (string.IsNullOrEmpty(version) && !string.IsNullOrEmpty(modPath))
            {
                try { version = FileVersionInfo.GetVersionInfo(modPath).FileVersion; } catch { }
            }
            if (!string.IsNullOrEmpty(version) && offs.TryGetValue(version, out var bias) && mBase0 != 0)
            {
                log?.Invoke($"命中版本偏移表 {version}，尝试快速读取...");
                var key = ReadKeyByOffset(h, mBase0 + bias[4], Environment.Is64BitProcess ? 8 : 4);
                if (key != null && WxDbCrypto.VerifyV3(key, mmPage1))
                {
                    log?.Invoke("快速读取成功 ✓");
                    return new ScanResult { KeyHex = Convert.ToHexString(key), Detail = "版本偏移表" };
                }
            }

            // 2) WeChatWin.dll 特征扫描
            log?.Invoke("扫描 WeChatWin.dll 内存特征（约需几秒到几十秒）...");
            var (modBase, modEnd) = FindWeChatWinRange(h);
            if (modBase == 0) throw new Exception("未找到 WeChatWin.dll 模块");
            var key2 = ScanV3(h, modBase, modEnd, mmPage1, log);
            if (key2 != null) return new ScanResult { KeyHex = Convert.ToHexString(key2), Detail = "内存特征扫描" };
            return new ScanResult { Detail = "未在内存中找到密钥（请确认微信已登录且聊天窗口已打开）" };
        }
        finally { CloseHandle(h); }
    }

    static Dictionary<string, long[]> LoadOffs()
    {
        try
        {
            using var s = typeof(WxKeyFinder).Assembly.GetManifestResourceStream("WeChatMsgExporter.WX_OFFS.json");
            using var sr = new StreamReader(s);
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(sr.ReadToEnd());
            var r = new Dictionary<string, long[]>();
            foreach (var kv in dict)
                if (kv.Value.ValueKind == System.Text.Json.JsonValueKind.Array && kv.Value.GetArrayLength() >= 5)
                    r[kv.Key] = kv.Value.EnumerateArray().Select(x => (long)x.GetInt64()).ToArray();
            return r;
        }
        catch { return new(); }
    }

    static (long, long) FindWeChatWinRange(IntPtr h)
    {
        foreach (var (baseAddr, size, path) in EnumModules(h))
            if (path.Contains("WeChatWin.dll", StringComparison.OrdinalIgnoreCase))
                return (baseAddr, baseAddr + size);
        return (0, 0);
    }

    static long FindWeChatWinBase(IntPtr h) { var (b, _) = FindWeChatWinRange(h); return b; }

    public static List<(long, long, string)> EnumModules(IntPtr h)
    {
        var result = new List<(long, long, string)>();
        try
        {
            var buf = new IntPtr[4096];
            if (EnumProcessModules(h, buf, (uint)(IntPtr.Size * buf.Length), out var needed))
            {
                int count = Math.Min((int)(needed / IntPtr.Size), buf.Length);
                for (int i = 0; i < count; i++)
                {
                    var sb = new StringBuilder(1024);
                    if (GetModuleFileNameEx(h, buf[i], sb, 1024) > 0)
                    {
                        // 模块大小通过 REGION 查询首个段获得
                        var mbi = new MEMORY_BASIC_INFORMATION();
                        long addr = buf[i].ToInt64();
                        if (VirtualQueryEx(h, addr, ref mbi, (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) != IntPtr.Zero)
                            result.Add((addr, mbi.RegionSize, sb.ToString()));
                    }
                }
            }
        }
        catch { }
        return result;
    }

    static byte[] ReadKeyByOffset(IntPtr h, long addr, int addrLen)
    {
        var ptrBuf = new byte[addrLen];
        if (!ReadProcessMemory(h, addr, ptrBuf, (IntPtr)addrLen, out _)) return null;
        long keyAddr = BitConverter.ToInt64(ptrBuf, 0);
        var key = new byte[32];
        if (!ReadProcessMemory(h, keyAddr, key, (IntPtr)32, out _)) return null;
        return key;
    }

    // ---------- v3 特征扫描 ----------

    static byte[] ScanV3(IntPtr h, long start, long end, byte[] page1, Action<string> log)
    {
        // 在模块范围内搜索 phone-type 特征
        var anchors = new List<long>();
        byte[][] patterns = { Encoding.ASCII.GetBytes("iphone\0"), Encoding.ASCII.GetBytes("android\0"), Encoding.ASCII.GetBytes("ipad\0") };
        int maxHitsPerType = 64;
        var typeHits = new Dictionary<int, int>();

        long addr = start;
        var mbi = new MEMORY_BASIC_INFORMATION();
        uint mbiSize = (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        while (addr < end)
        {
            if (VirtualQueryEx(h, addr, ref mbi, mbiSize) == IntPtr.Zero) break;
            if (mbi.State == MEM_COMMIT && ReadableProtect.Contains(mbi.Protect) && mbi.RegionSize > 0)
            {
                var size = (int)Math.Min(mbi.RegionSize, end - addr);
                if (size > 0)
                {
                    var buf = new byte[size];
                    if (ReadProcessMemory(h, addr, buf, (IntPtr)size, out var read) && read.ToInt64() > 64)
                    {
                        for (int t = 0; t < 3; t++)
                        {
                            if (typeHits.TryGetValue(t, out var c) && c >= maxHitsPerType) continue;
                            int idx = 0;
                            while ((idx = IndexOf(buf, patterns[t], idx)) >= 0)
                            {
                                anchors.Add(addr + idx);
                                typeHits[t] = typeHits.TryGetValue(t, out var cc) ? cc + 1 : 1;
                                if (typeHits[t] >= maxHitsPerType) break;
                                idx += 1;
                            }
                        }
                    }
                }
            }
            addr += mbi.RegionSize;
        }

        if (anchors.Count == 0) { log?.Invoke("未找到特征串"); return null; }
        log?.Invoke($"找到 {anchors.Count} 个特征地址，逐个验证候选密钥...");

        anchors.Sort((a, b) => b.CompareTo(a)); // 从大到小

        // 收集候选指针（去重）
        var candidates = new HashSet<long>();
        foreach (var a in anchors)
            for (long j = a; j > a - 2000; j -= 8)
            {
                var pb = new byte[8];
                if (!ReadProcessMemory(h, j, pb, (IntPtr)8, out _)) continue;
                long ptr = BitConverter.ToInt64(pb, 0);
                if (ptr > 0x10000 && ptr < 0x7FFFFFFEFFFF) candidates.Add(ptr);
            }
        log?.Invoke($"共 {candidates.Count} 个候选地址，开始 PBKDF2 验证（并行）...");

        byte[] foundKey = null;
        object gate = new();
        using var cts = new CancellationTokenSource();
        int total = candidates.Count, done = 0;

        try
        {
            Parallel.ForEach(candidates, new ParallelOptions { CancellationToken = cts.Token, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) },
                ptr =>
                {
                    if (cts.IsCancellationRequested) return;
                    var key = new byte[32];
                    if (!ReadProcessMemory(h, ptr, key, (IntPtr)32, out _)) return;
                    if (key.All(b => b == 0)) return;
                    if (WxDbCrypto.VerifyV3(key, page1))
                    {
                        lock (gate) { if (foundKey == null) foundKey = key; }
                        cts.Cancel();
                    }
                    int d = Interlocked.Increment(ref done);
                    if (d % 64 == 0) log?.Invoke($"验证进度 {d}/{total} ...");
                });
        }
        catch (OperationCanceledException) { }
        return foundKey;
    }

    static int IndexOf(byte[] hay, byte[] needle, int from)
    {
        int lim = hay.Length - needle.Length;
        for (int i = Math.Max(0, from); i <= lim; i++)
        {
            int j = 0;
            for (; j < needle.Length; j++) if (hay[i + j] != needle[j]) break;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    // ---------- v4 数据目录发现（从进程内存挖掘自定义路径） ----------

    public class V4RootInfo
    {
        public string Root;      // ...\xwechat_files
        public string WxId;      // 紧随其后的 wxid 目录
    }

    /// <summary>扫描 Weixin.exe 内存，找出所有 xwechat_files 数据根目录及账号（自定义安装位置也能找到）</summary>
    public static List<V4RootInfo> DiscoverV4Roots(int pid, Action<string> log = null)
    {
        var result = new List<V4RootInfo>();
        IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) return result;
        try
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (baseAddr, size, _) in EnumRegions(h))
            {
                int chunk = 4 * 1024 * 1024, overlap = 512;
                for (long off = 0; off < size; off += chunk - overlap)
                {
                    int len = (int)Math.Min(chunk, size - off);
                    if (len < 64) break;
                    var buf = new byte[len];
                    if (!ReadProcessMemory(h, baseAddr + off, buf, (IntPtr)len, out var read) || read.ToInt64() < 64) break;
                    len = (int)read.ToInt64();
                    foreach (var root in ExtractRoots(buf, Encoding.Unicode, baseAddr + off))
                        if (found.Add(root.Root + "|" + root.WxId)) result.Add(root);
                    foreach (var root in ExtractRoots(buf, Encoding.UTF8, baseAddr + off))
                        if (found.Add(root.Root + "|" + root.WxId)) result.Add(root);
                }
            }
            log?.Invoke($"内存中发现 {result.Count} 个候选数据目录");
        }
        finally { CloseHandle(h); }
        return result;
    }

    static IEnumerable<V4RootInfo> ExtractRoots(byte[] buf, Encoding enc, long regionAddr)
    {
        byte[] needle = enc.GetBytes("xwechat_files");
        int step = enc is UnicodeEncoding ? 2 : 1;
        int i = 0;
        while ((i = IndexOfFrom(buf, needle, i)) >= 0)
        {
            // 回溯找路径起点（遇到 NUL 或不可打印字符停止）
            int start = i;
            int backLimit = Math.Min(i, 520 * step);
            while (start - step >= 0 && i - start < backLimit)
            {
                int prev = start - step;
                if (buf[prev] < 0x20 && !(enc is UnicodeEncoding && prev + 1 < buf.Length && buf[prev + 1] == 0)) break;
                if (enc is UnicodeEncoding && buf[prev + 1] != 0) break;
                start = prev;
            }
            // 前向取完整路径直到 NUL
            int end = i + needle.Length;
            while (end + step <= buf.Length)
            {
                if (enc is UnicodeEncoding) { if (buf[end] == 0 && buf[end + 1] == 0) break; end += 2; }
                else { if (buf[end] == 0) break; end += 1; }
            }
            string full;
            try { full = enc.GetString(buf, start, Math.Min(end, buf.Length) - start); } catch { yield break; }
            if (full.Length > 4 && full.Length < 500 && full.Contains(':'))
            {
                int xi = full.IndexOf("xwechat_files", StringComparison.OrdinalIgnoreCase);
                string root = full[..(xi + "xwechat_files".Length)];
                string rest = full[(xi + "xwechat_files".Length)..].TrimStart('\\', '/');
                string wxid = rest.Split('\\', '/').FirstOrDefault() ?? "";
                if (wxid.Length > 3 && wxid.Length < 64)
                    yield return new V4RootInfo { Root = root, WxId = wxid };
            }
            i += needle.Length;
        }
    }

    static int IndexOfFrom(byte[] hay, byte[] needle, int from)
    {
        int lim = hay.Length - needle.Length;
        for (int i = Math.Max(0, from); i <= lim; i++)
        {
            int j = 0;
            for (; j < needle.Length; j++) if (hay[i + j] != needle[j]) break;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    // ---------- v4 扫描: x'<hex64..192>' ----------

    static ScanResult ScanV4(IntPtr h, Dictionary<string, byte[]> dbPage1, Action<string> log)
    {
        if (dbPage1.Count == 0)
        {
            log("没有可验证的数据库，跳过扫描");
            return new ScanResult { Detail = "无数据库" };
        }
        // 收集 盐值 -> [db路径]
        var saltToDbs = new Dictionary<string, List<string>>();
        var pageBySalt = new Dictionary<string, byte[]>();
        foreach (var kv in dbPage1)
        {
            var salt = Convert.ToHexString(kv.Value, 0, 16);
            if (!saltToDbs.TryGetValue(salt, out var l)) saltToDbs[salt] = l = new();
            l.Add(kv.Key);
            pageBySalt[salt] = kv.Value;
        }
        log?.Invoke($"共 {dbPage1.Count} 个数据库，{saltToDbs.Count} 种盐值");

        var solved = new Dictionary<string, string>(); // salt -> key hex
        log?.Invoke("检查密钥缓存...");

        // 1) 密钥缓存命中（盐值存在于库文件中，密钥与库绑定，跨启动有效）
        var cache = LoadKeyCache();
        log?.Invoke($"缓存条目 {cache.Count}");
        if (cache.Count > 0)
            foreach (var kv in pageBySalt)
            {
                if (!cache.TryGetValue(kv.Key, out var khex)) continue;
                try
                {
                    if (WxDbCrypto.VerifyV4(Convert.FromHexString(khex), kv.Value))
                    {
                        solved[kv.Key] = khex;
                        log?.Invoke($"  缓存命中: {Path.GetFileName(kv.Key)}");
                    }
                }
                catch { }
            }
        if (solved.Count >= saltToDbs.Count)
        {
            log?.Invoke("全部密钥来自缓存，无需扫描内存");
            return BuildResult(saltToDbs, solved, pageBySalt, log, resultIvOff);
        }

        // 2) 扫描私有可写堆（DLL 镜像/只读段不含运行时密钥，跳过可大幅提速）
        log?.Invoke("枚举内存区域...");
        var regions = EnumRegions(h).Where(r => r.type == 0x20000 && r.size >= 4096).ToList();
        long totalBytes = regions.Sum(r => r.size);
        log?.Invoke($"可读内存区域 {regions.Count} 个，共 {totalBytes / 1024 / 1024} MB，开始扫描 x'...' 密钥缓存...");

        long doneBytes = 0, hits = 0;
        var gate = new object();
        var seenKeySalt = new HashSet<string>();   // (key,salt) 去重，避免海量重复验证
        using var cts = new CancellationTokenSource();
        int procCount = Math.Max(2, Environment.ProcessorCount);

        Parallel.ForEach(regions, new ParallelOptions { CancellationToken = cts.Token, MaxDegreeOfParallelism = procCount },
            r =>
            {
                if (cts.IsCancellationRequested) return;
                int chunk = 4 * 1024 * 1024, overlap = 256;
                var buf = new byte[chunk];   // 每线程复用缓冲区
                for (long off = 0; off < r.size; off += chunk - overlap)
                {
                    if (cts.IsCancellationRequested) return;
                    int len = (int)Math.Min(chunk, r.size - off);
                    if (len < 64) break;
                    if (!ReadProcessMemory(h, r.baseAddr + off, buf, (IntPtr)len, out var read) || read.ToInt64() < 64) break;
                    len = (int)read.ToInt64();
                    if (ScanBufferV4(buf, len, pageBySalt, solved, seenKeySalt, gate)) { cts.Cancel(); return; }
                    long now = Interlocked.Add(ref doneBytes, len);
                    if (now / (128L * 1024 * 1024) != (now - len) / (128L * 1024 * 1024))
                        log?.Invoke($"  已扫描 {now / 1024 / 1024} MB，命中 {solved.Count}/{saltToDbs.Count} 盐值...");
                }
            });

        log?.Invoke($"扫描完成，命中 {solved.Count}/{saltToDbs.Count} 种盐值");

        // 交叉验证：未命中的盐用已找到的密钥再试一遍
        foreach (var salt in saltToDbs.Keys.ToList())
        {
            if (solved.ContainsKey(salt)) continue;
            foreach (var k in solved.Values.Distinct())
                if (WxDbCrypto.VerifyV4(Convert.FromHexString(k), pageBySalt[salt])) { solved[salt] = k; break; }
        }

        // 兜底：x'...' 模式未命中时，按盐值字节单遍定位密钥邻域（不依赖密钥字符串格式）
        var stillMissing = saltToDbs.Keys.Where(s => !solved.ContainsKey(s)).ToList();
        if (stillMissing.Count > 0)
        {
            log?.Invoke($"尝试盐值邻域扫描（{stillMissing.Count} 个未命中）...");
            var saltBytes = stillMissing.Select(s => Convert.FromHexString(s)).ToList();
            var saltIdx = stillMissing.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i);
            var solvedRaw = new Dictionary<int, byte[]>();   // saltIdx -> raw key
            var bruteCount = 0;

            foreach (var pid in Process.GetProcessesByName("Weixin").OrderByDescending(p => { try { return p.WorkingSet64; } catch { return 0; } }).Select(p => p.Id).ToList())
            {
                if (solvedRaw.Count >= stillMissing.Count) break;
                IntPtr h2 = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
                if (h2 == IntPtr.Zero) continue;
                long procBytes = EnumRegions(h2).Where(r => r.type == 0x20000).Sum(r => r.size);
                log?.Invoke($"  进程 PID={pid}（私有堆 {procBytes / 1024 / 1024} MB）...");
                long procDone = 0;
                try
                {
                    foreach (var (baseAddr, size, type) in EnumRegions(h2))
                    {
                        if (solvedRaw.Count >= stillMissing.Count) break;
                        if (type != 0x20000) continue;   // 只扫私有堆
                        int chunk = 4 * 1024 * 1024, overlap = 4096;
                        var buf = new byte[chunk];
                        for (long off = 0; off < size; off += chunk - overlap)
                        {
                            if (solvedRaw.Count >= stillMissing.Count) break;
                            int len = (int)Math.Min(chunk, size - off);
                            if (len < 64) break;
                            if (!ReadProcessMemory(h2, baseAddr + off, buf, (IntPtr)len, out var read) || read.ToInt64() < 64) break;
                            len = (int)read.ToInt64();
                            procDone += len;
                            if (procDone / (256L * 1024 * 1024) != (procDone - len) / (256L * 1024 * 1024))
                                log?.Invoke($"    已扫 {procDone / 1024 / 1024} MB，命中 {solvedRaw.Count}/{stillMissing.Count}");
                            for (int si = 0; si < saltBytes.Count; si++)
                            {
                                if (solvedRaw.ContainsKey(si)) continue;
                                var sb = saltBytes[si];
                                int idx = 0;
                                int tries = 0;
                                while (tries < 12 && (idx = IndexOfFrom(buf, sb, idx)) >= 0)
                                {
                                    long hitAddr = baseAddr + off + idx;
                                    var key = BruteNear(h2, hitAddr, pageBySalt[stillMissing[si]]);
                                    bruteCount++;
                                    if (key != null) { solvedRaw[si] = key; break; }
                                    tries++; idx += 1;
                                }
                            }
                        }
                    }
                }
                finally { CloseHandle(h2); }
                log?.Invoke($"  进程 PID={pid} 完成，累计命中 {solvedRaw.Count}/{stillMissing.Count}");
            }
            foreach (var kv in solvedRaw)
                solved[stillMissing[kv.Key]] = Convert.ToHexString(kv.Value);
            log?.Invoke($"邻域扫描结束（暴力验证 {bruteCount} 次），命中 {solvedRaw.Count}/{stillMissing.Count}");
        }

        // 终极兜底：已知明文全堆暴力搜索（SQLCipher 页1解密后必以 "SQLite format 3\0" 开头）
        // 同时扫描 IV 位置变体（保留段布局可能随版本变化）
        var stillMissing2 = saltToDbs.Keys.Where(s => !solved.ContainsKey(s)).ToList();
        if (stillMissing2.Count > 0)
        {
            log?.Invoke("尝试全堆已知明文暴力搜索（多保留段布局，约 3-10 分钟）...");
            var (bruteKey, ivOff) = BruteAllWeixin(pageBySalt, log);
            if (bruteKey != null)
            {
                var khex = Convert.ToHexString(bruteKey);
                foreach (var salt in saltToDbs.Keys.ToList())
                {
                    if (solved.ContainsKey(salt)) continue;
                    try
                    {
                        if (WxDbCrypto.VerifyV4Auto(bruteKey, pageBySalt[salt], ivOff))
                        {
                            solved[salt] = khex;
                            resultIvOff = ivOff;
                        }
                    }
                    catch { }
                }
                log?.Invoke($"暴力搜索命中（{khex} ivOff={ivOff}），验证通过的库 {solved.Count}/{saltToDbs.Count}");
            }
            else log?.Invoke("暴力搜索未命中");
        }

        // 方案 C：AES 轮密钥表扫描 —— WCDB/OpenSSL 在堆里缓存扩展后的密钥表（AES-256 为 240B），
        // 原始密钥未必单独存在。利用密钥扩展算法的结构校验（w[i] = w[i-8] ⊕ T(w[i-1])）定位轮密钥表，
        // 反推 enc_key 后用 HMAC 验证。
        var stillMissing3 = saltToDbs.Keys.Where(s => !solved.ContainsKey(s)).ToList();
        if (stillMissing3.Count > 0)
        {
            log?.Invoke("尝试 AES 轮密钥表扫描（结构校验，约 2-4 分钟）...");
            var sk = BruteSchedules(pageBySalt, log);
            if (sk != null)
            {
                var khex = Convert.ToHexString(sk.Value.key);
                foreach (var salt in saltToDbs.Keys.ToList())
                {
                    if (solved.ContainsKey(salt)) continue;
                    try
                    {
                        if (WxDbCrypto.VerifyV4Auto(sk.Value.key, pageBySalt[salt], sk.Value.ivOff))
                        { solved[salt] = khex; resultIvOff = sk.Value.ivOff; }
                    }
                    catch { }
                }
                log?.Invoke($"轮密钥表扫描命中（{khex} ivOff={sk.Value.ivOff}），验证通过 {solved.Count}/{saltToDbs.Count}");
            }
            else log?.Invoke("轮密钥表扫描未命中");
        }

        SaveKeyCache(solved);
        return BuildResult(saltToDbs, solved, pageBySalt, log, resultIvOff);
    }

    // ---- AES 结构校验工具 ----
    static readonly byte[] AesSBox = {
        0x63,0x7C,0x77,0x7B,0xF2,0x6B,0x6F,0xC5,0x30,0x01,0x67,0x2B,0xFE,0xD7,0xAB,0x76,
        0xCA,0x82,0xC9,0x7D,0xFA,0x59,0x47,0xF0,0xAD,0xD4,0xA2,0xAF,0x9C,0xA4,0x72,0xC0,
        0xB7,0xFD,0x93,0x26,0x36,0x3F,0xF7,0xCC,0x34,0xA5,0xE5,0xF1,0x71,0xD8,0x31,0x15,
        0x04,0xC7,0x23,0xC3,0x18,0x96,0x05,0x9A,0x07,0x12,0x80,0xE2,0xEB,0x27,0xB2,0x75,
        0x09,0x83,0x2C,0x1A,0x1B,0x6E,0x5A,0xA0,0x52,0x3B,0xD6,0xB3,0x29,0xE3,0x2F,0x84,
        0x53,0xD1,0x00,0xED,0x20,0xFC,0xB1,0x5B,0x6A,0xCB,0xBE,0x39,0x4A,0x4C,0x58,0xCF,
        0xD0,0xEF,0xAA,0xFB,0x43,0x4D,0x33,0x85,0x45,0xF9,0x02,0x7F,0x50,0x3C,0x9F,0xA8,
        0x51,0xA3,0x40,0x8F,0x92,0x9D,0x38,0xF5,0xBC,0xB6,0xDA,0x21,0x10,0xFF,0xF3,0xD2,
        0xCD,0x0C,0x13,0xEC,0x5F,0x97,0x44,0x17,0xC4,0xA7,0x7E,0x3D,0x64,0x5D,0x19,0x73,
        0x60,0x81,0x4F,0xDC,0x22,0x2A,0x90,0x88,0x46,0xEE,0xB8,0x14,0xDE,0x5E,0x0B,0xDB,
        0xE0,0x32,0x3A,0x0A,0x49,0x06,0x24,0x5C,0xC2,0xD3,0xAC,0x62,0x91,0x95,0xE4,0x79,
        0xE7,0xC8,0x37,0x6D,0x8D,0xD5,0x4E,0xA9,0x6C,0x56,0xF4,0xEA,0x65,0x7A,0xAE,0x08,
        0xBA,0x78,0x25,0x2E,0x1C,0xA6,0xB4,0xC6,0xE8,0xDD,0x74,0x1F,0x4B,0xBD,0x8B,0x8A,
        0x70,0x3E,0xB5,0x66,0x48,0x03,0xF6,0x0E,0x61,0x35,0x57,0xB9,0x86,0xC1,0x1D,0x9E,
        0xE1,0xF8,0x98,0x11,0x69,0xD9,0x8E,0x94,0x9B,0x1E,0x87,0xE9,0xCE,0x55,0x28,0xDF,
        0x8C,0xA1,0x89,0x0D,0xBF,0xE6,0x42,0x68,0x41,0x99,0x2D,0x0F,0xB0,0x54,0xBB,0x16 };

    static uint BERead(byte[] b, int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
    static uint RotWord(uint w) => (w << 8) | (w >> 24);
    static uint SubWord(uint w) =>
        ((uint)AesSBox[(int)((w >> 24) & 0xFF)] << 24) | ((uint)AesSBox[(int)((w >> 16) & 0xFF)] << 16) |
        ((uint)AesSBox[(int)((w >> 8) & 0xFF)] << 8) | AesSBox[(int)(w & 0xFF)];
    static uint RconVal(int i)
    {
        byte r = 1;
        for (int k = 1; k < i; k++) r = (byte)((r << 1) ^ ((r & 0x80) != 0 ? 0x1B : 0));
        return (uint)(r << 24);
    }

    /// <summary>扫描 AES-256/128 轮密钥表，返回验证通过的 enc_key</summary>
    static (byte[] key, int ivOff)? BruteSchedules(Dictionary<string, byte[]> pageBySalt, Action<string> log)
    {
        var kv0 = pageBySalt.First();
        byte[] page1 = kv0.Value;
        int[] ivOffs = { 4016, 4048, 4032, 4080, 4000, 3984 };

        foreach (var pid in Process.GetProcessesByName("Weixin")
                     .OrderByDescending(p => { try { return p.WorkingSet64; } catch { return 0; } })
                     .Select(p => p.Id).ToList())
        {
            IntPtr h2 = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
            if (h2 == IntPtr.Zero) continue;
            log?.Invoke($"  轮表进程 PID={pid} ...");
            try
            {
                foreach (var (baseAddr, size, type) in EnumRegions(h2))
                {
                    if (type != 0x20000) continue;
                    int chunk = 8 * 1024 * 1024, overlap = 512;
                    var buf = new byte[chunk];
                    for (long off = 0; off < size; off += chunk - overlap)
                    {
                        int len = (int)Math.Min(chunk, size - off);
                        if (len < 512) break;
                        if (!ReadProcessMemory(h2, baseAddr + off, buf, (IntPtr)len, out var read) || read.ToInt64() < 512) break;
                        len = (int)read.ToInt64();
                        for (int o = 0; o + 240 <= len; o += 4)
                        {
                            // AES-256: w[8] = w[0] ⊕ SubWord(RotWord(w[7])) ⊕ Rcon(1)
                            uint w0 = BERead(buf, o);
                            uint w7 = BERead(buf, o + 28);
                            uint w8s = BERead(buf, o + 32);
                            if (w8s != (w0 ^ SubWord(RotWord(w7)) ^ RconVal(1))) continue;
                            bool all = true;
                            for (int i = 9; i < 12; i++)
                            {
                                uint wim1 = BERead(buf, o + 4 * (i - 1));
                                uint wim8 = BERead(buf, o + 4 * (i - 8));
                                if (BERead(buf, o + 4 * i) != (wim8 ^ wim1)) { all = false; break; }
                            }
                            if (!all) continue;
                            var key = new byte[32];
                            Array.Copy(buf, o, key, 0, 32);
                            foreach (var ivOff in ivOffs)
                                if (WxDbCrypto.VerifyV4Auto(key, page1, ivOff)) return (key, ivOff);
                        }
                        // AES-128: w[4] = w[0] ⊕ SubWord(RotWord(w[3])) ⊕ Rcon(1)
                        for (int o = 0; o + 176 <= len; o += 4)
                        {
                            uint w0 = BERead(buf, o);
                            uint w3 = BERead(buf, o + 12);
                            uint w4s = BERead(buf, o + 16);
                            if (w4s != (w0 ^ SubWord(RotWord(w3)) ^ RconVal(1))) continue;
                            bool all = true;
                            for (int i = 5; i < 8; i++)
                            {
                                uint wim1 = BERead(buf, o + 4 * (i - 1));
                                uint wim4 = BERead(buf, o + 4 * (i - 4));
                                if (BERead(buf, o + 4 * i) != (wim4 ^ wim1)) { all = false; break; }
                            }
                            if (!all) continue;
                            var key16 = new byte[16];
                            Array.Copy(buf, o, key16, 0, 16);
                            foreach (var ivOff in ivOffs)
                                if (WxDbCrypto.VerifyV4Auto(key16, page1, ivOff)) return (key16, ivOff);
                        }
                    }
                }
            }
            finally { CloseHandle(h2); }
        }
        return null;
    }

    static int resultIvOff = 4016;

    /// <summary>遍历所有 Weixin 进程私有堆，用 SQLite 文件头已知明文快速校验候选密钥（含 IV 位置变体）</summary>
    static (byte[] key, int ivOff) BruteAllWeixin(Dictionary<string, byte[]> pageBySalt, Action<string> log)
    {
        var kv0 = pageBySalt.First();
        byte[] page1 = kv0.Value;
        byte[] hdr = Encoding.ASCII.GetBytes("SQLite format 3\0");
        int[] ivOffs = { 4016, 4048, 4032, 4080, 4000, 3984 };
        var expects = ivOffs.Select(o =>
        {
            var e = new byte[16];
            for (int i = 0; i < 16; i++) e[i] = (byte)(page1[16 + i] ^ page1[o + i]);   // E(hdr) = C1 ⊕ IV
            return e;
        }).ToArray();

        foreach (var pid in Process.GetProcessesByName("Weixin")
                     .OrderByDescending(p => { try { return p.WorkingSet64; } catch { return 0; } })
                     .Select(p => p.Id).ToList())
        {
            IntPtr h2 = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
            if (h2 == IntPtr.Zero) continue;
            log?.Invoke($"  暴力进程 PID={pid} ...");
            try
            {
                byte[] result = null;
                int resultIv = 4016;
                var regions = EnumRegions(h2).Where(r => r.type == 0x20000).ToList();
                using var cts = new CancellationTokenSource();
                object gate = new();

                Parallel.ForEach(regions, new ParallelOptions { CancellationToken = cts.Token, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) },
                    r =>
                    {
                        if (result != null) return;
                        var aes = Aes.Create();
                        aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None; aes.KeySize = 256;
                        var zeroIv = new byte[16];
                        var cand = new byte[32];
                        var blk = new byte[16];
                        int chunk = 8 * 1024 * 1024, overlap = 32;
                        var buf = new byte[chunk];
                        try
                        {
                            for (long off = 0; off < r.size; off += chunk - overlap)
                            {
                                if (result != null || cts.IsCancellationRequested) return;
                                int len = (int)Math.Min(chunk, r.size - off);
                                if (len < 64) break;
                                if (!ReadProcessMemory(h2, r.baseAddr + off, buf, (IntPtr)len, out var read) || read.ToInt64() < 64) break;
                                len = (int)read.ToInt64();
                                for (int o = 0; o + 32 <= len; o += 8)
                                {
                                    if (buf[o] == 0 && buf[o + 1] == 0) { o += 6; continue; }
                                    Array.Copy(buf, o, cand, 0, 32);
                                    using var enc = aes.CreateEncryptor(cand, zeroIv);
                                    enc.TransformBlock(hdr, 0, 16, blk, 0);
                                    for (int v = 0; v < ivOffs.Length; v++)
                                    {
                                        bool ok = true;
                                        var exp = expects[v];
                                        for (int i = 0; i < 16; i++) if (blk[i] != exp[i]) { ok = false; break; }
                                        if (ok)
                                        {
                                            lock (gate)
                                            {
                                                if (result == null) { result = (byte[])cand.Clone(); resultIv = ivOffs[v]; log?.Invoke($"  [暴力] 0x{r.baseAddr + off + o:X} 命中 ivOff={ivOffs[v]}"); }
                                            }
                                            cts.Cancel();
                                            return;
                                        }
                                    }
                                }
                            }
                        }
                        catch (OperationCanceledException) { }
                        finally { aes.Dispose(); }
                    });
                if (result != null) return (result, resultIv);
            }
            finally { CloseHandle(h2); }
        }
        return (null, 4016);
    }

    /// <summary>在盐值命中点附近按 8/4 字节步进暴力验证 32 字节候选密钥</summary>
    static byte[] BruteNear(IntPtr h, long hitAddr, byte[] page1)
    {
        long lo = Math.Max(0x10000, hitAddr - 2048), hi = hitAddr + 2048;
        for (long a = lo; a + 32 <= hi; a += 8)
        {
            var c = new byte[32];
            if (!ReadProcessMemory(h, a, c, (IntPtr)32, out _)) continue;
            if (c.All(b => b == 0) || c.Distinct().Count() < 8) continue;
            if (WxDbCrypto.VerifyV4(c, page1)) return c;
        }
        for (long a = lo + 4; a + 32 <= hi; a += 8)
        {
            var c = new byte[32];
            if (!ReadProcessMemory(h, a, c, (IntPtr)32, out _)) continue;
            if (WxDbCrypto.VerifyV4(c, page1)) return c;
        }
        return null;
    }

    static ScanResult BuildResult(Dictionary<string, List<string>> saltToDbs, Dictionary<string, string> solved,
        Dictionary<string, byte[]> dbPage1, Action<string> log, int ivOff)
    {
        var result = new ScanResult { IvOff = ivOff };
        foreach (var kv in saltToDbs)
            if (solved.TryGetValue(kv.Key, out var key))
                foreach (var db in kv.Value) result.PerDbKeys[db] = key;

        // 若所有库同一密钥，简化为全局
        var distinct = result.PerDbKeys.Values.Distinct().ToList();
        if (distinct.Count == 1) result.KeyHex = distinct[0];

        result.Detail = $"命中 {result.PerDbKeys.Count}/{dbPage1.Count} 个库";
        log?.Invoke(result.Detail);
        return result;
    }

    // ---------- 密钥缓存（盐值 -> 密钥，存本地；密钥与库文件绑定可跨启动复用） ----------

    static string KeyCachePath => Path.Combine(AppPaths.Root, "keycache.json");

    static Dictionary<string, string> LoadKeyCache()
    {
        try
        {
            if (!File.Exists(KeyCachePath)) return new();
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(KeyCachePath)) ?? new();
        }
        catch { return new(); }
    }

    static void SaveKeyCache(Dictionary<string, string> solved)
    {
        try
        {
            var old = LoadKeyCache();
            foreach (var kv in solved) old[kv.Key] = kv.Value;
            File.WriteAllText(KeyCachePath, System.Text.Json.JsonSerializer.Serialize(old));
        }
        catch { }
    }

    static bool ScanBufferV4(byte[] buf, int len, Dictionary<string, byte[]> pageBySalt,
        Dictionary<string, string> solved, HashSet<string> seenKeySalt, object gate)
    {
        int i = 0;
        while (i < len - 70)
        {
            if (buf[i] == (byte)'x' && buf[i + 1] == (byte)'\'')
            {
                int j = i + 2;
                while (j < buf.Length && buf[j] != (byte)'\'' && j - i - 2 <= 200) j++;
                int hexLen = j - i - 2;
                if (j < buf.Length && buf[j] == (byte)'\'' && hexLen >= 64 && hexLen <= 192 && hexLen % 2 == 0 && IsHex(buf, i + 2, hexLen))
                {
                    var hex = Encoding.ASCII.GetString(buf, i + 2, hexLen);
                    string keyHex = null, saltHex = null;
                    if (hexLen == 96) { keyHex = hex[..64]; saltHex = hex[64..]; }
                    else if (hexLen == 64) { keyHex = hex; }
                    else { keyHex = hex[..64]; saltHex = hex[^32..]; }

                    if (!string.IsNullOrEmpty(saltHex) && pageBySalt.ContainsKey(saltHex) && !solved.ContainsKey(saltHex))
                    {
                        if (seenKeySalt.Add(keyHex + saltHex) && WxDbCrypto.VerifyV4(Convert.FromHexString(keyHex), pageBySalt[saltHex]))
                        {
                            lock (gate) solved[saltHex] = keyHex;
                        }
                    }
                    else if (saltHex == null)
                    {
                        // 无盐值：去重后逐个尝试未解出的库（上限防海量误报拖慢）
                        if (seenKeySalt.Add(keyHex))
                        {
                            foreach (var kv in pageBySalt)
                            {
                                if (solved.ContainsKey(kv.Key)) continue;
                                if (seenKeySalt.Count > 20000) break;
                                if (WxDbCrypto.VerifyV4(Convert.FromHexString(keyHex), kv.Value))
                                    lock (gate) solved[kv.Key] = keyHex;
                            }
                        }
                    }
                }
                i = j + 1;
            }
            else i++;
        }
        return false;
    }

    static bool IsHex(byte[] b, int start, int len)
    {
        for (int i = start; i < start + len; i++)
        {
            byte c = b[i];
            bool ok = (c >= (byte)'0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok) return false;
        }
        return true;
    }

    // 供探针使用
    public static List<(long baseAddr, long size, uint type)> EnumRegionsForProbe(IntPtr h) => EnumRegions(h);
    public static int Index(byte[] hay, byte[] needle, int from) => IndexOf(hay, needle, from);

    static List<(long baseAddr, long size, uint type)> EnumRegions(IntPtr h)
    {
        var regions = new List<(long, long, uint)>();
        long addr = 0x10000;
        var mbi = new MEMORY_BASIC_INFORMATION();
        uint size = (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        while (addr < 0x7FFFFFFEFFFF)
        {
            if (VirtualQueryEx(h, addr, ref mbi, size) == IntPtr.Zero) break;
            if (mbi.State == MEM_COMMIT && ReadableProtect.Contains(mbi.Protect) && mbi.RegionSize > 0 && mbi.RegionSize < 4L * 1024 * 1024 * 1024)
                regions.Add((mbi.BaseAddress, mbi.RegionSize, mbi.Type));
            long next = mbi.BaseAddress + mbi.RegionSize;
            if (next <= addr) break;
            addr = next;
        }
        return regions;
    }
}

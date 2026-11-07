using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
        public long BaseAddress;
        public long AllocationBase;
        public uint AllocationProtect;
        public long _pad;
        public long RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint _pad2;
    }

    static readonly uint[] ReadableProtect = { 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80 };

    public class ProcInfo
    {
        public int Pid;
        public string Name;        // WeChat.exe / Weixin.exe
        public string ExePath;
        public string Version;     // 文件版本
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
                    string path = "";
                    try { path = p.MainModule?.FileName ?? ""; } catch { }
                    var pi = new ProcInfo { Pid = p.Id, Name = p.ProcessName + ".exe", ExePath = path };
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
        return list.OrderByDescending(x => x.IsV4).ToList();
    }

    static string QueryExePath(int pid) => "";

    // ---------- 对外入口 ----------

    public class ScanResult
    {
        public string KeyHex = "";                  // v3: 全局密钥；v4: ""（用 PerDbKeys）
        public Dictionary<string, string> PerDbKeys = new();  // v4: db路径 -> enc_key hex
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

    // ---------- v4 扫描: x'<hex64..192>' ----------

    static ScanResult ScanV4(IntPtr h, Dictionary<string, byte[]> dbPage1, Action<string> log)
    {
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
        var regions = EnumRegions(h);
        long totalBytes = regions.Sum(r => r.size);
        log?.Invoke($"可读内存区域 {regions.Count} 个，共 {totalBytes / 1024 / 1024} MB，开始扫描 x'...' 密钥缓存...");

        long doneBytes = 0, hits = 0;
        var gate = new object();
        using var cts = new CancellationTokenSource();

        Parallel.ForEach(regions, new ParallelOptions { CancellationToken = cts.Token, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) },
            r =>
            {
                if (cts.IsCancellationRequested) return;
                int chunk = 4 * 1024 * 1024, overlap = 256;
                for (long off = 0; off < r.size; off += chunk - overlap)
                {
                    if (cts.IsCancellationRequested) return;
                    int len = (int)Math.Min(chunk, r.size - off);
                    if (len < 64) break;
                    var buf = new byte[len];
                    if (!ReadProcessMemory(h, r.baseAddr + off, buf, (IntPtr)len, out var read) || read.ToInt64() < 64) break;
                    len = (int)read.ToInt64();
                    if (ScanBufferV4(buf, pageBySalt, solved, gate)) { cts.Cancel(); return; }
                    Interlocked.Add(ref doneBytes, len);
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

        var result = new ScanResult();
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

    static bool ScanBufferV4(byte[] buf, Dictionary<string, byte[]> pageBySalt, Dictionary<string, string> solved, object gate)
    {
        int i = 0;
        while (i < buf.Length - 70)
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
                        if (WxDbCrypto.VerifyV4(Convert.FromHexString(keyHex), pageBySalt[saltHex]))
                        {
                            lock (gate) solved[saltHex] = keyHex;
                        }
                    }
                    else if (saltHex == null)
                    {
                        // 无盐值，尝试所有未解出的库
                        foreach (var kv in pageBySalt)
                        {
                            if (solved.ContainsKey(kv.Key)) continue;
                            if (WxDbCrypto.VerifyV4(Convert.FromHexString(keyHex), kv.Value))
                                lock (gate) solved[kv.Key] = keyHex;
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

    static List<(long baseAddr, long size)> EnumRegions(IntPtr h)
    {
        var regions = new List<(long, long)>();
        long addr = 0x10000;
        var mbi = new MEMORY_BASIC_INFORMATION();
        uint size = (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        while (addr < 0x7FFFFFFEFFFF)
        {
            if (VirtualQueryEx(h, addr, ref mbi, size) == IntPtr.Zero) break;
            if (mbi.State == MEM_COMMIT && ReadableProtect.Contains(mbi.Protect) && mbi.RegionSize > 0 && mbi.RegionSize < 500L * 1024 * 1024)
                regions.Add((mbi.BaseAddress, mbi.RegionSize));
            long next = mbi.BaseAddress + mbi.RegionSize;
            if (next <= addr) break;
            addr = next;
        }
        return regions;
    }
}

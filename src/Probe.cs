using System.Runtime.InteropServices;

namespace WeChatMsgExporter;

// 内存探针：统计盐值命中次数 + 转储命中点邻域（仅 --probe 模式使用）
public static class Probe
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr h, long addr, byte[] buf, IntPtr size, out IntPtr read);

    public static void Run(int pid, List<(string saltHex, byte[] page1)> dbs)
    {
        IntPtr h = OpenProcess(0x0400 | 0x0010, false, pid);
        if (h == IntPtr.Zero) { Console.WriteLine("OpenProcess failed"); return; }
        var regions = WxKeyFinder.EnumRegionsForProbe(h);
        Console.WriteLine($"regions={regions.Count} totalMB={regions.Sum(r => r.size) / 1024 / 1024}");
        var targets = dbs.Take(3).ToList();
        var counts = new int[targets.Count];
        var dumps = new List<byte[]>[targets.Count];
        var dumpAddrs = new List<long>[targets.Count];
        for (int i = 0; i < targets.Count; i++) { dumps[i] = new(); dumpAddrs[i] = new(); }

        foreach (var (baseAddr, size, type) in regions)
        {
            if (type != 0x20000 && type != 0x40000) continue;
            int chunk = 4 * 1024 * 1024, overlap = 4096;
            var buf = new byte[chunk];
            for (long off = 0; off < size; off += chunk - overlap)
            {
                int len = (int)Math.Min(chunk, size - off);
                if (len < 64) break;
                if (!ReadProcessMemory(h, baseAddr + off, buf, (IntPtr)len, out var read) || read.ToInt64() < 64) break;
                len = (int)read.ToInt64();
                for (int si = 0; si < targets.Count; si++)
                {
                    var salt = Convert.FromHexString(targets[si].saltHex);
                    int idx = 0;
                    while ((idx = WxKeyFinder.Index(buf, salt, idx)) >= 0)
                    {
                        counts[si]++;
                        if (dumps[si].Count < 2)
                        {
                            int s = Math.Max(0, idx - 320);
                            int e = Math.Min(len, idx + 352);
                            var seg = new byte[e - s];
                            Array.Copy(buf, s, seg, 0, e - s);
                            dumps[si].Add(seg);
                            dumpAddrs[si].Add(baseAddr + off + s);
                        }
                        idx += 1;
                    }
                }
            }
        }
        CloseHandle(h);
        for (int i = 0; i < targets.Count; i++)
        {
            Console.WriteLine($"[{targets[i].saltHex[..12]}…] 命中 {counts[i]} 次");
            for (int d = 0; d < dumps[i].Count; d++)
            {
                Console.WriteLine($"--- dump#{d} @0x{dumpAddrs[i][d]:X} ---");
                var seg = dumps[i][d];
                for (int r = 0; r < seg.Length; r += 32)
                {
                    Console.Write($"{r:X4}  ");
                    for (int c = 0; c < 32 && r + c < seg.Length; c++) Console.Write($"{seg[r + c]:x2}");
                    Console.WriteLine();
                }
            }
        }
    }
}

// 扩展探针：搜索 codec 配置结构特征（MMV1 魔数 / 256000 迭代参数）
public static class Probe2
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr h, long addr, byte[] buf, IntPtr size, out IntPtr read);

    public static void Run(int pid)
    {
        IntPtr h = OpenProcess(0x0400 | 0x0010, false, pid);
        if (h == IntPtr.Zero) { Console.WriteLine("OpenProcess failed"); return; }
        var mmv1 = System.Text.Encoding.ASCII.GetBytes("MMV1");
        var kdf = BitConverter.GetBytes(256000);
        int mmv1Hits = 0, kdfHits = 0;
        var dumps = new List<(long addr, byte[] seg)>();

        foreach (var (baseAddr, size, type) in WxKeyFinder.EnumRegionsForProbe(h))
        {
            if (type != 0x20000) continue;
            int chunk = 8 * 1024 * 1024, overlap = 4096;
            var buf = new byte[chunk];
            for (long off = 0; off < size; off += chunk - overlap)
            {
                int len = (int)Math.Min(chunk, size - off);
                if (len < 64) break;
                if (!ReadProcessMemory(h, baseAddr + off, buf, (IntPtr)len, out var read) || read.ToInt64() < 64) break;
                len = (int)read.ToInt64();
                int idx = 0;
                while ((idx = WxKeyFinder.Index(buf, mmv1, idx)) >= 0)
                {
                    mmv1Hits++;
                    if (dumps.Count < 6)
                    {
                        int s = Math.Max(0, idx - 384);
                        int e = Math.Min(len, idx + 448);
                        var seg = new byte[e - s];
                        Array.Copy(buf, s, seg, 0, e - s);
                        dumps.Add((baseAddr + off + s, seg));
                    }
                    idx += 4;
                }
                idx = 0;
                while ((idx = WxKeyFinder.Index(buf, kdf, idx)) >= 0)
                {
                    kdfHits++;
                    // 配置结构特征：256000 附近 ±192B 内还有 4096（page size）
                    bool near4096 = false;
                    var psz = BitConverter.GetBytes(4096);
                    int s0 = Math.Max(0, idx - 192), e0 = Math.Min(len, idx + 192 + 4);
                    int t = s0;
                    while ((t = WxKeyFinder.Index(buf, psz, t)) >= 0 && t < e0) { near4096 = true; break; }
                    if (near4096 && dumps.Count < 10)
                    {
                        int s = Math.Max(0, idx - 448);
                        int e = Math.Min(len, idx + 512);
                        var seg = new byte[e - s];
                        Array.Copy(buf, s, seg, 0, e - s);
                        dumps.Add((baseAddr + off + s, seg));
                    }
                    idx += 4;
                }
            }
        }
        CloseHandle(h);
        Console.WriteLine($"MMV1 命中 {mmv1Hits}，256000 命中 {kdfHits}");
        foreach (var (addr, seg) in dumps)
        {
            Console.WriteLine($"--- MMV1 dump @0x{addr:X} ---");
            for (int r = 0; r < seg.Length; r += 32)
            {
                Console.Write($"{r:X4}  ");
                for (int c = 0; c < 32 && r + c < seg.Length; c++) Console.Write($"{seg[r + c]:x2}");
                Console.WriteLine();
            }
        }
    }
}

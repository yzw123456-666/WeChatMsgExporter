using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;

namespace WeChatMsgExporter;

// 探针4：定位 cipher 配置结构（256000+4096），顺着结构体内的堆指针读取 password 候选，
// 用 PBKDF2-SHA512(password, 库盐值, 256000) 派生并 HMAC 验证
public static class Probe4
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
        int cfgHits = 0, dumps = 0;
        bool loggedFirst = false;
        var (saltHex0, page10) = dbs[0];
        var salt0 = Convert.FromHexString(saltHex0);
        int verified = 0;
        var verifiedSet = new HashSet<string>();

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 区域枚举开始");
        Console.Out.Flush();
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
                for (int o = 0; o + 4 <= len; o += 4)
                {
                    if (buf[o] != 0 || buf[o + 1] != 0xE8 || buf[o + 2] != 3 || buf[o + 3] != 0) continue;   // 256000
                    bool near4096 = false;
                    for (int t = Math.Max(0, o - 256); t + 4 <= Math.Min(len, o + 260); t += 4)
                        if (buf[t] == 0 && buf[t + 1] == 0x10 && buf[t + 2] == 0 && buf[t + 3] == 0) { near4096 = true; break; }
                    if (!near4096) continue;
                    if (!loggedFirst) { loggedFirst = true; cfgHits++; Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 首个配置结构 @0x{baseAddr + off + o:X}"); Console.Out.Flush(); }

                    // 收集 ±512B 内的堆指针并逐个验证
                    for (int p = Math.Max(0, o - 512); p + 8 <= Math.Min(len, o + 640); p += 8)
                    {
                        ulong ptr = BitConverter.ToUInt64(buf, p);
                        if (ptr < 0x10000 || ptr > 0x7FFFFFFFFFFF) continue;
                        if (buf[p + 6] != 0 || buf[p + 7] != 0) continue;
                        var target = (long)ptr;
                        foreach (int skip in new[] { 0, 8 })
                        {
                            var cand = new byte[32];
                            if (!ReadProcessMemory(h, target + skip, cand, (IntPtr)32, out _)) continue;
                            if (cand.All(b => b == 0)) continue;
                            var sig = Convert.ToHexString(cand);
                            if (!verifiedSet.Add(sig)) continue;
                            verified++;
                            if (verified % 50 == 0) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] KDF 已验证 {verified}"); Console.Out.Flush(); }
                            try
                            {
                                var encKey = Rfc2898(cand, salt0, 256000);
                                if (WxDbCrypto.VerifyV4(encKey, page10))
                                {
                                    Console.WriteLine($"*** PASSWORD 找到! @0x{target + skip:X} = {sig}");
                                    File.WriteAllText(Path.Combine(AppPaths.Root, "password_42.txt"), sig);
                                    Console.WriteLine($"已保存到 {Path.Combine(AppPaths.Root, "password_42.txt")}，开始验证全部库...");
                                    VerifyAllDbs(dbs, cand);
                                    CloseHandle(h);
                                    return;
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
        }
        CloseHandle(h);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] KDF 验证 {verified} 个唯一候选，未命中");
    }

    static void VerifyAllDbs(List<(string saltHex, byte[] page1)> dbs, byte[] password)
    {
        int ok = 0;
        foreach (var (saltHex, page1) in dbs)
        {
            try
            {
                var encKey = Rfc2898(password, Convert.FromHexString(saltHex), 256000);
                if (WxDbCrypto.VerifyV4(encKey, page1)) ok++;
            }
            catch { }
        }
        Console.WriteLine($"password 对全部 {dbs.Count} 库的 KDF+HMAC 验证：{ok} 通过");
    }

    static byte[] Rfc2898(byte[] pw, byte[] salt, int iter) =>
        Rfc2898DeriveBytes.Pbkdf2(pw, salt, iter, HashAlgorithmName.SHA512, 32);
}

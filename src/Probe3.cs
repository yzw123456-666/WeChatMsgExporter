using System.Runtime.InteropServices;
using System.Text;

namespace WeChatMsgExporter;

// 探针3：精确定位 cipher 配置结构（4对齐的 256000 + 相邻 4096），收集周边 64hex 字符串并用 KDF 验证
public static class Probe3
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
        var kdf = BitConverter.GetBytes(256000);
        var psz = BitConverter.GetBytes(4096);
        var hexStrings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int cfgHits = 0, dumps = 0;

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
                // 4 对齐扫描 256000
                for (int o = 0; o + 4 <= len; o += 4)
                {
                    if (buf[o] != 0 || buf[o + 1] != 0xE8 || buf[o + 2] != 3 || buf[o + 3] != 0) continue;
                    // ±256B 内有 4096？
                    bool near4096 = false;
                    for (int t = Math.Max(0, o - 256); t + 4 <= Math.Min(len, o + 260); t += 4)
                        if (buf[t] == 0 && buf[t + 1] == 0x10 && buf[t + 2] == 0 && buf[t + 3] == 0) { near4096 = true; break; }
                    if (!near4096) continue;
                    cfgHits++;
                    if (dumps < 12)
                    {
                        dumps++;
                        int s = Math.Max(0, o - 512);
                        int e = Math.Min(len, o + 640);
                        // 收集邻域内所有 64hex 字符串
                        var text = Encoding.ASCII.GetString(buf, s, e - s);
                        foreach (System.Text.RegularExpressions.Match m in
                            System.Text.RegularExpressions.Regex.Matches(text, "[0-9a-fA-F]{64}"))
                            hexStrings.Add(m.Value);
                        Console.WriteLine($"--- cfg @0x{baseAddr + off + o:X} ---");
                        for (int r = 0; r < e - s; r += 32)
                        {
                            Console.Write($"{baseAddr + s + r:X12}  ");
                            for (int c = 0; c < 32 && s + r + c < e; c++) Console.Write($"{buf[s + r + c]:x2}");
                            Console.WriteLine();
                        }
                    }
                }
            }
        }
        CloseHandle(h);
        Console.WriteLine($"配置结构命中 {cfgHits}，收集 64hex 字符串 {hexStrings.Count} 个");

        // KDF 验证：password(hex字符串原文) → PBKDF2-SHA512 256000 → enc_key → HMAC
        if (dbs.Count == 0) return;
        var (saltHex, page1) = dbs[0];
        var salt = Convert.FromHexString(saltHex);
        int verified = 0;
        foreach (var hex in hexStrings)
        {
            foreach (var pw in new[] { Encoding.ASCII.GetBytes(hex), Convert.FromHexString(hex) })
            {
                verified++;
                try
                {
                    var encKey = Rfc2898(pw, salt, 256000);
                    if (WxDbCrypto.VerifyV4(encKey, page1))
                    {
                        Console.WriteLine($"*** PASSWORD 找到! (hex原文) {hex}");
                        File.WriteAllText(Path.Combine(AppPaths.Root, "password_42.txt"), hex);
                        return;
                    }
                    // password 变体：反序
                    var rv = (byte[])pw.Clone(); Array.Reverse(rv);
                    var ek2 = Rfc2898(rv, salt, 256000);
                    if (WxDbCrypto.VerifyV4(ek2, page1)) { Console.WriteLine($"*** PASSWORD 找到! (反序) {hex}"); return; }
                }
                catch { }
            }
        }
        Console.WriteLine($"KDF 验证 {verified} 次完成，未命中");
    }

    static byte[] Rfc2898(byte[] pw, byte[] salt, int iter) =>
        System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(pw, salt, iter, System.Security.Cryptography.HashAlgorithmName.SHA512, 32);
}

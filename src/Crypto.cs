using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WeChatMsgExporter;

// ============ 微信数据库加密/解密 ============
//
// v3 (WeChat 3.x, 微信官方定制 SQLCipher):
//   页大小 4096；首部 16 字节盐值；派生 AES 密钥 = PBKDF2-HMAC-SHA1(key, salt, 64000, 32)
//   macSalt = salt ^ 0x3A；macKey = PBKDF2-HMAC-SHA1(aesKey, macSalt, 2, 32)
//   页尾 48 字节保留段 = [IV 16][HMAC-SHA1 20][填充 12]
//   HMAC 覆盖: 密文 || IV || 页号(4字节小端)
//
// v4 (WeChat 4.x / Weixin.exe, WCDB):
//   页大小 4096；每库独立 enc_key 直接作 AES-256 密钥（不再做 64000 轮派生）
//   macKey = PBKDF2-HMAC-SHA512(enc_key, salt^0x3A, 2, 32)
//   页尾 80 字节保留段 = [IV 16][HMAC-SHA512 64]
//   HMAC 覆盖: 密文 || IV || 页号(4字节小端)
//   首页解密后需还原 "SQLite format 3\0" 文件头

public static class WxDbCrypto
{
    public const int PageSz = 4096;
    public const string SqliteHeader = "SQLite format 3\0";
    static readonly byte[] SqliteHdrBytes = Encoding.ASCII.GetBytes(SqliteHeader);

    public enum DbFormat { Unknown, Plain, V3, V4 }

    // ---------- 格式探测 ----------

    public static DbFormat Detect(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < PageSz) return DbFormat.Unknown;
            Span<byte> head = stackalloc byte[16];
            fs.ReadExactly(head);
            if (head.SequenceEqual(SqliteHdrBytes)) return DbFormat.Plain;
            // v3 与 v4 均为 16 字节盐值开头，无法从头部直接区分，用验证函数区分
            return DbFormat.Unknown; // 调用方用 TryVerify 区分 v3/v4
        }
        catch { return DbFormat.Unknown; }
    }

    public static byte[] ReadPage1(string path)
    {
        using var fs = OpenReadShared(path);
        var buf = new byte[PageSz];
        int n = fs.Read(buf, 0, PageSz);
        if (n < PageSz) throw new IOException("文件过小: " + path);
        return buf;
    }

    /// <summary>共享读取（微信以独占方式持有数据库文件句柄）</summary>
    public static byte[] ReadAllShared(string path)
    {
        using var fs = OpenReadShared(path);
        using var ms = new MemoryStream((int)fs.Length);
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    public static FileStream OpenReadShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    // ---------- v3 验证 ----------

    public static bool VerifyV3(byte[] key, byte[] page1)
    {
        try
        {
            var salt = page1.AsSpan(0, 16).ToArray();
            var macSalt = Xor3A(salt);
            var aesKey = Rfc2898DeriveBytes.Pbkdf2(key, salt, 64000, HashAlgorithmName.SHA1, 32);
            var macKey = Rfc2898DeriveBytes.Pbkdf2(aesKey, macSalt, 2, HashAlgorithmName.SHA1, 32);
            using var h = new HMACSHA1(macKey);
            h.TransformBlock(page1, 16, 4048, null, 0);       // 密文+IV: [16:4064]
            h.TransformFinalBlock(new byte[] { 1, 0, 0, 0 }, 0, 4);
            var want = page1.AsSpan(4064, 20).ToArray();      // first[-32:-12]
            return h.Hash.AsSpan().SequenceEqual(want);
        }
        catch { return false; }
    }

    // ---------- v4 验证 ----------

    public static bool VerifyV4(byte[] encKey, byte[] page1) => VerifyV4Auto(encKey, page1, 4016);

    /// <summary>验证（保留段布局可变：IV 位于 ivOff，HMAC 紧随其后占 64 字节）</summary>
    public static bool VerifyV4Auto(byte[] encKey, byte[] page1, int ivOff)
    {
        try
        {
            int ctLen = ivOff - 16;                       // 页1密文长度
            if (ctLen % 16 != 0 || ivOff + 16 + 64 > PageSz) return false;
            var salt = page1.AsSpan(0, 16).ToArray();
            var macSalt = Xor3A(salt);
            var macKey = Rfc2898DeriveBytes.Pbkdf2(encKey, macSalt, 2, HashAlgorithmName.SHA512, 32);
            using var h = new HMACSHA512(macKey);
            h.TransformBlock(page1, 16, ctLen + 16, null, 0);   // 密文+IV
            h.TransformFinalBlock(new byte[] { 1, 0, 0, 0 }, 0, 4);
            var want = page1.AsSpan(ivOff + 16, 64).ToArray();
            return h.Hash.AsSpan().SequenceEqual(want);
        }
        catch { return false; }
    }

    // 尝试用密钥解密（自动识别 v3 / v4）
    public static (bool ok, string fmt) TryKey(string keyHex, string dbPath)
    {
        if (string.IsNullOrWhiteSpace(keyHex) || keyHex.Replace(" ", "").Length != 64) return (false, "");
        byte[] key;
        try { key = Convert.FromHexString(keyHex.Replace(" ", "")); }
        catch { return (false, ""); }
        byte[] page1;
        try { page1 = ReadPage1(dbPath); } catch { return (false, ""); }
        if (VerifyV3(key, page1)) return (true, "v3");
        if (VerifyV4(key, page1)) return (true, "v4");
        return (false, "");
    }

    // ---------- v3 解密（完整文件） ----------

    public static bool DecryptV3(byte[] key, string dbPath, string outPath, Action<int, int> progress = null)
    {
        var blist = WxDbCrypto.ReadAllShared(dbPath);
        if (blist.Length < PageSz) return false;
        var salt = blist.AsSpan(0, 16).ToArray();
        var aesKey = Rfc2898DeriveBytes.Pbkdf2(key, salt, 64000, HashAlgorithmName.SHA1, 32);

        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
        using var aes = Aes.Create();
        aes.Key = aesKey; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None;

        int pages = blist.Length / PageSz;
        for (int i = 0; i < pages; i++)
        {
            int off = i * PageSz;
            byte[] page;
            if (i == 0)
            {
                // [16盐][4032密文][16IV][20HMAC][12填充]
                var iv = blist.AsSpan(4048, 16).ToArray();
                fs.Write(SqliteHdrBytes, 0, 16);
                page = aes.DecryptCbc(blist.AsSpan(16, 4032).ToArray(), iv, PaddingMode.None);
                fs.Write(page, 0, page.Length);
                fs.Write(blist, 4048, 48);
            }
            else
            {
                var iv = blist.AsSpan(off + 4048, 16).ToArray();
                page = aes.DecryptCbc(blist.AsSpan(off, 4048).ToArray(), iv, PaddingMode.None);
                fs.Write(page, 0, page.Length);
                fs.Write(blist, off + 4048, 48);
            }
            if (progress != null && (i & 0x3F) == 0) progress(i, pages);
        }
        return true;
    }

    // ---------- v4 解密（完整文件） ----------

    public static bool DecryptV4(byte[] encKey, string dbPath, string outPath, Action<int, int> progress = null)
        => DecryptV4Auto(encKey, dbPath, outPath, 4016, progress);

    /// <summary>解密（自动探测保留段布局：IV 位置 + 页头第 20 字节的保留字节数）</summary>
    public static bool DecryptV4Auto(byte[] encKey, string dbPath, string outPath, int hintIvOff, Action<int, int> progress = null)
    {
        var blist = ReadAllShared(dbPath);
        if (blist.Length < PageSz) return false;

        int[] ivOffs = { hintIvOff, 4016, 4048, 4032, 4080, 4000, 3984 };
        int ivOff = -1, reserve = 0;
        foreach (var off in ivOffs)
        {
            if (off < 16 || off + 80 > PageSz || (off - 16) % 16 != 0) continue;
            int R = PageSz - off;
            try
            {
                var iv = blist.AsSpan(off, 16).ToArray();
                using var aes0 = Aes.Create();
                aes0.Key = encKey; aes0.Mode = CipherMode.CBC; aes0.Padding = PaddingMode.None;
                var dec = aes0.DecryptCbc(blist.AsSpan(16, off - 16).ToArray(), iv, PaddingMode.None);
                // sqlite 头（自明文16字节起）: dec[2]=写版本1, dec[3]=读版本1, dec[4]=保留数R, dec[5..7]=64/32/32
                if (dec[2] == 1 && dec[3] == 1 && dec[4] == R && dec[5] == 64 && dec[6] == 32 && dec[7] == 32)
                { ivOff = off; reserve = R; break; }
            }
            catch { }
        }
        if (ivOff < 0) return false;

        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
        using var aes = Aes.Create();
        aes.Key = encKey; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None;

        int pages = (int)Math.Ceiling(blist.Length / (double)PageSz);
        for (int i = 0; i < pages; i++)
        {
            int off = i * PageSz;
            int len = Math.Min(PageSz, blist.Length - off);
            var page = new byte[PageSz];
            Array.Copy(blist, off, page, 0, len);

            var iv = page.AsSpan(ivOff, 16).ToArray();
            int ctLen = ivOff - (i == 0 ? 16 : 0);
            if (i == 0) fs.Write(SqliteHdrBytes, 0, 16);
            var dec = aes.DecryptCbc(page.AsSpan(i == 0 ? 16 : 0, ctLen).ToArray(), iv, PaddingMode.None);
            fs.Write(dec, 0, dec.Length);
            fs.Write(new byte[reserve], 0, reserve);   // 保留段置零
            if (progress != null && (i & 0x3F) == 0) progress(i, pages);
        }
        return true;
    }

    // ---------- 加密（仅用于自测往返验证） ----------

    public static byte[] EncryptV3(byte[] key, byte[] plain)
    {
        if (plain.Length % PageSz != 0) throw new ArgumentException("明文需为 4096 的倍数");
        var rnd = RandomNumberGenerator.GetBytes(16);
        var aesKey = Rfc2898DeriveBytes.Pbkdf2(key, rnd, 64000, HashAlgorithmName.SHA1, 32);
        var macSalt = Xor3A(rnd);
        var macKey = Rfc2898DeriveBytes.Pbkdf2(aesKey, macSalt, 2, HashAlgorithmName.SHA1, 32);
        using var aes = Aes.Create();
        aes.Key = aesKey; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None;

        var outBuf = new MemoryStream();
        int pages = plain.Length / PageSz;
        for (int i = 0; i < pages; i++)
        {
            int off = i * PageSz;
            byte[] data, iv;
            if (i == 0)
            {
                outBuf.Write(rnd, 0, 16);
                data = plain.AsSpan(16, 4032).ToArray();
            }
            else data = plain.AsSpan(off, 4048).ToArray();
            iv = RandomNumberGenerator.GetBytes(16);
            var ct = aes.EncryptCbc(data, iv, PaddingMode.None);
            if (i == 0) outBuf.Write(ct, 0, ct.Length); else outBuf.Write(ct, 0, ct.Length);
            outBuf.Write(iv, 0, 16);
            var pgno = BitConverter.GetBytes(i + 1);   // 小端页号
            var combined = new byte[ct.Length + 20];   // 密文 || IV || 页号(LE)
            Array.Copy(ct, combined, ct.Length);
            Array.Copy(iv, 0, combined, ct.Length, 16);
            Array.Copy(pgno, 0, combined, ct.Length + 16, 4);
            using var h2 = new HMACSHA1(macKey);
            var mac = h2.ComputeHash(combined);
            outBuf.Write(mac, 0, 20);
            outBuf.Write(new byte[12], 0, 12);
        }
        return outBuf.ToArray();
    }

    public static byte[] EncryptV4(byte[] encKey, byte[] plain)
    {
        if (plain.Length % PageSz != 0) throw new ArgumentException("明文需为 4096 的倍数");
        var rnd = RandomNumberGenerator.GetBytes(16);
        var macSalt = Xor3A(rnd);
        var macKey = Rfc2898DeriveBytes.Pbkdf2(encKey, macSalt, 2, HashAlgorithmName.SHA512, 32);
        using var aes = Aes.Create();
        aes.Key = encKey; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None;

        var outBuf = new MemoryStream();
        int pages = plain.Length / PageSz;
        for (int i = 0; i < pages; i++)
        {
            int off = i * PageSz;
            if (i == 0) outBuf.Write(rnd, 0, 16);
            var data = plain.AsSpan(off + (i == 0 ? 16 : 0), i == 0 ? 4000 : 4016).ToArray();
            var iv = RandomNumberGenerator.GetBytes(16);
            var ct = aes.EncryptCbc(data, iv, PaddingMode.None);
            outBuf.Write(ct, 0, ct.Length);
            outBuf.Write(iv, 0, 16);
            var pgno = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(pgno, i + 1);
            var combined = new byte[ct.Length + 20];   // 密文 || IV || 页号(LE)
            Array.Copy(ct, combined, ct.Length);
            Array.Copy(iv, 0, combined, ct.Length, 16);
            Array.Copy(pgno, 0, combined, ct.Length + 16, 4);
            using var h = new HMACSHA512(macKey);
            var mac = h.ComputeHash(combined);
            outBuf.Write(mac, 0, 64);
        }
        return outBuf.ToArray();
    }

    static byte[] Xor3A(byte[] salt)
    {
        var r = new byte[salt.Length];
        for (int i = 0; i < salt.Length; i++) r[i] = (byte)(salt[i] ^ 0x3A);
        return r;
    }
}

// ============ 微信图片 .dat 解码 ============
//
// v3 旧格式: 整文件单字节 XOR，头部与已知图片 magic 对比可推出 XOR key
// v4 V1 格式: [07 08 V1 08 07][aes_size][xor_size][pad] AES-128-ECB(固定key cfcd208495d565ef) + XOR(0x88)
// v4 V2 格式: 同上结构，AES key 为 16 字节 ASCII，存于微信进程内存

public static class DatImage
{
    static readonly Dictionary<string, byte[]> Magics = new()
    {
        ["png"] = new byte[] { 0x89, 0x50, 0x4E, 0x47 },
        ["gif"] = new byte[] { 0x47, 0x49, 0x46, 0x38 },
        ["tif"] = new byte[] { 0x49, 0x49, 0x2A, 0x00 },
        ["webp"] = new byte[] { 0x52, 0x49, 0x46, 0x46 },
        ["jpg"] = new byte[] { 0xFF, 0xD8, 0xFF },
    };

    public static string DetectXorKey(byte[] header)
    {
        if (header == null || header.Length < 4) return null;
        if (header[0] == 0x07 && header[1] == 0x08) return null; // V1/V2 格式
        foreach (var kv in Magics)
        {
            byte key = (byte)(header[0] ^ kv.Value[0]);
            bool ok = true;
            for (int i = 1; i < kv.Value.Length && i < header.Length; i++)
                if ((byte)(header[i] ^ key) != kv.Value[i]) { ok = false; break; }
            if (ok) return key.ToString("x2");
        }
        return null;
    }

    // 返回 (解码数据, 扩展名)；失败返回 (null, null)
    public static (byte[], string) Decode(string datPath, string v2AesKey = null)
    {
        try
        {
            var data = WxDbCrypto.ReadAllShared(datPath);
            if (data.Length < 16) return (null, null);

            // V1 / V2
            if (data[0] == 0x07 && data[1] == 0x08)
            {
                if (data.Length >= 15 && data[2] == (byte)'V' && (data[3] == (byte)'1' || data[3] == (byte)'2'))
                {
                    uint aesSize = BitConverter.ToUInt32(data, 6);
                    uint xorSize = BitConverter.ToUInt32(data, 10);
                    byte[] aesKey;
                    if (data[3] == (byte)'1') aesKey = Encoding.ASCII.GetBytes("cfcd208495d565ef");
                    else if (!string.IsNullOrEmpty(v2AesKey)) aesKey = Encoding.ASCII.GetBytes(v2AesKey.PadRight(16, '0')[..16]);
                    else return (null, null);

                    long aligned = (aesSize / 16 + 1) * 16;
                    if (15 + aligned > data.Length) return (null, null);
                    using var aes = Aes.Create();
                    aes.Key = aesKey; aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.PKCS7;
                    var decAes = aes.DecryptEcb(data.AsSpan(15, (int)aligned).ToArray(), PaddingMode.PKCS7);
                    long rawEnd = data.Length - xorSize;
                    var raw = offset(data, 15 + aligned, Math.Max(0, rawEnd - (15 + aligned)));
                    var xorPart = offset(data, Math.Max(0, rawEnd), data.Length - Math.Max(0, (int)rawEnd));
                    var outBuf = new MemoryStream();
                    outBuf.Write(decAes, 0, decAes.Length);
                    if (raw != null) outBuf.Write(raw, 0, raw.Length);
                    foreach (var b in xorPart) outBuf.Write([(byte)(b ^ 0x88)]);
                    var head = outBuf.ToArray();
                    return (head, DetectFormat(head));
                }
                return (null, null);
            }

            // 旧 XOR 格式
            var keyHex = DetectXorKey(data);
            if (keyHex == null) return (null, null);
            byte k = Convert.ToByte(keyHex, 16);
            var res = new byte[data.Length];
            for (int i = 0; i < data.Length; i++) res[i] = (byte)(data[i] ^ k);
            return (res, DetectFormat(res));
        }
        catch { return (null, null); }
    }

    static byte[] offset(byte[] src, long start, long len)
    {
        if (start >= src.Length || len <= 0) return Array.Empty<byte>();
        len = Math.Min(len, src.Length - start);
        var r = new byte[len];
        Array.Copy(src, start, r, 0, len);
        return r;
    }

    public static string DetectFormat(byte[] head)
    {
        if (head == null || head.Length < 4) return "bin";
        if (head[0] == 0xFF && head[1] == 0xD8) return "jpg";
        if (head[0] == 0x89 && head[1] == 0x50) return "png";
        if (head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46) return "gif";
        if (head[0] == 0x42 && head[1] == 0x4D) return "bmp";
        if (head[0] == 0x52 && head[1] == 0x49 && head[8] == 0x57 && head[9] == 0x45) return "webp";
        return "bin";
    }
}

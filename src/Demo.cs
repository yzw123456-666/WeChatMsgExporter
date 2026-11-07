using System.Text;
using Microsoft.Data.Sqlite;

namespace WeChatMsgExporter;

public static class AppPaths
{
    public static string Root
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeChatMsgExporter");
            Directory.CreateDirectory(d);
            return d;
        }
    }
    public static string Workspace(string tag = null)
    {
        var d = Path.Combine(Root, "workspace", tag ?? "default");
        Directory.CreateDirectory(d);
        return d;
    }
    public static string Cache => Path.Combine(Root, "cache");
    public static string DemoDir => Path.Combine(Root, "demo");
    public static string DemoDataDir => Path.Combine(DemoDir, "v3");
    public static string DemoKeyFile => Path.Combine(DemoDir, "key.txt");
}

public static class Demo
{
    public const string DemoKeyHex = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";

    /// <summary>生成演示数据（加密 v3 格式），返回数据目录。已存在则直接返回。</summary>
    public static (string dir, string keyHex) EnsureDemoData()
    {
        var dir = AppPaths.DemoDataDir;
        var flag = Path.Combine(dir, ".ok");
        if (File.Exists(flag) && File.Exists(AppPaths.DemoKeyFile)) return (dir, File.ReadAllText(AppPaths.DemoKeyFile));

        Directory.CreateDirectory(dir);
        var key = Convert.FromHexString(DemoKeyHex);

        // ---- 微信目录结构 ----
        var wxid = "wxid_demo_self000";
        var wxDir = Path.Combine(dir, "WeChat Files", wxid);
        var msgDir = Path.Combine(wxDir, "Msg");
        var fsDir = Path.Combine(wxDir, "FileStorage");
        Directory.CreateDirectory(msgDir);
        Directory.CreateDirectory(Path.Combine(fsDir, "Image", "2025-12", "Image"));
        Directory.CreateDirectory(Path.Combine(fsDir, "Image", "2025-12", "THUMBNAIL"));

        var contacts = new List<(string wxid, string nick, string remark)>
        {
            (wxid, "演示用户", ""),
            ("wxid_demo_zhang", "张三", "老张"),
            ("wxid_demo_li", "李四", ""),
            ("wxid_demo_wang", "小王", ""),
            ("filehelper", "文件传输助手", ""),
            ("gh_demo_official", "微信团队", ""),
        };
        var room = "12345678@chatroom";
        contacts.Add((room, "产品讨论组", "家庭群"));

        // MicroMsg.db
        using (var db = NewDb(Path.Combine(msgDir, "MicroMsg.db")))
        {
            Exec(db, "CREATE TABLE Contact(UserName TEXT, Alias TEXT, NickName TEXT, Remark TEXT, Type INTEGER, VerifyFlag INTEGER)");
            foreach (var (u, n, r) in contacts)
                Exec(db, $"INSERT INTO Contact VALUES('{u}','','{n}','{r}',{(u == room ? 2u : 1u)},0)");
            Exec(db, "CREATE TABLE Session(strUsrName TEXT, nOrder INTEGER, nUnReadCount INTEGER, strNickName TEXT, nStatus INTEGER, nIsSend INTEGER, strContent TEXT, nMsgType INTEGER, nTime INTEGER)");
        }

        // ChatRoom.db（群成员显示名）
        using (var db = NewDb(Path.Combine(msgDir, "ChatRoom.db")))
        {
            Exec(db, "CREATE TABLE ChatRoom(ChatRoomName TEXT, RoomData BLOB)");
            var memberBytes = BuildRoomData(new[] { (wxid, "我"), ("wxid_demo_zhang", "张三"), ("wxid_demo_li", "李四"), ("wxid_demo_wang", "小王") });
            Exec(db, $"INSERT INTO ChatRoom VALUES('{room}', @b)", ("@b", memberBytes));
        }

        // MSG0.db
        long now = DateTimeOffset.Now.ToUnixTimeSeconds();
        long baseT = now - 3600 * 26;
        var msgs = new List<(string talker, int tid, int type, int sub, int isSender, long time, string content, string sender, string extraImg)>();
        var demoPngs = new List<byte[]>();

        // 1) 张三 私聊
        var zhang = "wxid_demo_zhang";
        msgs.Add((zhang, 2, 1, 0, 0, baseT, "在吗？帮我看看这个图 👇", "", ""));
        var png1 = MakePng("Demo 图片 A", "#5B84C4");
        demoPngs.Add(png1);
        msgs.Add((zhang, 2, 3, 0, 1, baseT + 60, "<msg><img aeskey=\"demo\" encryver=\"1\"/></msg>", "", "THUMBNAIL_DIRPATH://th_demo0001"));
        msgs.Add((zhang, 2, 1, 0, 0, baseT + 180, "这是软件生成的演示图片，检查显示效果", "", ""));
        msgs.Add((zhang, 2, 1, 0, 1, baseT + 240, "收到，看起来不错！", "", ""));
        msgs.Add((zhang, 2, 49, 57, 1, baseT + 300,
            "<msg><appmsg appid=\"\" sdkver=\"0\"><title>引用了演示消息</title><type>57</type><refermsg><displayname>张三</displayname><content>这是软件生成的演示图片，检查显示效果</content></refermsg></appmsg></msg>", "", ""));
        msgs.Add((zhang, 2, 34, 0, 0, baseT + 420, "<msg><voicemsg voicelength=\"4200\"/></msg>", "", ""));

        // 2) 群聊
        long t2 = baseT + 3600 * 3;
        msgs.Add((room, 5, 10000, 0, 0, t2, "你邀请小王加入了群聊", "", ""));
        msgs.Add((room, 5, 1, 0, 0, t2 + 30, "大家好，测试群聊显示", "wxid_demo_zhang", ""));
        msgs.Add((room, 5, 1, 0, 0, t2 + 60, "收到~", "wxid_demo_li", ""));
        var png2 = MakePng("群聊截图 Demo", "#7EB050");
        demoPngs.Add(png2);
        msgs.Add((room, 5, 3, 0, 0, t2 + 90, "<msg><img aeskey=\"demo\" encryver=\"1\"/></msg>", "wxid_demo_wang", "THUMBNAIL_DIRPATH://th_demo0002"));
        msgs.Add((room, 5, 49, 5, 0, t2 + 200,
            "<msg><appmsg appid=\"\" sdkver=\"0\"><title>一个演示链接卡片</title><des>这是消息卡片描述内容</des><type>5</type><url>https://example.com</url></appmsg></msg>", "wxid_demo_zhang", ""));
        msgs.Add((room, 5, 43, 0, 0, t2 + 300, "<msg><videomsg cdnvideourl=\"demo\"/></msg>", "wxid_demo_li", "demo_video001"));

        // 3) 文件传输助手
        long t3 = baseT + 3600 * 8;
        msgs.Add(("filehelper", 6, 1, 0, 1, t3, "备忘：演示导出功能", "", ""));
        msgs.Add(("filehelper", 6, 48, 0, 1, t3 + 30,
            "<msg><location label=\"北京市朝阳区 xx 大厦\" poiname=\"演示大厦\"/></msg>", "", ""));

        // 4) 公众号
        long t4 = baseT + 3600 * 20;
        msgs.Add(("gh_demo_official", 7, 1, 0, 0, t4, "欢迎使用微信聊天记录导出器演示数据", "", ""));

        // 写 MSG0.db
        var name2id = new List<(long, string)> { (1, wxid), (2, zhang), (3, "wxid_demo_li"), (4, "wxid_demo_wang"), (5, room), (6, "filehelper"), (7, "gh_demo_official") };
        using (var db = NewDb(Path.Combine(msgDir, "MSG0.db")))
        {
            Exec(db, "CREATE TABLE Name2ID(Id INTEGER, UsrName TEXT)");
            foreach (var (i, n) in name2id) Exec(db, $"INSERT INTO Name2ID VALUES({i},'{n}')");
            Exec(db, @"CREATE TABLE MSG(localId INTEGER, TalkerId INTEGER, MsgSvrID INTEGER, Type INTEGER, SubType INTEGER,
                       IsSender INTEGER, CreateTime INTEGER, Sequence INTEGER, StatusEx INTEGER, FlagEx INTEGER, Status INTEGER,
                       MsgServerSeq INTEGER, NewMsgId INTEGER, StrContent TEXT, CompressContent BLOB, BytesExtra BLOB)");
            long lid = 0;
            foreach (var (talker, tid, type, sub, isSender, time, content, sender, extraImg) in msgs)
            {
                lid++;
                var extra = string.IsNullOrEmpty(sender)
                    ? (extraImg != "" ? BuildExtraImg(extraImg) : Array.Empty<byte>())
                    : BuildExtra(sender, extraImg);
                Exec(db, "INSERT INTO MSG VALUES(@a,@b,@c,@d,@e,@f,@g,@h,0,0,0,0,@i,@j,NULL,@k)",
                    ("@a", (object)lid), ("@b", (object)tid), ("@c", (object)(700000000000L + lid)), ("@d", (object)type),
                    ("@e", (object)sub), ("@f", (object)isSender), ("@g", (object)time), ("@h", (object)(lid * 1000)),
                    ("@i", (object)(lid * 7)), ("@j", content), ("@k", extra.Length > 0 ? (object)extra : DBNull.Value));
            }
        }

        // 图片 dat（XOR 格式）
        for (int i = 0; i < demoPngs.Count; i++)
        {
            var png = demoPngs[i];
            byte xk = (byte)(png[0] ^ 0xFF);
            var dat = png.Select(b => (byte)(b ^ xk)).ToArray();
            File.WriteAllBytes(Path.Combine(fsDir, "Image", "2025-12", "Image", $"demo{i + 1:D4}.dat"), dat);
            File.WriteAllBytes(Path.Combine(fsDir, "Image", "2025-12", "THUMBNAIL", $"th_demo{i + 1:D4}.dat"), dat);
        }

        // 更新 Session
        using (var db = OpenDb(Path.Combine(msgDir, "MicroMsg.db")))
        {
            var byTalker = msgs.GroupBy(m => m.talker);
            foreach (var g in byTalker)
            {
                var last = g.OrderByDescending(m => m.time).First();
                Exec(db, "INSERT INTO Session VALUES(@u,0,0,'',0,0,@c,@t,@n)",
                    ("@u", g.Key), ("@c", last.content), ("@t", (object)last.type), ("@n", (object)last.time));
            }
        }

        // ---- 全部加密成微信加密库格式 ----
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.EnumerateFiles(msgDir, "*.db").ToList())
        {
            var plain = File.ReadAllBytes(f);
            var enc = WxDbCrypto.EncryptV3(key, plain);
            File.WriteAllBytes(f, enc);
        }

        File.WriteAllText(AppPaths.DemoKeyFile, DemoKeyHex);
        File.WriteAllText(flag, "ok");
        return (dir, DemoKeyHex);
    }

    static SqliteConnection NewDb(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        // 先生成空库文件，再把每页保留段设为 48 字节（与微信 v3 / SQLCipher 布局一致），
        // 之后的建表写入都按 usable=4048 进行
        using (var c = new SqliteConnection("Data Source=" + path))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA page_size=4096; VACUUM;";
            cmd.ExecuteNonQuery();
        }
        PatchReserve(path, 48);
        return OpenDb(path);
    }

    internal static void PatchReserve(string path, int reserve)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var data = File.ReadAllBytes(path);
        data[20] = (byte)reserve;
        // 修正 page1 btree 的 cell 内容区起始指针（原为页尾 4096，需收缩到 usable 之内）
        if (data.Length >= 108 && data[100] is 0x0D or 0x0A or 0x05 or 0x02)
        {
            int contentStart = (data[105] << 8) | data[106];
            if (contentStart == 0) contentStart = 65536;
            if (contentStart > 4096 - reserve) { data[105] = (byte)((4096 - reserve) >> 8); data[106] = (byte)((4096 - reserve) & 0xFF); }
        }
        for (int off = 0; off + 4096 <= data.Length; off += 4096)
            for (int i = off + 4096 - reserve; i < off + 4096; i++) data[i] = 0;
        File.WriteAllBytes(path, data);
    }

    static SqliteConnection OpenDb(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    static void Exec(SqliteConnection c, string sql, params (string, object)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // BytesExtra: field1 = { 1: int, 2: "wxid" }，附加 field4 图片路径
    static byte[] BuildExtra(string sender, string imgPath = "")
    {
        using var ms = new MemoryStream();
        // root field 1, wiretype 2
        var inner = new MemoryStream();
        WriteVarint(inner, 0x08); WriteVarint(inner, 1);              // inner field1 varint 1
        var sb = Encoding.UTF8.GetBytes(sender);
        WriteVarint(inner, 0x12); WriteVarint(inner, (ulong)sb.Length); inner.Write(sb, 0, sb.Length); // inner field2 string
        var innerBytes = inner.ToArray();
        WriteVarint(ms, 0x0A); WriteVarint(ms, (ulong)innerBytes.Length); ms.Write(innerBytes, 0, innerBytes.Length);

        if (!string.IsNullOrEmpty(imgPath))
        {
            var p = Encoding.UTF8.GetBytes(imgPath);
            WriteVarint(ms, 0x22); WriteVarint(ms, (ulong)p.Length); ms.Write(p, 0, p.Length); // root field4 string
        }
        return ms.ToArray();
    }

    static byte[] BuildExtraImg(string imgPath) => BuildExtra("", imgPath);

    static void WriteVarint(MemoryStream s, ulong v)
    {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }

    static byte[] BuildRoomData((string un, string dn)[] members)
    {
        using var ms = new MemoryStream();
        foreach (var (un, dn) in members)
        {
            var inner = new MemoryStream();
            var b1 = Encoding.UTF8.GetBytes(un);
            WriteVarint(inner, 0x0A); WriteVarint(inner, (ulong)b1.Length); inner.Write(b1, 0, b1.Length);
            var b2 = Encoding.UTF8.GetBytes(dn);
            WriteVarint(inner, 0x12); WriteVarint(inner, (ulong)b2.Length); inner.Write(b2, 0, b2.Length);
            var bytes = inner.ToArray();
            WriteVarint(ms, 0x0A); WriteVarint(ms, (ulong)bytes.Length); ms.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    public static byte[] MakePng(string text, string colorHex)
    {
        using var bmp = new System.Drawing.Bitmap(360, 220);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(System.Drawing.ColorTranslator.FromHtml(colorHex));
        g.FillRectangle(System.Drawing.Brushes.White, 0, 70, 360, 80);
        using var font = new System.Drawing.Font("Microsoft YaHei", 16, System.Drawing.FontStyle.Bold);
        var size = g.MeasureString(text, font);
        g.DrawString(text, font, System.Drawing.Brushes.DimGray, (360 - size.Width) / 2, 92);
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }
}

// ============ 自测 ============

public static class SelfTest
{
    static string FirstDiff(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
            if (a[i] != b[i]) return $"len {a.Length}/{b.Length} 首个差异@0x{i:X} (页{(i / 4096) + 1} 偏移{i % 4096})";
        return a.Length == b.Length ? "" : $"长度 {a.Length}/{b.Length}";
    }

    /// <summary>创建一个每页带 reserve 保留段的空 sqlite 库（模拟微信/SQLCipher 布局）</summary>
    static SqliteConnection SelfTestDb(string path, int reserve)
    {
        if (File.Exists(path)) File.Delete(path);
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA page_size=4096; VACUUM;";
            cmd.ExecuteNonQuery();
        }
        Demo.PatchReserve(path, reserve);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        return conn;
    }

    /// <summary>逐页比较（跳过页尾 reserve 区——解密后该区域是 IV/HMAC，sqlite 不使用）</summary>
    static bool EqualExceptReserve(byte[] a, byte[] b, int reserve)
    {
        if (a.Length != b.Length) return false;
        for (int off = 0; off < a.Length; off += 4096)
        {
            int usable = Math.Min(4096 - reserve, a.Length - off);
            for (int i = 0; i < usable; i++)
                if (a[off + i] != b[off + i]) return false;
        }
        return true;
    }
    public static int RunAll(string tmpDir)
    {
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) { pass++; Console.WriteLine($"[PASS] {name}"); }
            else { fail++; Console.WriteLine($"[FAIL] {name}  {detail}"); }
        }

        Directory.CreateDirectory(tmpDir);

        // ---- v3 往返（建库即带 48 字节保留段，模拟微信明文库的实际布局） ----
        try
        {
            var plainPath = Path.Combine(tmpDir, "plain_v3.db");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using (var db = SelfTestDb(plainPath, 48))
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "CREATE TABLE t(a INTEGER, b TEXT); INSERT INTO t VALUES(1,'你好微信'),(2,'Hello 世界12345');";
                cmd.ExecuteNonQuery();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var plain = File.ReadAllBytes(plainPath);

            var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var enc = WxDbCrypto.EncryptV3(key, plain);
            var encPath = Path.Combine(tmpDir, "enc_v3.db");
            File.WriteAllBytes(encPath, enc);
            Check("v3 加密后非明文", !enc.AsSpan(0, 15).SequenceEqual(plain.AsSpan(0, 15)));

            var keyHex = Convert.ToHexString(key);
            var (ok, fmt) = WxDbCrypto.TryKey(keyHex, encPath);
            Check("v3 TryKey 识别", ok && fmt == "v3", fmt);

            var decPath = Path.Combine(tmpDir, "dec_v3.db");
            WxDbCrypto.DecryptV3(key, encPath, decPath);
            var dec = File.ReadAllBytes(decPath);
            Check("v3 往返字节一致(保留段除外)", EqualExceptReserve(dec, plain, 48), FirstDiff(dec, plain));

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using (var db = new SqliteConnection("Data Source=" + decPath))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT b FROM t WHERE a=2";
                var v = (string)cmd.ExecuteScalar();
                Check("v3 解密库可查询", v == "Hello 世界12345", v);
            }
        }
        catch (Exception ex) { Check("v3 往返", false, ex.Message); }

        // ---- v4 往返（带 80 字节保留段的 sqlite 库） ----
        try
        {
            var plainPath = Path.Combine(tmpDir, "plain_v4.db");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using (var db = SelfTestDb(plainPath, 80))
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "CREATE TABLE t(a INTEGER, b TEXT); INSERT INTO t VALUES(1,'微信4.0测试');";
                cmd.ExecuteNonQuery();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var plain = File.ReadAllBytes(plainPath);

            var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var enc = WxDbCrypto.EncryptV4(key, plain);
            var encPath = Path.Combine(tmpDir, "enc_v4.db");
            File.WriteAllBytes(encPath, enc);

            var decPath = Path.Combine(tmpDir, "dec_v4.db");
            WxDbCrypto.DecryptV4(key, encPath, decPath);
            var dec = File.ReadAllBytes(decPath);
            Check("v4 往返字节一致(保留段除外)", EqualExceptReserve(dec, plain, 80), FirstDiff(dec, plain));

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using (var db = new SqliteConnection("Data Source=" + decPath))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT b FROM t WHERE a=1";
                var v = (string)cmd.ExecuteScalar();
                Check("v4 解密库可查询(reserved=80)", v == "微信4.0测试", v);
            }
        }
        catch (Exception ex) { Check("v4 往返", false, ex.Message); }

        // ---- dat 图片 ----
        try
        {
            var png = Demo.MakePng("dat 测试", "#D4A017");
            byte xk = 0x37;
            var dat = png.Select(b => (byte)(b ^ xk)).ToArray();
            var datPath = Path.Combine(tmpDir, "img.dat");
            File.WriteAllBytes(datPath, dat);
            var (data, ext) = DatImage.Decode(datPath);
            Check("dat XOR 解码", data != null && data.AsSpan().SequenceEqual(png.AsSpan()) && ext == "png", ext ?? "null");

            // V1 格式
            var v1 = BuildV1Dat(png, Encoding.ASCII.GetBytes("cfcd208495d565ef"));
            var v1Path = Path.Combine(tmpDir, "img_v1.dat");
            File.WriteAllBytes(v1Path, v1);
            var (d1, e1) = DatImage.Decode(v1Path);
            Check("dat V1(AES-ECB) 解码", d1 != null && d1.AsSpan().SequenceEqual(png.AsSpan()), e1 ?? "null");
        }
        catch (Exception ex) { Check("dat 图片", false, ex.Message); }

        // ---- 演示数据全链路 ----
        try
        {
            var (dir, keyHex) = Demo.EnsureDemoData();
            var wxidDir = Directory.GetDirectories(Path.Combine(dir, "WeChat Files"))[0];
            var dbs = WxDetect.FindV3Dbs(wxidDir).Select(r => r.path).ToList();
            var ws = Path.Combine(tmpDir, "ws_demo");
            var report = WxDetect.DecryptAll(dbs, keyHex, ws);
            Check("演示库解密", report.Ok.Count >= 3 && report.Failed.Count == 0, $"ok={report.Ok.Count} fail={report.Failed.Count}");

            var store = WxStore.Load(report.Map.Values.ToArray(), wxidDir).Store;
            Check("演示会话加载", store.Sessions.Count >= 4, $"sessions={store.Sessions.Count}");
            var totalMsgs = store.Sessions.Sum(s => s.Messages.Count);
            Check("演示消息加载", totalMsgs >= 15, $"msgs={totalMsgs}");
            var room = store.Sessions.FirstOrDefault(s => s.Talker.EndsWith("@chatroom"));
            Check("群聊发送者解析", room != null && room.Messages.Any(m => m.Sender.StartsWith("wxid_")));

            var media = new MediaCache(WxStore.MediaIndex.Build(wxidDir, Path.GetFileName(wxidDir)), Path.Combine(tmpDir, "cache"));
            var imgSession = store.Sessions.FirstOrDefault(s => s.Talker == "wxid_demo_zhang");
            var imgMsg = imgSession?.Messages.FirstOrDefault(m => m.Type == 3);
            var imgBytes = imgMsg != null ? media.GetImage(imgMsg) : null;
            Check("演示图片解码", imgBytes != null && imgBytes.Length > 100);

            var expDir = Path.Combine(tmpDir, "exp");
            Directory.CreateDirectory(expDir);
            var html = Exporter.ExportHtml(imgSession, store, media, new ExportOptions { OutDir = expDir });
            var htmlText = File.ReadAllText(html);
            Check("HTML 导出", File.Exists(html) && htmlText.Contains("data:image/png;base64,") && htmlText.Contains("张三"));
            Check("群成员显示名", WxStore.RoomDisplayName(room?.Talker ?? "", "wxid_demo_zhang") == "张三");
        }
        catch (Exception ex) { Check("演示全链路", false, ex.ToString()); }

        Console.WriteLine($"==== 自测完成: {pass} 通过, {fail} 失败 ====");
        return fail == 0 ? 0 : 1;
    }

    static byte[] BuildV1Dat(byte[] png, byte[] aesKey16)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = aesKey16; aes.Mode = System.Security.Cryptography.CipherMode.ECB;
        aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
        var enc = aes.EncryptEcb(png, System.Security.Cryptography.PaddingMode.PKCS7);
        uint aesSize = (uint)png.Length;
        var aligned = (int)((aesSize / 16 + 1) * 16);
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x07, 0x08, (byte)'V', (byte)'1', 0x08, 0x07 }, 0, 6);
        ms.Write(BitConverter.GetBytes(aesSize), 0, 4);
        ms.Write(BitConverter.GetBytes(0u), 0, 4);
        ms.WriteByte(0);
        // aes 数据区按 aligned 大小（含填充）
        ms.Write(enc, 0, enc.Length);
        while (ms.Length < 15 + aligned) ms.WriteByte(0);
        return ms.ToArray();
    }
}

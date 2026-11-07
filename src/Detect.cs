using Microsoft.Win32;

namespace WeChatMsgExporter;

// ============ 微信数据自动识别 ============

public class WxInstall
{
    public string Kind = "";          // v3 / v4
    public string ExePath = "";
    public string DataRoot = "";      // WeChat Files / xwechat_files
    public List<string> Accounts = new();   // wxid 列表
    public override string ToString() => $"[{Kind}] {DataRoot} ({Accounts.Count} 账号)";
}

public static class WxDetect
{
    // ---------- 找安装与数据目录 ----------

    public static List<WxInstall> DetectInstalls()
    {
        var list = new List<WxInstall>();

        // v3: HKCU\Software\Tencent\WeChat FileSavePath + ini
        string v3Root = ResolveV3Root();
        if (!string.IsNullOrEmpty(v3Root))
        {
            var ins = new WxInstall { Kind = "v3", DataRoot = v3Root };
            ins.Accounts = SafeDirs(v3Root).Where(d => IsWxid(Path.GetFileName(d))).Select(Path.GetFileName).ToList();
            ins.ExePath = FindV3Exe();
            list.Add(ins);
        }

        // v4: Documents\xwechat_files + 全盘浅扫（可能存在多个候选根目录，全部返回）
        var v4Roots = new List<string>();
        var r0 = ResolveV4Root();
        if (!string.IsNullOrEmpty(r0)) v4Roots.Add(r0);
        foreach (var r in FindV4RootsByDriveScan())
            if (!v4Roots.Contains(r, StringComparer.OrdinalIgnoreCase)) v4Roots.Add(r);
        foreach (var v4Root in v4Roots)
        {
            var ins = new WxInstall { Kind = "v4", DataRoot = v4Root };
            ins.Accounts = AccountsUnder(v4Root);
            ins.ExePath = FindV4Exe();
            if (ins.Accounts.Count > 0) list.Add(ins);
        }
        return list;
    }

    /// <summary>全盘浅层搜索所有 xwechat_files（自定义数据目录兜底）</summary>
    public static List<string> FindV4RootsByDriveScan()
    {
        var found = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                string root = drive.Name;
                var c0 = Path.Combine(root, "xwechat_files");
                if (Directory.Exists(c0)) found.Add(c0);
                foreach (var d in SafeDirs(root))
                {
                    var c1 = Path.Combine(d, "xwechat_files");
                    if (Directory.Exists(c1) && !found.Contains(c1, StringComparer.OrdinalIgnoreCase)) found.Add(c1);
                    foreach (var d2 in SafeDirs(d))
                    {
                        var c2 = Path.Combine(d2, "xwechat_files");
                        if (Directory.Exists(c2) && !found.Contains(c2, StringComparer.OrdinalIgnoreCase)) found.Add(c2);
                    }
                }
            }
        }
        catch { }
        return found;
    }

    static List<string> SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir).ToList(); } catch { return new(); }
    }

    /// <summary>目录下所有形如 wxid 的账号文件夹</summary>
    public static List<string> AccountsUnder(string root)
    {
        var byDir = SafeDirs(root).Where(d => IsWxid(Path.GetFileName(d))).Select(Path.GetFileName).ToList();
        // 按内容过滤：真账号目录必有 db_storage 或 msg 子目录（排除 all_users 等系统目录）
        var byContent = SafeDirs(root)
            .Where(d => Directory.Exists(Path.Combine(d, "db_storage")) || Directory.Exists(Path.Combine(d, "msg")))
            .Select(Path.GetFileName).ToList();
        var final = byDir.Count > 0 ? byDir.Intersect(byContent).ToList() : byContent;
        return final.Count > 0 ? final : byContent;
    }

    /// <summary>数据目录里最新的数据库写入时间（判断目录是否是当前活跃的）</summary>
    public static DateTime NewestDbWrite(string wxDir)
    {
        var dt = DateTime.MinValue;
        try
        {
            var storage = Path.Combine(wxDir, "db_storage");
            if (Directory.Exists(storage))
            {
                foreach (var f in Directory.EnumerateFiles(storage, "*.db*", SearchOption.AllDirectories))
                {
                    try { var t = File.GetLastWriteTime(f); if (t > dt) dt = t; } catch { }
                }
            }
        }
        catch { }
        return dt;
    }

    public static bool IsWxid(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 6) return false;
        if (!s.StartsWith("wxid_") && !s.StartsWith("weixin_") && s.Contains(' ')) return false;
        return s.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    static string ResolveV3Root()
    {
        string baseDir = null;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Tencent\WeChat");
            baseDir = k?.GetValue("FileSavePath") as string;
        }
        catch { }
        if (string.IsNullOrEmpty(baseDir) || baseDir == "MyDocument:")
        {
            try
            {
                var ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Tencent", "WeChat", "All Users", "config", "3ebffe94.ini");
                if (File.Exists(ini)) baseDir = File.ReadAllText(ini).Trim();
            }
            catch { }
        }
        if (string.IsNullOrEmpty(baseDir) || baseDir == "MyDocument:")
            baseDir = GetDocumentsDir();
        var root = Path.Combine(baseDir, "WeChat Files");
        return Directory.Exists(root) ? root : null;
    }

    static string ResolveV4Root()
    {
        // v4 默认在文档目录 xwechat_files；注册表可能记录自定义位置
        string[] candidates = new string[0];
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Tencent\xwechat");
            var p = k?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(p)) candidates = new[] { Path.Combine(p, "xwechat_files") };
        }
        catch { }
        var docs = GetDocumentsDir();
        var list = new List<string>(candidates ?? Array.Empty<string>()) { Path.Combine(docs, "xwechat_files") };
        foreach (var c in list) if (Directory.Exists(c)) return c;
        return null;
    }

    public static string GetDocumentsDir()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            var v = k?.GetValue("Personal") as string;
            if (!string.IsNullOrEmpty(v))
            {
                var expanded = Environment.ExpandEnvironmentVariables(v);
                if (Directory.Exists(expanded)) return expanded;
            }
        }
        catch { }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    static string FindV3Exe()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Tencent\WeChat");
            var ip = k?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(ip))
            {
                var exe = Path.Combine(ip, "WeChat.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch { }
        return "";
    }

    static string FindV4Exe()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Tencent\xwechat");
            var ip = k?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(ip))
            {
                var exe = Path.Combine(ip, "Weixin.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch { }
        return "";
    }

    // ---------- 找数据库文件 ----------

    /// <summary>v3: 返回 (名称, 路径)。包含 MicroMsg/MSG*/MediaMSG*/ChatRoom/PublicMsg/Emotion/Misc</summary>
    public static List<(string name, string path)> FindV3Dbs(string wxDir)
    {
        var result = new List<(string, string)>();
        var msgDir = Path.Combine(wxDir, "Msg");
        void AddDir(string d, bool recursive)
        {
            if (!Directory.Exists(d)) return;
            var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            try
            {
                foreach (var f in Directory.EnumerateFiles(d, "*.db", opt))
                {
                    var name = Path.GetFileName(f);
                    if (name.StartsWith("de_", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Contains("FTS", StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add((name, f));
                }
            }
            catch { }
        }
        AddDir(msgDir, true);
        // 兜底：wxDir 下其他位置
        try
        {
            foreach (var f in Directory.EnumerateFiles(wxDir, "*.db", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(f);
                if (name.StartsWith("de_", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Contains("FTS", StringComparison.OrdinalIgnoreCase)) continue;
                if (!result.Any(r => r.Item2 == f)) result.Add((name, f));
            }
        }
        catch { }
        return result.DistinctBy(r => r.Item2).ToList();
    }

    /// <summary>v4: db_storage 下所有 .db（排除 fts 等）</summary>
    public static List<(string name, string path)> FindV4Dbs(string wxDir)
    {
        var result = new List<(string, string)>();
        var storage = Path.Combine(wxDir, "db_storage");
        if (!Directory.Exists(storage)) return result;
        try
        {
            foreach (var f in Directory.EnumerateFiles(storage, "*.db", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(f);
                var lower = f.ToLowerInvariant();
                if (name.StartsWith("de_", StringComparison.OrdinalIgnoreCase)) continue;
                if (lower.Contains("fts") || lower.Contains("biz")) continue;
                result.Add((name, f));
            }
        }
        catch { }
        return result;
    }

    // ---------- 解密流水线 ----------

    public class DecryptReport
    {
        public List<string> Ok = new();
        public List<string> Failed = new();
        public Dictionary<string, string> Map = new();   // 原路径 -> 解密输出路径
    }

    /// <summary>为一批 db 生成不冲突的输出文件名（同名时追加路径哈希）</summary>
    public static Dictionary<string, string> BuildOutNames(IEnumerable<string> paths)
    {
        var byName = paths.GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                          .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, string>();
        foreach (var g in byName)
        {
            foreach (var p in g.Value)
            {
                var name = g.Key;
                if (g.Value.Count > 1)
                {
                    var h = Math.Abs(p.ToLowerInvariant().GetHashCode()).ToString("x6");
                    name = $"{Path.GetFileNameWithoutExtension(p)}_{h}{Path.GetExtension(p)}";
                }
                map[p] = name;
            }
        }
        return map;
    }

    /// <summary>用给定密钥批量解密到工作目录。keyHex 为空时假设数据库已明文（复制）。</summary>
    public static DecryptReport DecryptAll(IEnumerable<string> dbPaths, string keyHex, string outDir,
        Action<string> log = null, Action<int, int> progress = null, bool passwordMode = false)
    {
        var report = new DecryptReport();
        Directory.CreateDirectory(outDir);
        var dbs = dbPaths.ToList();
        var outNames = BuildOutNames(dbs);
        int total = dbs.Count, idx = 0;

        foreach (var db in dbs)
        {
            idx++;
            progress?.Invoke(idx, total);
            try
            {
                var outPath = Path.Combine(outDir, outNames[db]);
                var fmt = WxDbCrypto.Detect(db);
                if (fmt == WxDbCrypto.DbFormat.Plain)
                {
                    if (!File.Exists(outPath)) File.Copy(db, outPath, true);
                    report.Ok.Add(db); report.Map[db] = outPath;
                    log?.Invoke($"[明文] {Path.GetFileName(db)}");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(keyHex)) { report.Failed.Add(db); continue; }

                var key = Convert.FromHexString(keyHex.Replace(" ", ""));
                bool ok;
                if (passwordMode)
                {
                    // 微信 4.1.13+/4.2：password 经 PBKDF2-SHA512(256000 轮, 盐=文件头16字节) 派生 enc_key
                    var saltDb = WxDbCrypto.ReadPage1(db).AsSpan(0, 16).ToArray();
                    var encKey = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
                        key, saltDb, 256000, System.Security.Cryptography.HashAlgorithmName.SHA512, 32);
                    ok = WxDbCrypto.DecryptV4(encKey, db, outPath);
                    if (ok) log?.Invoke($"[v4P] {Path.GetFileName(db)}（password 派生）");
                    else { report.Failed.Add(db); log?.Invoke($"[失败] {Path.GetFileName(db)}（password 不匹配）"); continue; }
                    report.Ok.Add(db); report.Map[db] = outPath;
                    continue;
                }
                if (WxDbCrypto.VerifyV3(key, WxDbCrypto.ReadPage1(db)))
                {
                    ok = WxDbCrypto.DecryptV3(key, db, outPath);
                    log?.Invoke($"[v3 ] {Path.GetFileName(db)}");
                }
                else if (WxDbCrypto.VerifyV4(key, WxDbCrypto.ReadPage1(db)))
                {
                    ok = WxDbCrypto.DecryptV4(key, db, outPath);
                    log?.Invoke($"[v4 ] {Path.GetFileName(db)}");
                }
                else
                {
                    // v4 每库独立密钥场景由调用方预先处理（PerDbKeys）
                    report.Failed.Add(db);
                    log?.Invoke($"[失败] {Path.GetFileName(db)}（密钥不匹配）");
                    continue;
                }
                if (ok) { report.Ok.Add(db); report.Map[db] = outPath; }
                else report.Failed.Add(db);
            }
            catch (Exception ex)
            {
                report.Failed.Add(db);
                log?.Invoke($"[异常] {Path.GetFileName(db)}: {ex.Message}");
            }
        }
        return report;
    }

    /// <summary>v4 每库独立密钥解密</summary>
    public static DecryptReport DecryptAllV4(Dictionary<string, string> perDbKeys, string outDir,
        Action<string> log = null, Action<int, int> progress = null, int ivOffHint = 4016)
    {
        var report = new DecryptReport();
        Directory.CreateDirectory(outDir);
        var outNames = BuildOutNames(perDbKeys.Keys);
        int total = perDbKeys.Count, idx = 0;
        foreach (var kv in perDbKeys)
        {
            idx++;
            progress?.Invoke(idx, total);
            try
            {
                var outPath = Path.Combine(outDir, outNames[kv.Key]);
                var key = Convert.FromHexString(kv.Value.Replace(" ", ""));
                if (WxDbCrypto.DecryptV4Auto(key, kv.Key, outPath, ivOffHint))
                {
                    report.Ok.Add(kv.Key); report.Map[kv.Key] = outPath;
                    log?.Invoke($"[v4 ] {Path.GetFileName(kv.Key)}");
                }
                else { report.Failed.Add(kv.Key); log?.Invoke($"[失败] {Path.GetFileName(kv.Key)}"); }
            }
            catch (Exception ex)
            {
                report.Failed.Add(kv.Key);
                log?.Invoke($"[异常] {Path.GetFileName(kv.Key)}: {ex.Message}");
            }
        }
        return report;
    }

    static string MakeRelName(string db) => Path.GetFileName(db);
}

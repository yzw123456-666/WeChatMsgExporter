namespace WeChatMsgExporter;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // 命令行自测模式: WeChatMsgExporter.exe --selftest
        if (args.Contains("--selftest"))
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            Console.WriteLine("==== 微信聊天记录导出器 自测 ====");
            var tmp = Path.Combine(Path.GetTempPath(), "wxexporter_selftest_" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                return SelfTest.RunAll(tmp);
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        // cipher 配置结构指针追踪探针: WeChatMsgExporter.exe --probe4
        if (args.Contains("--probe4"))
        {
            Console.WriteLine("[t0] probe4 启动"); Console.Out.Flush();
            var procs = WxKeyFinder.FindWeChatProcesses();
            Console.WriteLine($"[t1] procs={procs.Count}"); Console.Out.Flush();
            var proc = procs.FirstOrDefault();
            if (proc == null) { Console.WriteLine("无微信进程"); return 1; }
            Console.WriteLine($"[t2] PID={proc.Pid}"); Console.Out.Flush();
            var dbs = new List<(string saltHex, byte[] page1)>();
            foreach (var ins in WxDetect.DetectInstalls().Where(i => i.Kind == "v4"))
                foreach (var acc in ins.Accounts)
                    foreach (var (name, path) in WxDetect.FindV4Dbs(Path.Combine(ins.DataRoot, acc)))
                        try
                        {
                            if (new FileInfo(path).Length < 4096) continue;
                            var p1 = WxDbCrypto.ReadPage1(path);
                            dbs.Add((Convert.ToHexString(p1, 0, 16), p1));
                        }
                        catch { }
            Console.WriteLine($"[t3] 采集 {dbs.Count} 个库"); Console.Out.Flush();
            Probe4.Run(proc.Pid, dbs);
            return 0;
        }

        // 内存探针模式: WeChatMsgExporter.exe --probe
        if (args.Contains("--probe"))
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            var procs = WxKeyFinder.FindWeChatProcesses();
            var proc = procs.FirstOrDefault();
            if (proc == null) { Console.WriteLine("无微信进程"); return 1; }
            Console.WriteLine($"PID={proc.Pid}");
            var installs = WxDetect.DetectInstalls();
            var v4 = installs.FirstOrDefault(i => i.Kind == "v4");
            var dbs = new List<(string saltHex, byte[] page1)>();
            foreach (var ins in installs.Where(i => i.Kind == "v4"))
                foreach (var acc in ins.Accounts)
                    foreach (var (name, path) in WxDetect.FindV4Dbs(Path.Combine(ins.DataRoot, acc)))
                    {
                        try
                        {
                            if (new FileInfo(path).Length < 4096) continue;
                            var p1 = WxDbCrypto.ReadPage1(path);
                            dbs.Add((Convert.ToHexString(p1, 0, 16), p1));
                        }
                        catch { }
                    }
            Console.WriteLine($"采集 {dbs.Count} 个库的盐值");
            Probe.Run(proc.Pid, dbs.Take(3).ToList());
            return 0;
        }

        // cipher 配置结构精确探针: WeChatMsgExporter.exe --probe3
        if (args.Contains("--probe3"))
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            var procs = WxKeyFinder.FindWeChatProcesses();
            var proc = procs.FirstOrDefault();
            if (proc == null) { Console.WriteLine("无微信进程"); return 1; }
            Console.WriteLine($"PID={proc.Pid}");
            var dbs = new List<(string saltHex, byte[] page1)>();
            foreach (var ins in WxDetect.DetectInstalls().Where(i => i.Kind == "v4"))
                foreach (var acc in ins.Accounts)
                    foreach (var (name, path) in WxDetect.FindV4Dbs(Path.Combine(ins.DataRoot, acc)))
                        try
                        {
                            if (new FileInfo(path).Length < 4096) continue;
                            var p1 = WxDbCrypto.ReadPage1(path);
                            dbs.Add((Convert.ToHexString(p1, 0, 16), p1));
                        }
                        catch { }
            Console.WriteLine($"采集 {dbs.Count} 个库");
            Probe3.Run(proc.Pid, dbs);
            return 0;
        }

        // codec 配置结构探针: WeChatMsgExporter.exe --probe2
        if (args.Contains("--probe2"))
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            var procs = WxKeyFinder.FindWeChatProcesses();
            var proc = procs.FirstOrDefault();
            if (proc == null) { Console.WriteLine("无微信进程"); return 1; }
            Console.WriteLine($"PID={proc.Pid}");
            Probe2.Run(proc.Pid);
            return 0;
        }

        // 自动识别诊断模式: WeChatMsgExporter.exe --auto
        if (args.Contains("--auto"))
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            return ConsoleAutoDetect();
        }


        // 示例导出模式: WeChatMsgExporter.exe --demo-export <输出目录>
        if (args.Length >= 2 && args[0] == "--demo-export")
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            return DemoExport(args[1]);
        }

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (s, e) =>
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "wxexporter_debug.log"),
                "\n[UI异常] " + e.Exception); } catch { }
        };
        Application.Run(new MainForm(args.Contains("--demo")));
        return 0;
    }

    static int DemoExport(string outDir)
    {
        try
        {
            Console.WriteLine("[1/4] 生成演示数据...");
            var (dir, keyHex) = Demo.EnsureDemoData();
            var wxidDir = Directory.GetDirectories(Path.Combine(dir, "WeChat Files"))[0];
            var dbs = WxDetect.FindV3Dbs(wxidDir).Select(r => r.path).ToList();
            Console.WriteLine("[2/4] 解密...");
            var ws = Path.Combine(AppPaths.Workspace("demo"));
            var report = WxDetect.DecryptAll(dbs, keyHex, ws);
            Console.WriteLine("[3/4] 加载...");
            var store = WxStore.Load(report.Map.Values.ToArray(), wxidDir).Store;
            var mi = WxStore.MediaIndex.Build(wxidDir, Path.GetFileName(wxidDir));
            var media = new MediaCache(mi, Path.Combine(AppPaths.Cache, "demo"));
            Directory.CreateDirectory(outDir);
            Console.WriteLine("[4/4] 导出 HTML...");
            foreach (var s in store.Sessions.Where(x => x.Messages.Count > 0))
            {
                var f = Exporter.ExportHtml(s, store, media, new ExportOptions { OutDir = outDir });
                Console.WriteLine("  + " + f);
            }
            var txt = Exporter.ExportTxt(store.Sessions.First(s => s.Messages.Count > 0), store, media, new ExportOptions { OutDir = outDir });
            Console.WriteLine("  + " + txt);
            Console.WriteLine("完成: " + outDir);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("失败: " + ex);
            return 1;
        }
    }

    static WxInstall DiscoverV4InsFromMemory(WxKeyFinder.ProcInfo proc, Action<string> log)
    {
        var roots = WxKeyFinder.DiscoverV4Roots(proc.Pid, log);
        var best = roots.GroupBy(r => r.Root, StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        if (best == null) return null;
        var accs = WxDetect.AccountsUnder(best);
        if (accs.Count == 0)
            accs = roots.Where(r => r.Root.Equals(best, StringComparison.OrdinalIgnoreCase))
                        .Select(r => r.WxId).Where(WxDetect.IsWxid).Distinct().ToList();
        if (accs.Count == 0) return null;
        log($"选定数据目录: {best}");
        return new WxInstall { Kind = "v4", DataRoot = best, Accounts = accs };
    }

    static int ConsoleAutoDetect()
    {
        var log = new Action<string>(s => { Console.WriteLine(s); try { Console.Out.Flush(); } catch { } });
        try
        {
            log("==== 自动识别诊断 ====");
            var procs = WxKeyFinder.FindWeChatProcesses();
            if (procs.Count == 0) { log("✗ 未检测到运行中的微信进程"); return 1; }
            foreach (var p in procs) log($"进程 {p.Name} PID={p.Pid} 内存={p.WorkingSet / 1024 / 1024}MB 版本={p.Version ?? "?"}");
            var proc = procs[0];
            bool isV4 = proc.IsV4;

            var installs = WxDetect.DetectInstalls();
            foreach (var i in installs) log("候选: " + i);

            List<(string name, string path)> dbs;
            string wxDir;
            if (isV4)
            {
                // 合并所有候选根目录 × 账号的数据库；内存中的密钥只匹配活跃目录的库，旧目录自动验证失败
                dbs = new List<(string name, string path)>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string activeRoot = null, activeWxid = null;
                var newest = DateTime.MinValue;
                foreach (var iv in installs.Where(i => i.Kind == "v4"))
                    foreach (var acc in iv.Accounts)
                    {
                        var dir = Path.Combine(iv.DataRoot, acc);
                        var nw = WxDetect.NewestDbWrite(dir);
                        log($"  候选账号: {dir}  最近写入 {(nw == DateTime.MinValue ? "-" : nw.ToString("yyyy-MM-dd HH:mm"))}");
                        foreach (var d in WxDetect.FindV4Dbs(dir))
                            if (seen.Add(d.path)) dbs.Add(d);
                        if (nw > newest) { newest = nw; activeRoot = iv.DataRoot; activeWxid = acc; }
                    }
                if (dbs.Count == 0)
                {
                    log("候选目录中无数据库 → 尝试从微信内存定位...");
                    var insM = DiscoverV4InsFromMemory(proc, log);
                    if (insM != null)
                        foreach (var acc in insM.Accounts)
                            foreach (var d in WxDetect.FindV4Dbs(Path.Combine(insM.DataRoot, acc)))
                                if (seen.Add(d.path)) dbs.Add(d);
                }
                if (dbs.Count == 0) { log("✗ 未找到任何数据库"); return 1; }
                wxDir = Path.Combine(activeRoot ?? "", activeWxid ?? "");
                log($"活跃数据目录: {wxDir}");
            }
            else
            {
                var ins = installs.FirstOrDefault(i => i.Kind == "v3");
                if (ins == null || ins.Accounts.Count == 0) { log("✗ 数据目录未找到"); return 1; }
                wxDir = Path.Combine(ins.DataRoot, ins.Accounts[0]);
                dbs = WxDetect.FindV3Dbs(wxDir);
            }
            log($"数据库 {dbs.Count} 个:");
            foreach (var d in dbs)
                log($"  {d.name,-24} {new FileInfo(d.path).Length / 1024,8}KB" +
                    (File.Exists(d.path + "-wal") ? $"  (wal {new FileInfo(d.path + "-wal").Length / 1024}KB)" : ""));

            var page1s = new Dictionary<string, byte[]>();
            foreach (var (name, path) in dbs)
            {
                try
                {
                    if (new FileInfo(path).Length < 4096) continue;
                    if (WxDbCrypto.Detect(path) == WxDbCrypto.DbFormat.Plain) { log($"  [明文] {name}"); continue; }
                    page1s[path] = WxDbCrypto.ReadPage1(path);
                }
                catch (Exception ex) { log($"  [读取失败] {name}: {ex.Message}"); }
            }
            log($"待验证 {page1s.Count} 个加密库");

            log("开始内存密钥扫描（微信主进程）...");
            var scan = WxKeyFinder.Scan(proc, page1s, log);
            log(scan.Found
                ? $"✓ 密钥={scan.KeyHex} 每库密钥={scan.PerDbKeys.Count} ({scan.Detail})"
                : $"✗ 未找到密钥: {scan.Detail}");
            if (!scan.Found) return 2;

            var ws = AppPaths.Workspace("auto");
            var report = (isV4 && scan.PerDbKeys.Count > 0 && string.IsNullOrEmpty(scan.KeyHex))
                ? WxDetect.DecryptAllV4(scan.PerDbKeys, ws, log, null, scan.IvOff)
                : WxDetect.DecryptAll(dbs.Select(d => d.path), scan.KeyHex, ws, log);
            log($"解密成功 {report.Ok.Count} 失败 {report.Failed.Count}");

            log("--- 解密库表结构（抽样）---");
            foreach (var kv in report.Map.Take(8))
            {
                try
                {
                    using var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + kv.Value);
                    c.Open();
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' LIMIT 12";
                    var names = new List<string>();
                    using (var r = cmd.ExecuteReader()) while (r.Read()) names.Add(r.GetString(0));
                    log($"[{Path.GetFileName(kv.Key)}] {string.Join(", ", names)}");
                }
                catch (Exception ex) { log($"[{Path.GetFileName(kv.Key)}] 表错误: {ex.Message}"); }
            }

            var st = WxStore.Load(report.Map.Values.ToArray(), wxDir).Store;
            log($"会话 {st.Sessions.Count} 个 / 消息 {st.Sessions.Sum(s => s.Messages.Count)} 条");
            foreach (var s in st.Sessions.Take(10))
                log($"  {s.DisplayName} [{s.Talker}] {s.Messages.Count}条  最后: {s.LastText}");
            log("==== 完成 ====");
            return 0;
        }
        catch (Exception ex)
        {
            log("✗ 异常: " + ex);
            return 3;
        }
    }
}

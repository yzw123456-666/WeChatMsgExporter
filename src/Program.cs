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
}

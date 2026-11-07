using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WeChatMsgExporter;

// ============ 主窗口 ============

public class MainForm : Form
{
    SessionListBox sessionList;
    ChatCanvas chat;
    TextBox searchBox;
    Label chatTitle, chatSub;
    Panel emptyPanel;
    StatusStrip status;
    ToolStripStatusLabel statusLabel;

    Store store;
    MediaCache media;

    Sidebar sidebarNav;
    Panel rightPanel;
    readonly bool autoDemo;

    public MainForm(bool autoDemo = false)
    {
        this.autoDemo = autoDemo;
        Text = "微信聊天记录导出器";
        Size = new Size(1180, 780);
        MinimumSize = new Size(960, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.FontMain;
        BackColor = Theme.PanelBg;
        KeyPreview = true;

        BuildUi();
        UpdateEmptyState();
        if (autoDemo) Shown += async (s, e) => await LoadDemoAsync();
    }

    void BuildUi()
    {
        // ---- 微信风格深色侧边导航 ----
        var sidebar = new Sidebar { Dock = DockStyle.Left };
        sidebarNav = sidebar;
        sidebar.OnClick += async id =>
        {
            switch (id)
            {
                case Sidebar.IconId.Chat: break;
                case Sidebar.IconId.Export: ShowExportDialog(); break;
                case Sidebar.IconId.Detect: await AutoDetectAsync(); break;
                case Sidebar.IconId.Import: ShowImportDialog(); break;
                case Sidebar.IconId.About:
                    MessageBox.Show(this,
                        "微信聊天记录导出器 v1.1\n\n用于导出与查看本人电脑版微信（3.x / 4.x）的聊天记录。\n\n· 支持 HTML（微信样式网页）/ TXT / CSV 导出\n· 原始数据库只读不写，全部处理在本地完成\n\n仅用于个人数据备份，请遵守当地法律与微信用户协议。",
                        "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    break;
            }
        };
        Controls.Add(sidebar);

        // ---- 左侧：搜索 + 会话列表 ----
        var left = new Panel { Dock = DockStyle.Fill, BackColor = Theme.PanelBg };

        searchBox = new TextBox
        {
            Dock = DockStyle.Top,
            Font = Theme.FontMain,
            BorderStyle = BorderStyle.FixedSingle,
        };
        SetCue(searchBox, "🔍  搜索联系人 / 消息");
        var searchWrap = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Theme.PanelBg, Padding = new Padding(12, 12, 12, 8) };
        searchBox.Parent = searchWrap;
        searchBox.Dock = DockStyle.Fill;

        sessionList = new SessionListBox { Dock = DockStyle.Fill };
        sessionList.SelectedIndexChanged += (s, e) =>
        {
            if (sessionList.SelectedItem is SessionInfo si)
                OpenSession(si);
        };

        left.Controls.Add(sessionList);
        left.Controls.Add(searchWrap);
        searchBox.TextChanged += (s, e) => sessionList.ApplyFilter(searchBox.Text.Trim());

        // ---- 右侧：聊天区 ----
        rightPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        var chatHeader = new Panel { Dock = DockStyle.Top, Height = 50, BackColor = Theme.PanelBg };
        chatHeader.Paint += (s, e) => e.Graphics.DrawLine(new Pen(Theme.Border), 0, chatHeader.Height - 1, chatHeader.Width, chatHeader.Height - 1);
        chatTitle = new Label { Text = "", Font = Theme.FontTitle, AutoSize = true, Location = new Point(18, 11) };
        chatSub = new Label { Text = "", Font = Theme.FontSmall, AutoSize = true, ForeColor = Theme.Gray, Location = new Point(18, 32) };
        chatHeader.Controls.Add(chatTitle);
        chatHeader.Controls.Add(chatSub);

        chat = new ChatCanvas { Dock = DockStyle.Fill };

        // 空状态
        emptyPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.PanelBg };
        var hint = new Label
        {
            Text = "微信聊天记录导出器",
            Font = new Font("Microsoft YaHei UI", 17f, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
        };
        var hint2 = new Label
        {
            Text = "① 点击「自动识别微信」—— 打开并登录电脑版微信后，一键提取聊天记录\n② 或点击「手动导入」—— 选择数据库文件夹 + 密钥（也支持已解密数据库）\n③ 左侧选择会话即可查看，支持导出为 HTML / TXT / CSV\n\n演示数据：内置一套示例聊天记录，用于预览界面效果",
            Font = Theme.FontMain,
            ForeColor = Theme.Gray,
            AutoSize = true,
        };
        var bigAuto = BigBtn("自动识别微信", true);
        var bigImport = BigBtn("手动导入数据", false);
        var bigDemo = BigBtn("加载演示数据", false);
        bigAuto.Click += async (s, e) => await AutoDetectAsync();
        bigImport.Click += (s, e) => ShowImportDialog();
        bigDemo.Click += async (s, e) => await LoadDemoAsync();

        emptyPanel.Controls.Add(hint);
        emptyPanel.Controls.Add(hint2);
        emptyPanel.Controls.Add(bigAuto);
        emptyPanel.Controls.Add(bigImport);
        emptyPanel.Controls.Add(bigDemo);
        void Place()
        {
            int cx = emptyPanel.ClientSize.Width / 2;
            hint.Location = new Point(cx - hint.Width / 2, Math.Max(40, emptyPanel.Height / 2 - 190));
            hint2.Location = new Point(cx - hint2.Width / 2, hint.Bottom + 14);
            int bw = 200, gap2 = 16;
            int totalW = bw * 3 + gap2 * 2;
            bigAuto.Location = new Point(cx - totalW / 2, hint2.Bottom + 26);
            bigImport.Location = new Point(bigAuto.Right + gap2, bigAuto.Top);
            bigDemo.Location = new Point(bigImport.Right + gap2, bigAuto.Top);
        }
        emptyPanel.Resize += (s, e) => Place();
        emptyPanel.Paint += (s, e) => Place();

        rightPanel.Controls.Add(chat);
        rightPanel.Controls.Add(chatHeader);
        chat.BringToFront();

        // ---- 布局 ----
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            SplitterWidth = 1,
            BackColor = Theme.PanelBg,
        };
        split.Panel1.BackColor = Theme.PanelBg;
        split.Panel1.Controls.Add(left);
        split.Panel2.BackColor = Theme.Bg;
        split.Panel2.Controls.Add(emptyPanel);
        split.Panel2.Controls.Add(rightPanel);
        rightPanel.BringToFront();

        status = new StatusStrip { SizingGrip = false, BackColor = Theme.PanelBg };
        statusLabel = new ToolStripStatusLabel("就绪 — 仅用于导出本人聊天数据，请遵守当地法律与微信用户协议") { ForeColor = Theme.Gray };
        status.Items.Add(statusLabel);

        // Dock 布局顺序：最后添加的先布局 —— status(Bottom)、sidebar(Left) 先安排，split(Fill) 占剩余
        Controls.Add(split);
        Controls.Add(sidebar);
        Controls.Add(status);
        Shown += (s, e) =>
        {
            try { split.SplitterDistance = 320; } catch { }
            split.SplitterMoved += (s2, e2) => sessionList.Invalidate();
        };
    }

    static Button TopBtn(string text, bool primary)
    {
        var b = new Button
        {
            Text = text,
            Size = new Size(primary ? 128 : 96, 32),
            FlatStyle = FlatStyle.Flat,
            Font = Theme.FontMain,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        if (primary) { b.BackColor = Theme.Green; b.ForeColor = Color.White; }
        else { b.BackColor = Theme.PanelBg; b.ForeColor = Theme.Text; }
        b.FlatAppearance.BorderColor = primary ? Theme.Green : Theme.Border;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.MouseOverBackColor = primary ? Theme.GreenDark : Theme.ListHover;
        return b;
    }

    static Button BigBtn(string text, bool primary)
    {
        var b = TopBtn(text, primary);
        b.AutoSize = true;
        b.Size = new Size(200, 44);
        b.Font = new Font("Microsoft YaHei UI", 10.5f);
        return b;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
    static void SetCue(TextBox tb, string cue) => SendMessage(tb.Handle, 0x1501, (IntPtr)1, cue);

    // ---------- 数据绑定 ----------

    void BindStore(Store st, MediaCache mc)
    {
        store = st; media = mc;
        sessionList.SetSessions(store.Sessions.Where(s => s.Messages.Count > 0 || true));
        if (sessionList.Items.Count > 0) sessionList.SelectedIndex = 0;
        UpdateEmptyState();
        statusLabel.Text = $"已加载 {store.Sessions.Count} 个会话，共 {store.Sessions.Sum(x => x.Messages.Count)} 条消息 · 账号 {store.WxId} · {store.WxVersion}";
        try
        {
            var dbg = Path.Combine(Path.GetTempPath(), "wxexporter_debug.log");
            var lines = store.Sessions.Take(5).Select(s =>
                $"session '{s.Talker}' name='{s.DisplayName}' last='{s.LastText}' msgs={s.Messages.Count} first='{(s.Messages.Count > 0 ? s.Messages[0].PreviewText : "")}'");
            File.WriteAllLines(dbg, lines);
        }
        catch { }
    }

    void OpenSession(SessionInfo si)
    {
        chatTitle.Text = si.DisplayName;
        chatSub.Text = $"{si.Talker} · {si.Messages.Count} 条消息";
        chat.SetData(si, store, media);
        emptyPanel.Visible = false;
        chat.BringToFront();
    }

    void UpdateEmptyState()
    {
        bool has = store?.Sessions?.Count > 0;
        emptyPanel.Visible = !has;
        rightPanel.Visible = has;
        chat.Visible = has;
        sidebarNav?.SetEnabled(Sidebar.IconId.Export, has);
    }

    WxInstall DiscoverV4InsFromMemory(WxKeyFinder.ProcInfo proc, Action<string> log)
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
        log($"发现活跃数据目录: {best}");
        return new WxInstall { Kind = "v4", DataRoot = best, Accounts = accs };
    }

    void SetStatus(string s) => statusLabel.Text = s;

    // ---------- 自动识别 ----------

    async Task AutoDetectAsync()
    {
        using var dlg = new LogDialog("自动识别微信数据");
        var work = new Func<Action<string>, Task>(async log =>
        {
            log("正在查找运行中的微信进程...");
            var procs = WxKeyFinder.FindWeChatProcesses();
            if (procs.Count == 0)
            {
                log("✗ 未检测到运行中的微信。");
                log("");
                log("请先打开电脑版微信并登录，然后点击「自动识别微信」。");
                log("如果没有安装微信或微信已退出，请使用「手动导入」选择数据库文件夹和密钥。");
                return;
            }
            foreach (var p in procs) log($"  发现 {p.Name} (PID={p.Pid}) 版本={p.Version ?? "?"}");

            var proc = procs[0];
            if (procs.Count > 1)
            {
                var sel = SelectDialog.Pick(this, "选择微信进程", procs.Select(p => $"{p.Name} (PID={p.Pid}) {p.Version}").ToArray());
                if (sel < 0) { log("已取消"); return; }
                proc = procs[sel];
            }
            bool isV4 = proc.IsV4;

            log("正在查找微信数据目录...");
            var installs = WxDetect.DetectInstalls();
            foreach (var i in installs) log("  候选: " + i);

            List<(string name, string path)> dbs;
            string wxDir;
            string mediaWxid = "";
            if (isV4)
            {
                // 合并所有候选根目录 × 账号的数据库；内存中的密钥只匹配活跃目录的库
                dbs = new List<(string name, string path)>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string activeRoot = null, activeWxid = null;
                var newest = DateTime.MinValue;
                foreach (var iv in installs.Where(i => i.Kind == "v4"))
                    foreach (var acc in iv.Accounts)
                    {
                        var dir = Path.Combine(iv.DataRoot, acc);
                        var nw = WxDetect.NewestDbWrite(dir);
                        log($"  候选: {dir}  最近写入 {(nw == DateTime.MinValue ? "-" : nw.ToString("yyyy-MM-dd HH:mm"))}");
                        foreach (var d in WxDetect.FindV4Dbs(dir))
                            if (seen.Add(d.path)) dbs.Add(d);
                        if (nw > newest) { newest = nw; activeRoot = iv.DataRoot; activeWxid = acc; }
                    }
                if (dbs.Count == 0)
                {
                    log("候选目录中无数据库，正在从微信内存定位...");
                    var insM = await Task.Run(() => DiscoverV4InsFromMemory(proc, log));
                    if (insM != null)
                        foreach (var acc in insM.Accounts)
                            foreach (var d in WxDetect.FindV4Dbs(Path.Combine(insM.DataRoot, acc)))
                                if (seen.Add(d.path)) dbs.Add(d);
                }
                if (dbs.Count == 0) { log("✗ 未找到 .db 数据库文件。请确认微信已登录后重试，或使用「手动导入」。"); return; }
                wxDir = Path.Combine(activeRoot ?? "", activeWxid ?? "");
                mediaWxid = activeWxid ?? "";
                log($"活跃数据目录: {wxDir}");
            }
            else
            {
                var ins = installs.FirstOrDefault(i => i.Kind == "v3");
                if (ins == null || ins.Accounts.Count == 0)
                {
                    log("✗ 未找到微信数据目录。");
                    log("若微信数据保存在自定义位置，请使用「手动导入」直接选择该文件夹。");
                    return;
                }
                string wxid = ins.Accounts[0];
                if (ins.Accounts.Count > 1)
                {
                    var sel = SelectDialog.Pick(this, "选择微信账号", ins.Accounts.ToArray());
                    if (sel < 0) { log("已取消"); return; }
                    wxid = ins.Accounts[sel];
                }
                wxDir = Path.Combine(ins.DataRoot, wxid);
                mediaWxid = wxid;
                dbs = WxDetect.FindV3Dbs(wxDir).Select(d => d).ToList();
            }
            log($"共 {dbs.Count} 个数据库");

            // 读取首页用于验证
            var page1s = new Dictionary<string, byte[]>();
            foreach (var (name, path) in dbs)
            {
                try
                {
                    if (new FileInfo(path).Length < 4096) continue;
                    if (WxDbCrypto.Detect(path) == WxDbCrypto.DbFormat.Plain) continue;
                    if (isV4 || name.Equals("MicroMsg.db", StringComparison.OrdinalIgnoreCase) || name.StartsWith("MSG", StringComparison.OrdinalIgnoreCase))
                        page1s[path] = WxDbCrypto.ReadPage1(path);
                }
                catch { }
            }

            log("正在从微信内存提取数据库密钥（请保持微信已登录）...");
            var scan = await Task.Run(() => WxKeyFinder.Scan(proc, page1s, log));
            if (!scan.Found)
            {
                log("✗ " + (string.IsNullOrEmpty(scan.Detail) ? "未能提取密钥" : scan.Detail));
                // 微信 4.1.13+ 加固了密钥存储（内存中不再保留密钥/盐值），当前开源方案均无法提取
                if (isV4)
                {
                    Version v = null;
                    try { v = new Version(proc.Version ?? ""); } catch { }
                    if (v == null || v >= new Version(4, 1, 13))
                    {
                        log("");
                        log("━━ 检测到微信 " + (proc.Version ?? "4.1.13+") + " ━━");
                        log("微信 4.1.13 起对数据库密钥做了加固（内存中不再保留密钥，");
                        log("改用系统 CNG 等方式持有），目前所有已知开源方案都无法提取，");
                        log("开源社区正在攻坚中。");
                        log("");
                        log("替代方案：");
                        log("  ① 手动导入其他工具（如旧版微信环境）解密出的数据库；");
                        log("  ② 关注本项目更新，适配后可一键识别；");
                        log("  ③ 微信 3.x / 4.0.x ~ 4.1.12.x 用户不受影响，可直接识别。");
                        return;
                    }
                }
                log("提示：保持微信前台并打开任意一个聊天窗口后重试，或使用「手动导入」输入密钥。");
                return;
            }

            log("正在解密数据库（不修改原始文件）...");
            var ws = AppPaths.Workspace(isV4 ? "v4" : "v3");
            WxDetect.DecryptReport report;
            if (isV4 && scan.PerDbKeys.Count > 0 && string.IsNullOrEmpty(scan.KeyHex))
                report = await Task.Run(() => WxDetect.DecryptAllV4(scan.PerDbKeys, ws, log, null, scan.IvOff));
            else
                report = await Task.Run(() => WxDetect.DecryptAll(dbs.Select(d => d.path), scan.KeyHex, ws, log));

            log($"  成功 {report.Ok.Count} 个，失败 {report.Failed.Count} 个");
            if (report.Ok.Count == 0) { log("✗ 解密全部失败，密钥可能不匹配。请重试或手动导入。"); return; }

            log("正在加载聊天数据...");
            var lr = await Task.Run(() => WxStore.Load(report.Map.Values.ToArray(), wxDir));
            var st = lr.Store;
            foreach (var w in lr.Warnings.Take(5)) log("  警告: " + w);
            var mi = WxStore.MediaIndex.Build(wxDir, mediaWxid);
            var mc = new MediaCache(mi, AppPaths.Cache);
            await Task.Delay(50);
            BeginInvoke(() => BindStore(st, mc));
            log("✓ 完成！左侧选择会话即可查看，点击「导出记录」可导出 HTML。");
        });
        await dlg.RunAsync(work);
    }

    // ---------- 手动导入 ----------

    void ShowImportDialog() => new ImportDialog(this).ShowDialog(this);

    /// <summary>导入管线：folder=数据库目录或文件；keyHex 可空（已解密/自动）</summary>
    public async Task ImportFromAsync(string folder, string keyHex, LogDialog dlg)
    {
        Action<string> log = s => dlg?.Log(s);
        try
        {
            log("正在收集数据库文件...");
            List<string> dbs = CollectDbs(folder);
            if (dbs.Count == 0) { log("✗ 所选文件夹中没有找到 .db 数据库文件"); return; }
            bool hasV4 = dbs.Any(f => Path.GetFileName(f).StartsWith("message_", StringComparison.OrdinalIgnoreCase));
            log($"  共 {dbs.Count} 个数据库（{(hasV4 ? "微信 4.x" : "微信 3.x")} 结构）");

            string mediaRoot = folder;
            // 若选择了上级目录，尝试定位 wxid 目录
            if (!hasV4 && !dbs.Any(f => Path.GetFileName(f).Equals("MicroMsg.db", StringComparison.OrdinalIgnoreCase)))
                log("  提示: 未发现 MicroMsg.db，将按通用结构解析");

            WxDetect.DecryptReport report;
            if (string.IsNullOrWhiteSpace(keyHex))
            {
                log("未提供密钥，按已解密数据库处理...");
                report = await Task.Run(() => WxDetect.DecryptAll(dbs, "", AppPaths.Workspace("plain"), log));
            }
            else
            {
                log("正在验证密钥并解密...");
                report = await Task.Run(() => WxDetect.DecryptAll(dbs, keyHex.Replace(" ", ""), AppPaths.Workspace("manual"), log));
            }
            log($"  成功 {report.Ok.Count} 个，失败 {report.Failed.Count} 个");
            if (report.Ok.Count == 0)
            {
                log("✗ 没有可用数据库。");
                log("密钥为 64 位十六进制字符串（32 字节）；若数据库未加密请勾选「已解密」。");
                return;
            }

            log("正在加载聊天数据...");
            var lr = await Task.Run(() => WxStore.Load(report.Map.Values.ToArray(), mediaRoot));
            var st = lr.Store;
            foreach (var w in lr.Warnings.Take(5)) log("  警告: " + w);
            var mi = WxStore.MediaIndex.Build(mediaRoot, store?.WxId ?? "");
            var mc = new MediaCache(mi, AppPaths.Cache);
            await Task.Delay(50);
            BeginInvoke(() => BindStore(st, mc));
            log("✓ 导入完成！");
        }
        catch (Exception ex)
        {
            log("✗ 出错: " + ex.Message);
        }
    }

    static List<string> CollectDbs(string folder)
    {
        var result = new List<string>();
        if (File.Exists(folder) && folder.EndsWith(".db", StringComparison.OrdinalIgnoreCase)) return new List<string> { folder };
        if (!Directory.Exists(folder)) return result;

        // v3 布局优先
        var msgDir = Path.Combine(folder, "Msg");
        if (Directory.Exists(msgDir))
        {
            foreach (var f in Directory.EnumerateFiles(msgDir, "*.db", SearchOption.AllDirectories))
                if (!IsSkippedDb(f)) result.Add(f);
        }
        var storage = Path.Combine(folder, "db_storage");
        if (Directory.Exists(storage))
        {
            foreach (var f in Directory.EnumerateFiles(storage, "*.db", SearchOption.AllDirectories))
                if (!IsSkippedDb(f)) result.Add(f);
        }
        if (result.Count == 0)
        {
            foreach (var f in Directory.EnumerateFiles(folder, "*.db", SearchOption.AllDirectories))
                if (!IsSkippedDb(f)) result.Add(f);
        }
        return result.Distinct().ToList();
    }

    static bool IsSkippedDb(string f)
    {
        var n = Path.GetFileName(f);
        if (n.StartsWith("de_", StringComparison.OrdinalIgnoreCase)) return true;
        if (f.Contains("FTS", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.Contains("-wal", StringComparison.OrdinalIgnoreCase) || n.Contains("-shm", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ---------- 演示数据 ----------

    async Task LoadDemoAsync()
    {
        using var dlg = new LogDialog("加载演示数据");
        var work = new Func<Action<string>, Task>(async log =>
        {
            log("正在生成演示数据（加密微信库格式）...");
            var (dir, keyHex) = await Task.Run(() => Demo.EnsureDemoData());
            var wxidDir = Directory.GetDirectories(Path.Combine(dir, "WeChat Files"))[0];
            var dbs = WxDetect.FindV3Dbs(wxidDir).Select(r => r.path).ToList();
            log($"  {dbs.Count} 个演示数据库（已按微信 v3 加密格式存储）");
            log("正在解密...");
            var ws = AppPaths.Workspace("demo");
            var report = await Task.Run(() => WxDetect.DecryptAll(dbs, keyHex, ws, log));
            log("正在加载...");
            var st = await Task.Run(() => WxStore.Load(report.Map.Values.ToArray(), wxidDir).Store);
            var mi = WxStore.MediaIndex.Build(wxidDir, Path.GetFileName(wxidDir));
            var mc = new MediaCache(mi, Path.Combine(AppPaths.Cache, "demo"));
            await Task.Delay(50);
            BeginInvoke(() => BindStore(st, mc));
            log("✓ 演示数据加载完成！试试左侧会话与「导出记录」。");
        });
        await dlg.RunAsync(work);
    }

    // ---------- 导出 ----------

    void ShowExportDialog()
    {
        if (store == null || store.Sessions.Count == 0) return;
        new ExportDialog(this).ShowDialog(this);
    }

    public async Task ExportAsync(bool allSessions, string format, string outDir, LogDialog dlg)
    {
        Action<string> log = s => dlg?.Log(s);
        try
        {
            Directory.CreateDirectory(outDir);
            var opt = new ExportOptions { OutDir = outDir };
            var targets = allSessions ? store.Sessions : new List<SessionInfo> { ((SessionInfo)sessionList.SelectedItem) };
            int i = 0;
            foreach (var s in targets)
            {
                i++;
                log($"[{i}/{targets.Count}] {s.DisplayName} ...");
                await Task.Run(() =>
                {
                    if (format == "html") Exporter.ExportHtml(s, store, media, opt);
                    else if (format == "txt") Exporter.ExportTxt(s, store, media, opt);
                    else Exporter.ExportCsv(s, store, media, opt);
                });
            }
            log($"✓ 导出完成：{outDir}");
            dlg?.EnableOpenFolder(outDir);
        }
        catch (Exception ex)
        {
            log("✗ 导出失败: " + ex.Message);
        }
    }
}

// ============ 日志对话框（进度） ============

public class LogDialog : Form
{
    readonly ListBox logBox;
    readonly Button closeBtn, openBtn;
    string openFolder = "";
    Task running;

    public LogDialog(string title)
    {
        Text = title;
        Size = new Size(680, 470);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Font = Theme.FontMain;

        logBox = new ListBox { Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), BorderStyle = BorderStyle.None, BackColor = Color.White };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Theme.PanelBg };
        closeBtn = new Button { Text = "关闭", Size = new Size(88, 30), Anchor = AnchorStyles.Right | AnchorStyles.Bottom, FlatStyle = FlatStyle.Flat, Enabled = true };
        closeBtn.FlatAppearance.BorderColor = Theme.Border;
        openBtn = new Button { Text = "打开文件夹", Size = new Size(100, 30), Anchor = AnchorStyles.Right | AnchorStyles.Bottom, FlatStyle = FlatStyle.Flat, Visible = false };
        openBtn.FlatAppearance.BorderColor = Theme.Border;
        closeBtn.Location = new Point(680 - 88 - 16, 9);
        openBtn.Location = new Point(680 - 88 - 16 - 110, 9);
        bottom.Controls.Add(closeBtn);
        bottom.Controls.Add(openBtn);
        Controls.Add(logBox);
        Controls.Add(bottom);
        closeBtn.Click += (s, e) => Close();
        openBtn.Click += (s, e) => { if (!string.IsNullOrEmpty(openFolder)) try { Process.Start("explorer.exe", openFolder); } catch { } };
        AcceptButton = closeBtn;
        CancelButton = closeBtn;
    }

    public void Log(string s)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => Log(s)); return; }
        logBox.Items.Add(s);
        try { logBox.TopIndex = logBox.Items.Count - 1; } catch { }
    }

    public void EnableOpenFolder(string dir) { openFolder = dir; if (IsDisposed) return; BeginInvoke(() => openBtn.Visible = true); }

    public async Task RunAsync(Func<Action<string>, Task> work)
    {
        Show(Owner);
        running = work(Log);
        try { await running; } catch (Exception ex) { Log("✗ 异常: " + ex.Message); }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (running != null && !running.IsCompleted) e.Cancel = true;   // 防止扫描中途关闭
    }
}

// ============ 选择对话框 ============

public static class SelectDialog
{
    public static int Pick(IWin32Window owner, string title, string[] options)
    {
        using var f = new Form
        {
            Text = title,
            Size = new Size(460, 300),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            Font = Theme.FontMain,
        };
        var lb = new ListBox { Dock = DockStyle.Fill, Font = Theme.FontMain };
        foreach (var o in options) lb.Items.Add(o);
        lb.SelectedIndex = 0;
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46 };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Size = new Size(84, 30), FlatStyle = FlatStyle.Flat, BackColor = Theme.Green, ForeColor = Color.White };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Size = new Size(84, 30), FlatStyle = FlatStyle.Flat };
        cancel.FlatAppearance.BorderColor = Theme.Border;
        ok.Location = new Point(460 - 84 - 16, 8);
        cancel.Location = new Point(460 - 84 * 2 - 26, 8);
        bottom.Controls.Add(ok);
        bottom.Controls.Add(cancel);
        lb.DoubleClick += (s, e) => { f.DialogResult = DialogResult.OK; };
        f.Controls.Add(lb);
        f.Controls.Add(bottom);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        return f.ShowDialog(owner) == DialogResult.OK && lb.SelectedIndex >= 0 ? lb.SelectedIndex : -1;
    }
}

// ============ 导入对话框 ============

public class ImportDialog : Form
{
    readonly MainForm main;
    TextBox txtFolder, txtKey;
    CheckBox chkPlain;

    public ImportDialog(MainForm owner)
    {
        main = owner;
        Text = "手动导入聊天数据";
        Size = new Size(620, 340);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Font = Theme.FontMain;

        var lbl1 = new Label { Text = "数据库文件夹（支持微信3.x的 WeChat Files\\wxid…，或微信4.x 的 db_storage，或任意含 .db 的文件夹）", AutoSize = true, Location = new Point(18, 16), ForeColor = Theme.Gray };
        txtFolder = new TextBox { Location = new Point(18, 40), Width = 470 };
        var btnBrowse = new Button { Text = "浏览…", Location = new Point(498, 38), Size = new Size(84, 27), FlatStyle = FlatStyle.Flat };
        btnBrowse.FlatAppearance.BorderColor = Theme.Border;

        var lbl2 = new Label { Text = "数据库密钥（64 位十六进制，即 32 字节；自动识别时无需填写）", AutoSize = true, Location = new Point(18, 82), ForeColor = Theme.Gray };
        txtKey = new TextBox { Location = new Point(18, 106), Width = 470, Font = new Font("Consolas", 10f) };
        txtKey.TextChanged += (s, e) => txtKey.Text = txtKey.Text.Trim().Replace(" ", "");

        chkPlain = new CheckBox { Text = "数据库已解密（无需密钥）", Location = new Point(18, 140), AutoSize = true };
        chkPlain.CheckedChanged += (s, e) => txtKey.Enabled = !chkPlain.Checked;

        var hint = new Label
        {
            Text = "说明：\n· 微信 3.x 密钥为全局 32 字节；微信 4.x 每库密钥不同，请使用「自动识别」提取。\n· 也可以直接导入其他工具（如 PyWxDump/WeChatMsg）导出的已解密数据库。\n· 导入过程只读取原始文件，不会修改它们。",
            AutoSize = true,
            Location = new Point(18, 172),
            ForeColor = Theme.Gray,
        };

        var btnGo = new Button { Text = "开始导入", Size = new Size(120, 34), Location = new Point(462, 250), FlatStyle = FlatStyle.Flat, BackColor = Theme.Green, ForeColor = Color.White };
        var btnCancel = new Button { Text = "取消", Size = new Size(84, 34), Location = new Point(462 - 94, 250), FlatStyle = FlatStyle.Flat };
        btnCancel.FlatAppearance.BorderColor = Theme.Border;

        Controls.AddRange(new Control[] { lbl1, txtFolder, btnBrowse, lbl2, txtKey, chkPlain, hint, btnGo, btnCancel });

        btnBrowse.Click += (s, e) =>
        {
            using var fb = new FolderBrowserDialog { Description = "选择数据库文件夹" };
            if (fb.ShowDialog(this) == DialogResult.OK) txtFolder.Text = fb.SelectedPath;
        };
        btnCancel.Click += (s, e) => Close();
        btnGo.Click += async (s, e) =>
        {
            if (string.IsNullOrWhiteSpace(txtFolder.Text)) { MessageBox.Show(this, "请先选择数据库文件夹", "提示"); return; }
            if (!chkPlain.Checked && txtKey.Text.Length != 64)
            {
                var r = MessageBox.Show(this, "密钥不是 64 位十六进制。是否按「已解密数据库」继续？", "提示", MessageBoxButtons.YesNo);
                if (r != DialogResult.Yes) return;
            }
            var key = chkPlain.Checked ? "" : txtKey.Text;
            Hide();
            using var dlg = new LogDialog("导入数据");
            var t = dlg.RunAsync(log => main.ImportFromAsync(txtFolder.Text, key, dlg));
            dlg.ShowDialog(this);
            await t;
            Close();
        };
        AcceptButton = btnGo;
        CancelButton = btnCancel;
    }
}

// ============ 导出对话框 ============

public class ExportDialog : Form
{
    readonly MainForm main;
    TextBox txtDir;
    ComboBox cboFormat;
    RadioButton rbAll, rbCurrent;

    public ExportDialog(MainForm owner)
    {
        main = owner;
        Text = "导出聊天记录";
        Size = new Size(520, 300);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Font = Theme.FontMain;

        var lbl1 = new Label { Text = "导出范围", AutoSize = true, Location = new Point(18, 18), ForeColor = Theme.Gray };
        rbCurrent = new RadioButton { Text = "当前会话", AutoSize = true, Location = new Point(30, 44) };
        rbAll = new RadioButton { Text = "全部会话", AutoSize = true, Location = new Point(130, 44) };
        rbCurrent.Checked = true;

        var lbl2 = new Label { Text = "导出格式", AutoSize = true, Location = new Point(18, 80), ForeColor = Theme.Gray };
        cboFormat = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(30, 104), Width = 200 };
        cboFormat.Items.AddRange(new object[] { "HTML 网页（推荐，含图片气泡样式）", "TXT 文本", "CSV 表格" });
        cboFormat.SelectedIndex = 0;

        var lbl3 = new Label { Text = "输出文件夹", AutoSize = true, Location = new Point(18, 142), ForeColor = Theme.Gray };
        txtDir = new TextBox { Location = new Point(30, 166), Width = 360 };
        txtDir.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "微信聊天记录导出");
        var btnBrowse = new Button { Text = "浏览…", Location = new Point(400, 164), Size = new Size(84, 27), FlatStyle = FlatStyle.Flat };
        btnBrowse.FlatAppearance.BorderColor = Theme.Border;

        var btnGo = new Button { Text = "开始导出", Size = new Size(120, 34), Location = new Point(370, 216), FlatStyle = FlatStyle.Flat, BackColor = Theme.Green, ForeColor = Color.White };
        var btnCancel = new Button { Text = "取消", Size = new Size(84, 34), Location = new Point(276, 216), FlatStyle = FlatStyle.Flat };
        btnCancel.FlatAppearance.BorderColor = Theme.Border;

        Controls.AddRange(new Control[] { lbl1, rbCurrent, rbAll, lbl2, cboFormat, lbl3, txtDir, btnBrowse, btnGo, btnCancel });

        btnBrowse.Click += (s, e) =>
        {
            using var fb = new FolderBrowserDialog { Description = "选择导出文件夹" };
            if (fb.ShowDialog(this) == DialogResult.OK) txtDir.Text = fb.SelectedPath;
        };
        btnCancel.Click += (s, e) => Close();
        btnGo.Click += async (s, e) =>
        {
            var format = cboFormat.SelectedIndex switch { 1 => "txt", 2 => "csv", _ => "html" };
            Hide();
            using var dlg = new LogDialog("导出聊天记录");
            var t = dlg.RunAsync(log => main.ExportAsync(rbAll.Checked, format, txtDir.Text, dlg));
            dlg.ShowDialog(this);
            await t;
            Close();
        };
        AcceptButton = btnGo;
        CancelButton = btnCancel;
    }
}

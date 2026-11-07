using System.Diagnostics;

namespace WeChatMsgExporter;

// ============ 主题常量（对齐微信 Windows 版视觉） ============

public static class Theme
{
    public static readonly Color Bg = ColorTranslator.FromHtml("#EDEDED");          // 聊天区背景
    public static readonly Color Green = ColorTranslator.FromHtml("#07C160");
    public static readonly Color GreenDark = ColorTranslator.FromHtml("#06AD56");
    public static readonly Color Mine = ColorTranslator.FromHtml("#95EC69");        // 自己气泡
    public static readonly Color Other = Color.White;                                // 对方气泡
    public static readonly Color Text = ColorTranslator.FromHtml("#1A1A1A");
    public static readonly Color Gray = ColorTranslator.FromHtml("#9C9C9C");
    public static readonly Color TimeChip = ColorTranslator.FromHtml("#DADADA");
    public static readonly Color TimeText = ColorTranslator.FromHtml("#808080");
    public static readonly Color Border = ColorTranslator.FromHtml("#E0E0E0");
    public static readonly Color PanelBg = Color.White;
    public static readonly Color ListHover = ColorTranslator.FromHtml("#F7F7F7");
    public static readonly Color ListSel = ColorTranslator.FromHtml("#C7E8CF");     // 微信选中绿
    public static readonly Color SidebarBg = ColorTranslator.FromHtml("#2E2E2E");   // 左侧导航深色
    public static readonly Color SidebarIcon = ColorTranslator.FromHtml("#9E9E9E");
    public static readonly Color SidebarHover = ColorTranslator.FromHtml("#3A3A3A");
    public static readonly Color BadgeBg = ColorTranslator.FromHtml("#C0C0C2");     // 会话角标

    public static readonly Font FontMain = new("Microsoft YaHei UI", 9.75f);        // 气泡正文
    public static readonly Font FontName = new("Microsoft YaHei UI", 8.5f);         // 群成员名/预览
    public static readonly Font FontSession = new("Microsoft YaHei UI", 10f);       // 会话名称
    public static readonly Font FontSmall = new("Microsoft YaHei UI", 8.25f);       // 时间/系统
    public static readonly Font FontTitle = new("Microsoft YaHei UI", 12.5f, FontStyle.Bold);
    public static readonly Font FontBig = new("Microsoft YaHei UI", 10.5f);
    public static readonly Font FontLogo = new("Microsoft YaHei UI", 11f, FontStyle.Bold);

    public static Color AvatarColor(string seed)
    {
        // 微信默认头像配色（偏沉稳的青/绿/蓝/橙系）
        var palette = new[] { "#68B88E", "#5B84C4", "#E8A33D", "#C4756B", "#8A6FBF", "#4FAEA8", "#D87F5B", "#7BA23F" };
        int h = 0;
        foreach (var c in seed) h = h * 31 + c;
        return ColorTranslator.FromHtml(palette[Math.Abs(h) % palette.Length]);
    }

    /// <summary>绘制微信风格方圆头像（可带首字）</summary>
    public static void DrawAvatar(Graphics g, Rectangle r, Brush bg, string ch, Font font)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var path = RoundRect(r, 5);
        g.FillPath(bg, path);
        if (!string.IsNullOrEmpty(ch))
        {
            var c = ch[0];
            if (c >= 'a' && c <= 'z') c = char.ToUpper(c);
            TextRenderer.DrawText(g, c.ToString(), font, r, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    public static System.Drawing.Drawing2D.GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        int d = Math.Max(2, radius * 2);
        d = Math.Min(d, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

// ============ 微信风格会话列表 ============

    public class SessionListBox : ListBox
    {
        public List<SessionInfo> All = new();

    public SessionListBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = 64;
        BorderStyle = BorderStyle.None;
        Font = Theme.FontMain;
        BackColor = Theme.PanelBg;
        HorizontalScrollbar = false;
        IntegralHeight = false;
        // 彻底禁用水平滚动（owner-draw 列表偶尔会自行产生水平偏移）
        const int LB_SETHORIZONTALEXTENT = 0x0194;
        HandleCreated += (s, e) => SendMessage(Handle, LB_SETHORIZONTALEXTENT, IntPtr.Zero, IntPtr.Zero);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public void SetSessions(IEnumerable<SessionInfo> list)
    {
        All = list.ToList();
        ApplyFilter("");
    }

    public void ApplyFilter(string keyword)
    {
        BeginUpdate();
        try
        {
            Items.Clear();
            IEnumerable<SessionInfo> q = All;
            if (!string.IsNullOrWhiteSpace(keyword))
                q = All.Where(s => s.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                                   || s.Talker.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                                   || s.Messages.Any(m => m.PreviewText != null && m.PreviewText.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
            foreach (var s in q) Items.Add(s);
        }
        finally { EndUpdate(); }
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var s = (SessionInfo)Items[e.Index];
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        bool sel = (e.State & DrawItemState.Selected) != 0;

        // 锚定客户区绘制（忽略可能存在的水平偏移）
        var row = new Rectangle(0, e.Bounds.Y, Math.Max(120, ClientSize.Width), e.Bounds.Height);
        using (var bg = new SolidBrush(sel ? Theme.ListSel : Theme.PanelBg))
            g.FillRectangle(bg, row);

        // 微信方圆头像
        int av = 40, pad = 13;
        var avRect = new Rectangle(row.X + pad, row.Y + (row.Height - av) / 2, av, av);
        using (var ab = new SolidBrush(Theme.AvatarColor(s.Talker)))
            Theme.DrawAvatar(g, avRect, ab, s.DisplayName.Length > 0 ? s.DisplayName[..1] : "?", Theme.FontSession);

        int textX = avRect.Right + 11;
        int textW = Math.Max(40, row.Width - textX - 14);

        // 名称（微信为常规字重）
        TextRenderer.DrawText(g, s.DisplayName, Theme.FontSession,
            new Rectangle(textX, row.Y + 11, textW - 56, 20),
            Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // 时间（右上，浅灰）
        TextRenderer.DrawText(g, TimeUtil.FormatListTime(s.LastTime), Theme.FontSmall,
            new Rectangle(textX + textW - 62, row.Y + 13, 62, 16), ColorTranslator.FromHtml("#B2B2B2"),
            TextFormatFlags.Right | TextFormatFlags.NoPrefix);

        // 摘要
        TextRenderer.DrawText(g, s.LastText ?? "", Theme.FontName,
            new Rectangle(textX, row.Y + 36, textW - 6, 17), Theme.Gray,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // 消息数角标（微信样式：头像右上小圆点）
        if (s.MsgCount > 0)
        {
            var txt = s.MsgCount > 99 ? "99+" : s.MsgCount.ToString();
            var size = TextRenderer.MeasureText(txt, Theme.FontSmall);
            int bw = Math.Max(16, size.Width + 6), bh = 16;
            var br = new Rectangle(avRect.Right - bw / 2, avRect.Top - bh / 2 + 2, bw, bh);
            using var bb = new SolidBrush(Theme.BadgeBg);
            g.FillEllipse(bb, br);
            TextRenderer.DrawText(g, txt, Theme.FontSmall, br, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}

// ============ 微信风格聊天气泡画布 ============

public class ChatCanvas : Panel
{
    SessionInfo session;
    Store store;
    MediaCache media;

    class Item
    {
        public MsgItem Msg;
        public int Y, Height;
        public int BubbleX, BubbleY, BubbleW, BubbleH;
        public int AvatarX, AvatarY;
        public int NameY;
        public string SenderName;
        public string TimeText;      // 非空时绘制时间
        public int TimeY;
        public bool IsSystem;
        public int SysX, SysY, SysW, SysH;
        public int ImgW, ImgH;
    }

    readonly List<Item> items = new();
    int totalHeight;
    bool isGroup;

    static readonly Dictionary<string, Image> imgCache = new();

    public ChatCanvas()
    {
        DoubleBuffered = true;
        BackColor = Theme.Bg;
        AutoScroll = true;
    }

    public void Clear()
    {
        session = null; store = null; media = null;
        items.Clear(); totalHeight = 0;
        AutoScrollMinSize = new Size(0, 0);
        Invalidate();
    }

    public void SetData(SessionInfo s, Store st, MediaCache mc)
    {
        session = s; store = st; media = mc;
        isGroup = s?.Talker?.EndsWith("@chatroom") == true;
        Relayout();
        ScrollToBottom();
    }

    public void ScrollToBottom()
    {
        if (totalHeight > ClientSize.Height)
            AutoScrollPosition = new Point(0, totalHeight - ClientSize.Height + 40);
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout();
    }

    int ContentWidth => Math.Max(ClientSize.Width, 400);

    // 微信时间胶囊文案
    static string ChipText(long unix, bool sameDay)
    {
        try
        {
            var t = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
            if (sameDay) return t.ToString("HH:mm");
            var now = DateTime.Now;
            if (t.Date == now.Date) return t.ToString("HH:mm");
            if (t.Date == now.Date.AddDays(-1)) return "昨天 " + t.ToString("HH:mm");
            if (t.Year == now.Year) return t.ToString("MM-dd HH:mm");
            return t.ToString("yyyy-MM-dd HH:mm");
        }
        catch { return TimeUtil.FormatDateTime(unix); }
    }

    void Relayout()
    {
        items.Clear();
        totalHeight = 0;
        if (session == null) { AutoScrollMinSize = new Size(0, 0); Invalidate(); return; }

        int maxBubbleW = (int)(ContentWidth * 0.60);
        long lastTime = 0; bool haveLast = false;
        int pad = 16, avatar = 36, gap = 9;

        foreach (var m in session.Messages)
        {
            var it = new Item { Msg = m };

            // 时间（微信：超过时间间隔才显示）
            if (!haveLast || m.Time - lastTime > 300)
            {
                it.TimeText = ChipText(m.Time, haveLast && TimeUtil.FormatDate(m.Time) == TimeUtil.FormatDate(lastTime));
                it.TimeY = totalHeight + 8;
                totalHeight += 32;
            }
            haveLast = true; lastTime = m.Time;

            // 系统消息（微信：居中灰字，无背景）
            var sys = SystemText(m);
            if (sys != null)
            {
                var size = TextRenderer.MeasureText(sys, Theme.FontSmall, new Size(maxBubbleW, 0), TextFormatFlags.WordBreak);
                it.IsSystem = true;
                it.SysW = size.Width; it.SysH = size.Height;
                it.SysX = (ContentWidth - it.SysW) / 2;
                it.SysY = totalHeight + 4;
                it.Y = totalHeight; it.Height = it.SysH + 12;
                totalHeight += it.Height;
                items.Add(it);
                continue;
            }

            bool me = m.IsSender;
            it.SenderName = me ? store.SelfNick : SenderName(m);

            it.AvatarX = me ? ContentWidth - pad - avatar : pad;
            it.AvatarY = totalHeight + 4;

            int bodyX = me ? it.AvatarX - gap : it.AvatarX + avatar + gap;
            int bodyMaxW = me
                ? Math.Min(maxBubbleW, it.AvatarX - gap - pad)
                : Math.Min(maxBubbleW, ContentWidth - bodyX - pad - 8);

            int nameH = 0;
            if (isGroup && !me)
            {
                it.NameY = totalHeight + 2;
                nameH = 19;
            }
            int contentTop = totalHeight + 4 + nameH;

            int bw, bh;
            switch (m.Type)
            {
                case 3:
                {
                    var img = LoadImage(m);
                    if (img != null)
                    {
                        double sc = Math.Min(1.0, Math.Min(245.0 / img.Width, 300.0 / img.Height));
                        it.ImgW = Math.Max(60, (int)(img.Width * sc));
                        it.ImgH = Math.Max(40, (int)(img.Height * sc));
                        bw = it.ImgW; bh = it.ImgH;
                    }
                    else
                    {
                        (bw, bh) = MeasureText("[图片]", bodyMaxW - 24);
                        bw += 26; bh += 18;
                    }
                    break;
                }
                case 49:
                {
                    var card = CardLines(m);
                    var size = TextRenderer.MeasureText(card, Theme.FontMain, new Size(bodyMaxW - 24, 0), TextFormatFlags.WordBreak);
                    bool hasUrl = !string.IsNullOrEmpty(MsgPreview.XmlText(m.Content, "url"));
                    bw = Math.Min(size.Width + 26, bodyMaxW);
                    bh = Math.Max(52, size.Height + 22) + (hasUrl ? 18 : 0);
                    break;
                }
                default:
                    (bw, bh) = MeasureText(DisplayText(m), bodyMaxW - 24);
                    bw += 26;
                    bh += 18;
                    break;
            }

            bw = Math.Max(bw, 40);
            it.BubbleX = me ? bodyX - bw : bodyX;
            it.BubbleY = contentTop;
            it.BubbleW = bw; it.BubbleH = bh;

            int bottom = Math.Max(contentTop + bh + 12, it.AvatarY + avatar + 6);
            it.Y = totalHeight;
            it.Height = bottom - totalHeight;
            totalHeight = bottom + 2;
            items.Add(it);
        }

        totalHeight += 30;
        AutoScrollMinSize = new Size(0, totalHeight);
        Invalidate();
    }

    (int, int) MeasureText(string text, int maxW)
    {
        var size = TextRenderer.MeasureText(text, Theme.FontMain, new Size(maxW, 0), TextFormatFlags.WordBreak);
        return (size.Width, size.Height);
    }

    string DisplayText(MsgItem m) => m.Type switch
    {
        1 => m.Content ?? "",
        3 => "[图片]",
        34 => $"🎤 {MsgPreview.ParseVoiceSec(m.Content)}″",
        42 => "[名片]",
        43 => "[视频]",
        47 => "[动画表情]",
        48 => "[位置] " + MsgPreview.XmlAttr(m.Content, "poiname"),
        50 => "[语音/视频通话]",
        _ => $"[类型 {m.Type}]",
    };

    string CardLines(MsgItem m)
    {
        var title = MsgPreview.AppMsgTitle(m.Content, m.SubType);
        var desc = MsgPreview.XmlText(m.Content, "des");
        var s = title;
        if (!string.IsNullOrEmpty(desc)) s += "\n" + desc;
        return s;
    }

    string SenderName(MsgItem m)
    {
        if (string.IsNullOrEmpty(m.Sender)) return store.DisplayName.TryGetValue(session.Talker, out var d) ? d : session.Talker;
        var room = WxStore.RoomDisplayName(session.Talker, m.Sender);
        if (!string.IsNullOrEmpty(room)) return room;
        return store.DisplayName.TryGetValue(m.Sender, out var d2) ? d2 : m.Sender;
    }

    static string SystemText(MsgItem m)
    {
        if (m.Type == 10000 || m.Type == 10002)
        {
            var c = m.Content ?? "";
            int i = c.IndexOf('\n');
            var s = (i >= 0 ? c[(i + 1)..] : c).Replace("revokemsg", "").Trim();
            if (s.Length == 0 || s.StartsWith("<")) return null;
            return s;
        }
        if (m.Type == 51) return null;
        return null;
    }

    Image LoadImage(MsgItem m)
    {
        if (m.Type != 3 || media == null) return null;
        var f = media.GetImageFile(m);
        if (f == null) return null;
        if (imgCache.TryGetValue(f, out var img))
        {
            try { if (img != null) return img; } catch { }
        }
        try
        {
            img?.Dispose();
            img = Image.FromFile(f);
            imgCache[f] = img;
            return img;
        }
        catch { return null; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        var pos = AutoScrollPosition;
        g.TranslateTransform(pos.X, pos.Y);
        int sx = -pos.X, sy = -pos.Y;   // TextRenderer(GDI) 不受变换影响，手动偏移

        foreach (var it in items)
        {
            if (it.TimeText != null)
            {
                var size = TextRenderer.MeasureText(it.TimeText, Theme.FontSmall);
                var chip = new Rectangle((ContentWidth - size.Width) / 2 - 10, it.TimeY, size.Width + 20, 19);
                using var chipBrush = new SolidBrush(Color.FromArgb(210, Theme.TimeChip));
                using var path = Theme.RoundRect(chip, 3);
                g.FillPath(chipBrush, path);
                TextRenderer.DrawText(g, it.TimeText, Theme.FontSmall, new Rectangle(chip.X + sx, chip.Y + sy, chip.Width, chip.Height),
                    Theme.TimeText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            if (it.IsSystem)
            {
                TextRenderer.DrawText(g, SystemText(it.Msg) ?? "[系统消息]", Theme.FontSmall,
                    new Rectangle(it.SysX + sx, it.SysY + sy, it.SysW, it.SysH),
                    Theme.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
                continue;
            }

            var m = it.Msg;
            bool me = m.IsSender;

            // 方圆头像
            var avRect = new Rectangle(it.AvatarX, it.AvatarY, 36, 36);
            using (var ab = new SolidBrush(Theme.AvatarColor(me ? "self" : it.SenderName)))
                Theme.DrawAvatar(g, avRect, ab, it.SenderName.Length > 0 ? it.SenderName[..1] : "?", Theme.FontSession);

            // 群成员名（微信：灰色小字在气泡上方）
            if (isGroup && !me && it.NameY > 0)
                TextRenderer.DrawText(g, it.SenderName, Theme.FontName, new Point(it.BubbleX + sx, it.NameY + sy),
                    ColorTranslator.FromHtml("#7F7F7F"));

            bool hasTail = m.Type != 3 || it.ImgW <= 0;

            if (m.Type == 3 && it.ImgW > 0)
            {
                var img = LoadImage(m);
                if (img != null)
                {
                    g.DrawImage(img, it.BubbleX, it.BubbleY, it.ImgW, it.ImgH);
                }
                else DrawBubble(g, it, me, sx, sy, "[图片]", hasTail);
            }
            else if (m.Type == 49)
            {
                using var bb = new SolidBrush(me ? Theme.Mine : Theme.Other);
                var rect = new Rectangle(it.BubbleX, it.BubbleY, it.BubbleW, it.BubbleH);
                using var path = Theme.RoundRect(rect, 4);
                g.FillPath(bb, path);
                if (hasTail) DrawTail(g, bb, me, rect);
                var cardRect = new Rectangle(it.BubbleX + 13 + sx, it.BubbleY + 10 + sy, it.BubbleW - 26, it.BubbleH - 20);
                var lines = CardLines(m);
                TextRenderer.DrawText(g, lines, Theme.FontMain, cardRect, Theme.Text,
                    TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                var url = MsgPreview.XmlText(m.Content, "url");
                if (!string.IsNullOrEmpty(url))
                {
                    var urlRect = new Rectangle(cardRect.X, cardRect.Bottom - 16 + sy, cardRect.Width, 16);
                    TextRenderer.DrawText(g, "🔗 " + url, Theme.FontSmall, urlRect, Theme.GreenDark,
                        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
            }
            else
            {
                DrawBubble(g, it, me, sx, sy, DisplayText(m), hasTail);
            }
        }
        g.ResetTransform();
    }

    // 微信气泡小三角（指向头像一侧）
    static void DrawTail(Graphics g, Brush b, bool me, Rectangle r)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        if (!me)
        {
            int ty = r.Y + 13;
            p.AddPolygon(new[] { new Point(r.X, ty), new Point(r.X, ty + 10), new Point(r.X - 6, ty + 6) });
        }
        else
        {
            int ty = r.Y + 13;
            p.AddPolygon(new[] { new Point(r.Right, ty), new Point(r.Right, ty + 10), new Point(r.Right + 6, ty + 6) });
        }
        g.FillPath(b, p);
        p.Dispose();
    }

    void DrawBubble(Graphics g, Item it, bool me, int sx, int sy, string text, bool hasTail)
    {
        using var bb = new SolidBrush(me ? Theme.Mine : Theme.Other);
        var rect = new Rectangle(it.BubbleX, it.BubbleY, it.BubbleW, it.BubbleH);
        using var path = Theme.RoundRect(rect, 4);
        g.FillPath(bb, path);
        if (hasTail) DrawTail(g, bb, me, rect);
        var textRect = new Rectangle(rect.X + 13 + sx, rect.Y + 9 + sy, rect.Width - 26, rect.Height - 18);
        TextRenderer.DrawText(g, text, Theme.FontMain, textRect, Theme.Text,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (session == null) return;
        var pos = AutoScrollPosition;
        int x = e.X - pos.X, y = e.Y - pos.Y;
        foreach (var it in items)
        {
            if (it.Msg.Type == 3 && it.ImgW > 0 &&
                x >= it.BubbleX && x <= it.BubbleX + it.BubbleW && y >= it.BubbleY && y <= it.BubbleY + it.BubbleH)
            {
                var f = media?.GetImageFile(it.Msg);
                if (f != null)
                    try { Process.Start(new ProcessStartInfo(f) { UseShellExecute = true }); } catch { }
                return;
            }
            if (it.Msg.Type == 49 && x >= it.BubbleX && x <= it.BubbleX + it.BubbleW &&
                y >= it.BubbleY && y <= it.BubbleY + it.BubbleH)
            {
                var url = MsgPreview.XmlText(it.Msg.Content, "url");
                if (!string.IsNullOrEmpty(url))
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
                return;
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pos = AutoScrollPosition;
        int x = e.X - pos.X, y = e.Y - pos.Y;
        bool hand = false;
        foreach (var it in items)
        {
            if ((it.Msg.Type == 3 || it.Msg.Type == 49) &&
                x >= it.BubbleX && x <= it.BubbleX + it.BubbleW && y >= it.BubbleY && y <= it.BubbleY + it.BubbleH)
            { hand = true; break; }
        }
        Cursor = hand ? Cursors.Hand : Cursors.Default;
    }
}

// ============ 微信风格深色侧边导航 ============

public class Sidebar : Panel
{
    public enum IconId { Chat, Export, Detect, Import, About }
    public Action<IconId> OnClick;
    readonly List<(IconId id, Rectangle r, bool bottom)> layout = new();
    IconId? hover;
    IconId active = IconId.Chat;
    readonly ToolTip tip = new();
    readonly Dictionary<IconId, string> tips = new()
    {
        [IconId.Chat] = "会话",
        [IconId.Export] = "导出聊天记录 (HTML/TXT/CSV)",
        [IconId.Detect] = "自动识别微信",
        [IconId.Import] = "手动导入数据",
        [IconId.About] = "关于",
    };
    readonly Dictionary<IconId, bool> enabled = new();
    int lastBottomY;

    public Sidebar()
    {
        DoubleBuffered = true;
        BackColor = Theme.SidebarBg;
        Width = 52;
        foreach (var id in new[] { IconId.Chat, IconId.Export, IconId.Detect, IconId.Import, IconId.About })
            enabled[id] = true;
    }

    public void SetEnabled(IconId id, bool en) { enabled[id] = en; Invalidate(); }
    public void SetActive(IconId id) { active = id; Invalidate(); }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        layout.Clear();
        int top = 64;
        layout.Add((IconId.Chat, new Rectangle((Width - 30) / 2, top, 30, 30), false)); top += 52;
        layout.Add((IconId.Export, new Rectangle((Width - 30) / 2, top, 30, 30), false)); top += 52;
        int bottom = Height - 14;
        void addBottom(IconId id) { bottom -= 40; layout.Add((id, new Rectangle((Width - 28) / 2, bottom, 28, 28), true)); }
        addBottom(IconId.About);
        addBottom(IconId.Import);
        addBottom(IconId.Detect);
        lastBottomY = bottom;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // Logo：绿色圆角方块 + 白色气泡
        var logo = new Rectangle((Width - 34) / 2, 16, 34, 34);
        using (var gb = new SolidBrush(Theme.Green))
        using (var p = Theme.RoundRect(logo, 8))
            g.FillPath(gb, p);
        using (var wp = new Pen(Color.White, 2f))
        {
            var b = new Rectangle(logo.X + 7, logo.Y + 8, 20, 14);
            using var bp = Theme.RoundRect(b, 5);
            g.DrawPath(wp, bp);
            g.FillPolygon(System.Drawing.Brushes.White, new[] {
                new Point(logo.X + 12, logo.Y + 21), new Point(logo.X + 12, logo.Y + 27), new Point(logo.X + 18, logo.Y + 21) });
        }

        foreach (var (id, r, bottom) in layout)
        {
            bool en = enabled[id];
            if (id == active && en)
            {
                using var ab = new SolidBrush(Theme.SidebarHover);
                using var p = Theme.RoundRect(new Rectangle(r.X - 6, r.Y - 6, r.Width + 12, r.Height + 12), 8);
                g.FillPath(ab, p);
            }
            else if (hover == id && en)
            {
                using var ab = new SolidBrush(Theme.SidebarHover);
                using var p = Theme.RoundRect(new Rectangle(r.X - 6, r.Y - 6, r.Width + 12, r.Height + 12), 8);
                g.FillPath(ab, p);
            }
            var col = !en ? ColorTranslator.FromHtml("#5A5A5A") : (id == active ? Color.White : Theme.SidebarIcon);
            DrawIcon(g, id, r, col);
        }
    }

    void DrawIcon(Graphics g, IconId id, Rectangle r, Color col)
    {
        using var p = new Pen(col, 1.8f);
        switch (id)
        {
            case IconId.Chat:
            {
                var b = new Rectangle(r.X + 2, r.Y + 4, r.Width - 4, r.Height - 12);
                using var bp = Theme.RoundRect(b, 6);
                g.DrawPath(p, bp);
                g.FillPolygon(new SolidBrush(col), new[] {
                    new Point(r.X + 8, r.Bottom - 8), new Point(r.X + 8, r.Bottom - 1), new Point(r.X + 15, r.Bottom - 8) });
                break;
            }
            case IconId.Export:
            {
                var b = new Rectangle(r.X + 3, r.Y + 12, r.Width - 6, r.Height - 14);
                using var bp = Theme.RoundRect(b, 4);
                g.DrawPath(p, bp);
                g.DrawLine(p, r.X + r.Width / 2, r.Y + 2, r.X + r.Width / 2, r.Y + r.Height - 12);
                var arrow = new[] {
                    new Point(r.X + r.Width / 2 - 5, r.Y + 8), new Point(r.X + r.Width / 2, r.Y + 2),
                    new Point(r.X + r.Width / 2 + 5, r.Y + 8) };
                g.DrawLines(p, arrow);
                break;
            }
            case IconId.Detect:
            {
                // 圆形箭头（刷新/识别）
                g.DrawArc(p, r.X + 3, r.Y + 3, r.Width - 6, r.Height - 6, -60, 300);
                var a = new[] {
                    new Point(r.Right - 8, r.Y + 2), new Point(r.Right - 3, r.Y + 7), new Point(r.Right - 10, r.Y + 9) };
                g.DrawLines(p, a);
                break;
            }
            case IconId.Import:
            {
                var b = new Rectangle(r.X + 2, r.Y + 8, r.Width - 4, r.Height - 12);
                using var bp = Theme.RoundRect(b, 3);
                g.DrawPath(p, bp);
                g.DrawArc(p, r.X + 3, r.Y + 3, 10, 8, 180, 180);
                break;
            }
            case IconId.About:
            {
                g.DrawEllipse(p, r.X + 3, r.Y + 3, r.Width - 6, r.Height - 6);
                TextRenderer.DrawText(g, "?", Theme.FontLogo, new Rectangle(r.X + sx0, r.Y, r.Width, r.Height), col,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                break;
            }
        }
    }
    static readonly int sx0 = 0;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        IconId? h = null;
        foreach (var (id, r, _) in layout)
            if (r.Contains(e.Location) && enabled[id]) { h = id; break; }
        if (h != hover)
        {
            hover = h;
            Invalidate();
            tip.RemoveAll();
            if (h.HasValue) tip.SetToolTip(this, tips[h.Value]);
        }
        Cursor = h.HasValue ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hover.HasValue) { hover = null; Invalidate(); }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        foreach (var (id, r, _) in layout)
            if (r.Contains(e.Location) && enabled[id]) { OnClick?.Invoke(id); return; }
    }
}

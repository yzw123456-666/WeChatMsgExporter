using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace WeChatMsgExporter;

// ============ 导出器：HTML（主）/ TXT / CSV ============

public class ExportOptions
{
    public string OutDir = "";
    public bool EmbedImages = true;      // HTML 内嵌 base64
    public long FromTime = 0;
    public long ToTime = long.MaxValue;
}

public static class Exporter
{
    // ---------- HTML ----------

    const string CssHtml = @" - 微信聊天记录</title><style>
:root{--green:#07C160;--bg:#EDEDED;--mine:#95EC69;--txt:#111;}
*{box-sizing:border-box}
body{margin:0;background:#F5F5F5;font-family:""Segoe UI"",""Microsoft YaHei"",sans-serif;color:var(--txt);}
.wrap{max-width:860px;margin:0 auto;background:var(--bg);min-height:100vh;}
.header{position:sticky;top:0;background:#F7F7F7;border-bottom:1px solid #E0E0E0;padding:14px 20px;font-size:17px;font-weight:600;text-align:center;}
.meta{background:#F7F7F7;text-align:center;color:#999;font-size:12px;padding:4px 20px 12px;}
.chat{padding:12px 16px 40px;display:flex;flex-direction:column;gap:4px;}
.time{align-self:center;color:#B2B2B2;font-size:12px;background:#E0E0E0;border-radius:4px;padding:2px 8px;margin:10px 0;}
.row{display:flex;gap:10px;margin:6px 0;align-items:flex-start;}
.row.me{flex-direction:row-reverse;}
.avatar{width:38px;height:38px;border-radius:6px;flex:none;display:flex;align-items:center;justify-content:center;font-size:17px;color:#fff;font-weight:600;}
.bubble{max-width:64%;background:#fff;border-radius:8px;padding:9px 12px;font-size:14.5px;line-height:1.65;word-break:break-word;box-shadow:0 1px 1px rgba(0,0,0,.04);position:relative;white-space:pre-wrap;}
.me .bubble{background:var(--mine);}
.bubble img.pic{max-width:240px;max-height:320px;border-radius:6px;display:block;cursor:zoom-in;}
.sender{font-size:12px;color:#9a9a9a;margin-bottom:3px;}
.me .sender{text-align:right;}
.sys{align-self:center;color:#9C9C9C;font-size:12.5px;background:rgba(0,0,0,.03);border-radius:6px;padding:4px 10px;margin:6px 0;text-align:center;max-width:80%;}
.card{border-left:4px solid var(--green);background:#F8F8F8;border-radius:6px;padding:8px 12px;}
.card .t{font-weight:600;margin-bottom:4px;}
.card .d{color:#888;font-size:12.5px;overflow:hidden;text-overflow:ellipsis;}
.card a{color:var(--green);text-decoration:none;font-size:12.5px;word-break:break-all;}
.voice{display:flex;align-items:center;gap:8px;font-size:13.5px;}
.voice .dur{color:#888;font-size:12px;}
.footer{text-align:center;color:#bbb;font-size:12px;padding:18px 0 26px;}
</style></head><body><div class=""wrap"">
<div class=""header"">";

    public static string ExportHtml(SessionInfo session, Store store, MediaCache media, ExportOptions opt)
    {
        var title = Html(session.DisplayName);
        var sb = new StringBuilder(1 << 20);
        sb.Append("""
            <!DOCTYPE html><html lang="zh-CN"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>
            """);
        sb.Append(title);
        sb.Append(CssHtml);
        sb.Append("</div>");
        sb.Append("<div class=\"meta\">共 ").Append(session.Messages.Count(m => InRange(m, opt)))
          .Append(" 条消息 · 导出时间 ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
          .Append(" · 本文件由 微信聊天记录导出器 生成，仅用于个人数据备份</div><div class=\"chat\">");

        long lastDate = 0, lastTime = 0;
        bool isGroup = session.Talker.EndsWith("@chatroom");
        string selfName = store.SelfNick;

        foreach (var m in session.Messages)
        {
            if (!InRange(m, opt)) continue;

            if (lastDate == 0 || TimeUtil.FormatDate(m.Time) != TimeUtil.FormatDate(lastDate))
            {
                sb.Append("<div class=\"time\">").Append(TimeUtil.FormatDate(m.Time)).Append("</div>");
                lastDate = m.Time;
            }
            else if (m.Time - lastTime > 300)
            {
                sb.Append("<div class=\"time\">").Append(TimeUtil.FormatTime(m.Time)).Append("</div>");
            }
            lastTime = m.Time;

            var sys = RenderSystem(m);
            if (sys != null) { sb.Append("<div class=\"sys\">").Append(Html(sys)).Append("</div>"); continue; }

            bool me = m.IsSender;
            string senderName = me ? selfName : SenderNameOf(m, store, isGroup);

            sb.Append("<div class=\"row").Append(me ? " me" : "").Append("\">");
            sb.Append("<div class=\"avatar\" style=\"background:").Append(AvatarColor(m.IsSender ? "self:" + selfName : m.Sender)).Append("\">")
              .Append(Html(AvatarChar(m.IsSender ? selfName : SenderNameOf(m, store, isGroup)))).Append("</div>");
            sb.Append("<div><div class=\"sender\">").Append(Html(senderName)).Append("</div><div class=\"bubble\">");
            sb.Append(RenderBody(m, store, media, opt));
            sb.Append("</div></div></div>");
        }

        sb.Append("<div class=\"footer\">— END —</div></div><script>document.addEventListener('click',e=>{if(e.target.matches('img.pic')){window.open(e.target.src)}})</script>");
        sb.Append("</div></body></html>");

        var file = Path.Combine(opt.OutDir, SafeFileName(session.DisplayName) + ".html");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
        return file;
    }

    static bool InRange(MsgItem m, ExportOptions o) => m.Time >= o.FromTime && m.Time <= o.ToTime;

    static string RenderSystem(MsgItem m)
    {
        if (m.Type == 10000 || m.Type == 10002)
        {
            var c = m.Content ?? "";
            int i = c.IndexOf('\n');
            var s = i >= 0 ? c[(i + 1)..] : c;
            s = s.Replace("revokemsg", "").Trim();
            if (s.Length == 0) return null;
            // 去掉 xml 残留
            if (s.StartsWith("<?xml") || s.StartsWith("<")) return null;
            return s;
        }
        if (m.Type == 51) return null;
        return null;
    }

    static string SenderNameOf(MsgItem m, Store store, bool isGroup)
    {
        if (!isGroup) return store.DisplayName.TryGetValue(m.Talker, out var d) ? d : m.Talker;
        var sender = string.IsNullOrEmpty(m.Sender) ? m.Talker : m.Sender;
        var roomName = WxStore.RoomDisplayName(m.Talker, sender);
        if (!string.IsNullOrEmpty(roomName)) return roomName;
        return store.DisplayName.TryGetValue(sender, out var d2) ? d2 : sender;
    }

    static string RenderBody(MsgItem m, Store store, MediaCache media, ExportOptions opt)
    {
        switch (m.Type)
        {
            case 1:
                return Html(m.Content ?? "");
            case 3:
            {
                var bytes = media.GetImage(m);
                if (bytes != null && opt.EmbedImages)
                {
                    var ext = DatImage.DetectFormat(bytes);
                    if (ext == "bin") ext = "jpg";
                    return $"<img class=\"pic\" alt=\"图片\" src=\"data:image/{ext};base64,{Convert.ToBase64String(bytes)}\">";
                }
                return "<i style=\"color:#999\">[图片]（未能解析本地图片文件）</i>";
            }
            case 34:
                return $"<div class=\"voice\">🎤 <span class=\"dur\">{MsgPreview.ParseVoiceSec(m.Content)}&quot;</span></div>";
            case 43:
            {
                var f = media.GetVideo(m);
                if (f != null) return $"<div class=\"card\"><div class=\"t\">🎬 视频</div><div class=\"d\">{Html(Path.GetFileName(f))}</div></div>";
                return "<i style=\"color:#999\">[视频]</i>";
            }
            case 47:
            {
                var url = MsgPreview.XmlAttr(m.Content, "cdnurl");
                if (!string.IsNullOrEmpty(url)) return $"<img class=\"pic\" src=\"{Html(url)}\" alt=\"表情\" onerror=\"this.replaceWith('[动画表情]')\">";
                return "[动画表情]";
            }
            case 48:
            {
                var label = MsgPreview.XmlAttr(m.Content, "label");
                var poiname = MsgPreview.XmlAttr(m.Content, "poiname");
                return $"<div class=\"card\"><div class=\"t\">📍 {Html(poiname)}</div><div class=\"d\">{Html(label)}</div></div>";
            }
            case 49:
                return RenderAppMsg(m, store, media, opt);
            case 50:
                return "<i style=\"color:#999\">[语音/视频通话]</i>";
            case 42:
            {
                var nick = MsgPreview.XmlAttr(m.Content, "nickname");
                return $"<div class=\"card\"><div class=\"t\">👤 名片</div><div class=\"d\">{Html(nick)}</div></div>";
            }
            case 10000:
            case 10002:
                return Html(MsgPreview.SystemPlain(m.Content));
            default:
                return $"<i style=\"color:#999\">[类型 {m.Type}]</i>";
        }
    }

    static string RenderAppMsg(MsgItem m, Store store, MediaCache media, ExportOptions opt)
    {
        string title = "", desc = "", url = "", refer = "";
        int appType = 0;
        try
        {
            var doc = XDocument.Parse(MsgPreview.CleanXml(m.Content));
            var app = doc.Descendants("appmsg").FirstOrDefault();
            if (app != null)
            {
                title = app.Element("title")?.Value ?? "";
                desc = app.Element("des")?.Value ?? "";
                url = app.Element("url")?.Value ?? "";
                int.TryParse(app.Element("type")?.Value, out appType);
                var refMsg = app.Element("refermsg");
                if (refMsg != null)
                {
                    var rn = refMsg.Element("displayname")?.Value ?? "";
                    var rc = refMsg.Element("content")?.Value ?? "";
                    refer = $"<div class=\"card\" style=\"margin-top:6px;border-left-color:#c0c0c0\"><div class=\"d\">{Html(rn)}：{Html(TrimTo(rc, 120))}</div></div>";
                }
            }
        }
        catch { }
        if (string.IsNullOrEmpty(title)) title = m.Content ?? "";

        if (appType == 6)   // 文件
        {
            return $"<div class=\"card\"><div class=\"t\">📎 {Html(title)}</div><div class=\"d\">文件</div></div>";
        }
        var link = string.IsNullOrEmpty(url) ? "" : $"<a href=\"{Html(url)}\" target=\"_blank\">{Html(TrimTo(url, 80))}</a>";
        return $"<div class=\"card\"><div class=\"t\">{Html(TrimTo(title, 200))}</div>" +
               (string.IsNullOrEmpty(desc) ? "" : $"<div class=\"d\">{Html(TrimTo(desc, 160))}</div>") + link + refer + "</div>";
    }

    static string TrimTo(string s, int n) { if (s == null) return ""; s = s.Length > n ? s[..n] + "…" : s; return s; }

    static string AvatarColor(string seed)
    {
        var palette = new[] { "#7EB050", "#5B84C4", "#D4A017", "#C4756B", "#8A6FBF", "#4FAEA8", "#D87F5B" };
        int h = 0;
        foreach (var c in seed) h = h * 31 + c;
        return palette[Math.Abs(h) % palette.Length];
    }

    static string AvatarChar(string name)
    {
        if (string.IsNullOrEmpty(name)) return "?";
        var ch = name[0];
        if (ch >= 'a' && ch <= 'z') ch = char.ToUpper(ch);
        return ch.ToString();
    }

    public static string Html(string s) => s == null ? "" :
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    public static string SafeFileName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length > 60 ? s[..60] : s;
    }

    // ---------- TXT ----------

    public static string ExportTxt(SessionInfo session, Store store, MediaCache media, ExportOptions opt)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"微信聊天记录 - {session.DisplayName} ({session.Talker})");
        sb.AppendLine($"导出时间: {DateTime.Now:yyyy-MM-dd HH:mm}    共 {session.Messages.Count} 条");
        sb.AppendLine(new string('=', 46));
        string lastDate = "";
        bool isGroup = session.Talker.EndsWith("@chatroom");
        foreach (var m in session.Messages)
        {
            if (!InRange(m, opt)) continue;
            var date = TimeUtil.FormatDate(m.Time);
            if (date != lastDate) { sb.AppendLine($"—— {date} ——"); lastDate = date; }
            var name = m.IsSender ? store.SelfNick : (isGroup ? SenderNameOf(m, store, true) : session.DisplayName);
            sb.AppendLine($"[{TimeUtil.FormatDateTime(m.Time)}] {name}: {m.PreviewText}");
        }
        var file = Path.Combine(opt.OutDir, SafeFileName(session.DisplayName) + ".txt");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
        return file;
    }

    // ---------- CSV ----------

    public static string ExportCsv(SessionInfo session, Store store, MediaCache media, ExportOptions opt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("时间,发送者,是否自己,类型,内容,备注");
        bool isGroup = session.Talker.EndsWith("@chatroom");
        foreach (var m in session.Messages)
        {
            if (!InRange(m, opt)) continue;
            var name = m.IsSender ? store.SelfNick : (isGroup ? SenderNameOf(m, store, true) : session.DisplayName);
            sb.AppendLine(string.Join(",",
                Csv(TimeUtil.FormatDateTime(m.Time)), Csv(name), Csv(m.IsSender ? "是" : "否"),
                Csv(m.Type.ToString()), Csv(m.PreviewText), Csv(m.SourceDb)));
        }
        var file = Path.Combine(opt.OutDir, SafeFileName(session.DisplayName) + ".csv");
        File.WriteAllText(file, sb.ToString(), Encoding.UTF8);   // 带 BOM 便于 Excel
        return file;
    }

    static string Csv(string s) => "\"" + (s ?? "").Replace("\"", "\"\"").Replace("\r", "").Replace("\n", " ") + "\"";
}

// ============ 媒体缓存（解码 dat -> png/jpg 缓存文件） ============

public class MediaCache
{
    readonly WxStore.MediaIndex index;
    readonly string cacheDir;
    readonly Dictionary<long, string> resolved = new();
    public string V2ImageKey = "";

    public MediaCache(WxStore.MediaIndex index, string cacheDir)
    {
        this.index = index; this.cacheDir = cacheDir;
        try { Directory.CreateDirectory(cacheDir); } catch { }
    }

    public byte[] GetImage(MsgItem m)
    {
        var p = GetImageFile(m);
        if (p == null) return null;
        try { return File.ReadAllBytes(p); } catch { return null; }
    }

    public string GetImageFile(MsgItem m)
    {
        if (resolved.TryGetValue(m.Id, out var cached)) return cached;
        string result = null;
        foreach (var cand in index.Resolve(m, wantThumb: false))
        {
            var dec = DecodeTo(cand);
            if (dec != null) { result = dec; break; }
        }
        if (result == null)
        {
            foreach (var cand in index.Resolve(m, wantThumb: true))
            {
                var dec = DecodeTo(cand);
                if (dec != null) { result = dec; break; }
            }
        }
        if (result != null) resolved[m.Id] = result;
        return result;
    }

    string DecodeTo(string datPath)
    {
        try
        {
            var (data, ext) = DatImage.Decode(datPath, V2ImageKey);
            if (data == null || ext == "bin" || ext == "hevc") return null;
            var name = Path.GetFileNameWithoutExtension(datPath);
            var safe = SHA256.HashData(Encoding.UTF8.GetBytes(datPath))[..10].ToString();
            var outP = Path.Combine(cacheDir, $"{name}_{safe}.{ext}");
            if (!File.Exists(outP)) File.WriteAllBytes(outP, data);
            return outP;
        }
        catch { return null; }
    }

    public string GetVideo(MsgItem m)
    {
        var all = index.ResolveVideo(m);
        return all.FirstOrDefault();
    }
}

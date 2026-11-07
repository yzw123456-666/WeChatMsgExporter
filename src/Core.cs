using System.Text;
using System.Xml.Linq;

namespace WeChatMsgExporter;

// ============ 数据模型 ============

public class MsgItem
{
    public long Id;                 // 序号
    public string Talker = "";      // 会话对方 wxid
    public string Sender = "";      // 实际发送者（群聊内）
    public bool IsSender;           // 是否由本账号发送
    public int Type;                // 消息类型
    public int SubType;
    public long Time;               // unix 秒
    public string Content = "";     // 文本 / XML
    public string ExtraPath = "";   // BytesExtra 中的媒体相对路径
    public byte[] RawExtra;         // BytesExtra 原始数据
    public long MsgSvrId;
    public string SourceDb = "";    // 来源 db 文件名

    // 展示辅助
    public string TimeText => TimeUtil.FormatDateTime(Time);
    public string PreviewText => MsgPreview.Preview(this);
}

public class SessionInfo
{
    public string Talker = "";          // wxid / wxid@chatroom / gh_xxx
    public string DisplayName = "";     // 显示名（备注>昵称>wxid）
    public string NickName = "";
    public string Remark = "";
    public long LastTime;
    public string LastText = "";
    public int MsgCount;
    public List<MsgItem> Messages = new();
}

public class Store
{
    public string WxDir = "";               // 数据根目录
    public string WxId = "";                // 当前账号
    public string WxVersion = "";           // v3 / v4
    public List<SessionInfo> Sessions = new();
    public Dictionary<string, SessionInfo> ByTalker = new();
    public Dictionary<string, string> DisplayName = new();   // wxid -> 显示名
    public Dictionary<string, string> SelfSenderName = new(); // 群内成员名
    public string SelfNick = "我";
}

// ============ 时间工具 ============

public static class TimeUtil
{
    public static string FormatDateTime(long unixSec)
    {
        try { return DateTimeOffset.FromUnixTimeSeconds(unixSec).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"); }
        catch { return ""; }
    }
    public static string FormatTime(long unixSec)
    {
        try { return DateTimeOffset.FromUnixTimeSeconds(unixSec).LocalDateTime.ToString("HH:mm"); }
        catch { return ""; }
    }
    public static string FormatDate(long unixSec)
    {
        try { return DateTimeOffset.FromUnixTimeSeconds(unixSec).LocalDateTime.ToString("yyyy-MM-dd"); }
        catch { return ""; }
    }
    public static string FormatListTime(long unixSec)
    {
        try
        {
            var t = DateTimeOffset.FromUnixTimeSeconds(unixSec).LocalDateTime;
            if (t.Date == DateTime.Today) return t.ToString("HH:mm");
            if (t.Year == DateTime.Today.Year) return t.ToString("MM-dd");
            return t.ToString("yyyy-MM-dd");
        }
        catch { return ""; }
    }
}

// ============ 消息预览文本 ============

public static class MsgPreview
{
    public static string Preview(MsgItem m)
    {
        switch (m.Type)
        {
            case 1: return m.Content;
            case 3: return "[图片]";
            case 34: return $"[语音] {ParseVoiceSec(m.Content)}″";
            case 42: return "[名片]";
            case 43: return "[视频]";
            case 46: return "[动画表情]";
            case 47: return "[动画表情]";
            case 48: return "[位置]";
            case 49: return AppMsgTitle(m.Content, m.SubType);
            case 50: return "[语音/视频通话]";
            case 51: return "";
            case 10000:
            case 10002: return SystemPlain(m.Content);
            case 1048625: return "[文件]";
            case 419430449: return "[合并转发]";
            default: return $"[类型{m.Type}]";
        }
    }

    public static string ParseVoiceSec(string xml)
    {
        var v = XmlAttr(xml, "voicelength");
        if (long.TryParse(v, out var ms)) return Math.Max(1, (int)Math.Round(ms / 1000.0)).ToString();
        return "1";
    }

    public static string AppMsgTitle(string xml, int subType)
    {
        if (string.IsNullOrEmpty(xml)) return "[消息]";
        try
        {
            var doc = XDocument.Parse(CleanXml(xml));
            var title = doc.Descendants("title").FirstOrDefault()?.Value;
            if (!string.IsNullOrWhiteSpace(title)) return title;
        }
        catch { }
        return subType == 6 ? "[文件]" : subType == 57 ? "[引用]" : "[链接]";
    }

    public static string CleanXml(string xml)
    {
        if (string.IsNullOrEmpty(xml)) return "<msg/>";
        var i = xml.IndexOf('<');
        var s = i > 0 ? xml.Substring(i) : xml;
        int end = s.LastIndexOf('>');
        if (end >= 0 && end < s.Length - 1) s = s.Substring(0, end + 1);
        return s;
    }

    public static string XmlAttr(string xml, string attr)
    {
        if (string.IsNullOrEmpty(xml)) return "";
        int i = xml.IndexOf(attr + "=\"", StringComparison.Ordinal);
        if (i < 0) return "";
        int s = i + attr.Length + 2;
        int e = xml.IndexOf('"', s);
        return e > s ? xml.Substring(s, e - s) : "";
    }

    public static string XmlText(string xml, string tag)
    {
        try
        {
            var doc = XDocument.Parse(CleanXml(xml));
            return doc.Descendants(tag).FirstOrDefault()?.Value ?? "";
        }
        catch { return ""; }
    }

    public static string SystemPlain(string c)
    {
        if (string.IsNullOrEmpty(c)) return "";
        int i = c.IndexOf('\n');
        var s = i >= 0 ? c.Substring(i + 1) : c;
        if (s.StartsWith("revokemsg")) return "你撤回了一条消息";
        if (s.Contains("你撤回") || s.Contains("撤回了")) return s.Trim();
        return s.Trim();
    }
}

// ============ 极简 Protobuf 解析（用于 BytesExtra 等） ============

public class PbNode
{
    public Dictionary<int, List<object>> Fields = new();
    public byte[] Raw;

    public IEnumerable<PbNode> Sub(int field)
    {
        if (Fields.TryGetValue(field, out var list))
            foreach (var o in list)
            {
                if (o is PbNode p) yield return p;
                else if (o is byte[] b)
                {
                    var n = Proto.TryDecode(b);
                    if (n != null && n.HasFields) yield return n;
                }
            }
    }

    public IEnumerable<string> Strings()
    {
        foreach (var kv in Fields)
            foreach (var o in kv.Value)
            {
                if (o is byte[] b)
                {
                    // 尝试按消息递归；失败则作为字符串
                    var child = Proto.TryDecode(b);
                    if (child != null && child.HasFields) { foreach (var s in child.Strings()) yield return s; }
                    else { var s = Str(b); if (s != null) yield return s; }
                }
            }
    }

    public bool HasFields => Fields.Count > 0;

    static string Str(byte[] b)
    {
        try
        {
            var s = Encoding.UTF8.GetString(b);
            foreach (var ch in s) if (ch < ' ' && ch != '\t') return null;
            return s;
        }
        catch { return null; }
    }
}

public static class Proto
{
    public static PbNode TryDecode(byte[] data)
    {
        if (data == null || data.Length == 0) return null;
        var node = new PbNode { Raw = data };
        int pos = 0;
        try
        {
            while (pos < data.Length)
            {
                if (!ReadVarint(data, ref pos, out var key)) return null;
                int field = (int)(key >> 3);
                int wt = (int)(key & 7);
                if (field <= 0) return null;
                object val;
                switch (wt)
                {
                    case 0:
                        if (!ReadVarint(data, ref pos, out var v)) return null;
                        val = v;
                        break;
                    case 1:
                        if (pos + 8 > data.Length) return null;
                        pos += 8; val = 0UL; break;
                    case 2:
                        if (!ReadVarint(data, ref pos, out var len)) return null;
                        if (len > (ulong)(data.Length - pos)) return null;
                        var buf = new byte[len];
                        Array.Copy(data, pos, buf, 0, (int)len);
                        pos += (int)len;
                        val = buf;
                        break;
                    case 5:
                        if (pos + 4 > data.Length) return null;
                        pos += 4; val = 0UL; break;
                    default: return null;
                }
                if (!node.Fields.TryGetValue(field, out var list))
                    node.Fields[field] = list = new List<object>();
                list.Add(val);
            }
        }
        catch { return null; }
        return node;
    }

    static bool ReadVarint(byte[] d, ref int pos, out ulong val)
    {
        val = 0; int shift = 0;
        while (pos < d.Length)
        {
            byte b = d[pos++];
            val |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
            if (shift > 63) return false;
        }
        return false;
    }
}

// ============ BytesExtra 提取（v3 群聊发送者 / 媒体路径） ============

public static class BytesExtraUtil
{
    public static string FindSender(byte[] extra)
    {
        if (extra == null || extra.Length == 0) return "";
        var root = Proto.TryDecode(extra);
        if (root == null) return "";
        foreach (var f1 in root.Sub(1))
            foreach (var s in f1.Strings())
                if (LooksLikeWxid(s)) return s;
        return "";
    }

    public static string FindMediaPath(byte[] extra)
    {
        if (extra == null || extra.Length == 0) return "";
        var root = Proto.TryDecode(extra);
        if (root == null) return "";
        foreach (var s in root.Strings())
        {
            if (s.Contains("THUMBNAIL_DIRPATH") || s.Contains("Image\\") || s.Contains("Image/")
                || s.Contains("Video\\") || s.Contains("Video/") || s.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                return s;
        }
        return "";
    }

    static bool LooksLikeWxid(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 4 || s.Length > 64) return false;
        if (s.Contains("\\") || s.Contains("/") || s.Contains(":") || s.Contains("<")) return false;
        foreach (var ch in s) if (ch < 0x21 || ch > 0x7e) return false;
        return true;
    }
}

// ============ zstd 解压 ============

public static class Z
{
    static readonly ZstdSharp.Decompressor Dec = new();
    public static byte[] Unpack(byte[] data)
    {
        try { return Dec.Unwrap(data).ToArray(); }
        catch { return null; }
    }
}

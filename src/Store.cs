using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace WeChatMsgExporter;

// ============ 数据库读取与会话构建（v3 / v4 统一） ============

public static class WxStore
{
    public class LoadResult
    {
        public Store Store = new();
        public List<string> Warnings = new();
    }

    static SqliteConnection Open(string path)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
        }.ToString();
        var conn = new SqliteConnection(cs);
        conn.Open();
        return conn;
    }

    static HashSet<string> Tables(SqliteConnection c)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    static HashSet<string> Columns(SqliteConnection c, string table)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info('{table.Replace("'", "''")}')";
            using var r = cmd.ExecuteReader();
            while (r.Read()) set.Add(r.GetString(1));
        }
        catch { }
        return set;
    }

    static object V(SqliteDataReader r, string col)
    {
        try
        {
            int i = r.GetOrdinal(col);
            if (r.IsDBNull(i)) return null;
            return r.GetValue(i);
        }
        catch { return null; }
    }

    static long L(object o, long def = 0)
    {
        try { return Convert.ToInt64(o); } catch { return def; }
    }

    static string S(object o) => o switch
    {
        null => "",
        string s => s,
        byte[] b => Encoding.UTF8.GetString(b),
        _ => o.ToString() ?? ""
    };

    // ---------- 主入口 ----------

    public static LoadResult Load(string[] dbFiles, string mediaRoot)
    {
        var res = new LoadResult();
        var store = res.Store;
        store.WxDir = mediaRoot ?? "";

        // 判断 v3 / v4
        bool hasV4 = dbFiles.Any(f => Path.GetFileName(f).StartsWith("message_", StringComparison.OrdinalIgnoreCase));
        bool hasV3 = dbFiles.Any(f => Path.GetFileName(f).Equals("MicroMsg.db", StringComparison.OrdinalIgnoreCase));
        store.WxVersion = hasV4 && !hasV3 ? "v4" : "v3";

        var contacts = new Dictionary<string, ContactRow>();

        if (hasV3) LoadV3Contacts(dbFiles, contacts, res);
        if (hasV4) LoadV4Contacts(dbFiles, contacts, res);

        foreach (var c in contacts.Values)
        {
            var dn = !string.IsNullOrEmpty(c.Remark) ? c.Remark : (!string.IsNullOrEmpty(c.NickName) ? c.NickName : c.UserName);
            store.DisplayName[c.UserName] = dn;
        }

        var msgDbFiles = dbFiles.Where(f =>
        {
            var n = Path.GetFileName(f);
            return hasV4 ? n.StartsWith("message_", StringComparison.OrdinalIgnoreCase)
                         : n.StartsWith("MSG", StringComparison.OrdinalIgnoreCase);
        }).ToList();

        if (hasV3) LoadV3Messages(msgDbFiles, contacts, store, res);
        else if (hasV4) LoadV4Messages(msgDbFiles, contacts, store, res);

        // 全局按时间排序并刷新会话摘要
        foreach (var s in store.Sessions)
        {
            s.Messages.Sort((a, b) => a.Time.CompareTo(b.Time));
            if (s.Messages.Count > 0)
            {
                var last = s.Messages[^1];
                if (last.Time >= s.LastTime) { s.LastTime = last.Time; s.LastText = last.PreviewText; }
            }
        }

        // 自身昵称
        if (contacts.TryGetValue(store.WxId, out var self))
            store.SelfNick = !string.IsNullOrEmpty(self.Remark) ? self.Remark : (!string.IsNullOrEmpty(self.NickName) ? self.NickName : "我");

        // 群成员显示名缓存
        store.SelfSenderName = store.DisplayName;

        store.Sessions = store.Sessions.OrderByDescending(s => s.LastTime).ToList();
        store.ByTalker = store.Sessions.ToDictionary(s => s.Talker);
        return res;
    }

    class ContactRow
    {
        public string UserName = "", NickName = "", Remark = "", Alias = "";
        public long Type;
    }

    // ---------- v3 ----------

    static void LoadV3Contacts(string[] dbFiles, Dictionary<string, ContactRow> contacts, LoadResult res)
    {
        var mm = dbFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("MicroMsg.db", StringComparison.OrdinalIgnoreCase));
        if (mm == null) { res.Warnings.Add("未找到 MicroMsg.db"); return; }
        using var c = Open(mm);
        if (!Tables(c).Contains("Contact")) { res.Warnings.Add("MicroMsg.db 中无 Contact 表"); return; }

        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT UserName, Alias, NickName, Remark, Type, VerifyFlag FROM Contact";
        try
        {
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var un = S(V(r, "UserName"));
                if (string.IsNullOrEmpty(un)) continue;
                contacts[un] = new ContactRow
                {
                    UserName = un,
                    Alias = S(V(r, "Alias")),
                    NickName = S(V(r, "NickName")),
                    Remark = S(V(r, "Remark")),
                    Type = L(V(r, "Type")),
                };
            }
        }
        catch (Exception ex) { res.Warnings.Add("读取联系人失败: " + ex.Message); }

        // 群成员
        LoadV3ChatRoomMembers(dbFiles, contacts, res);
    }

    static void LoadV3ChatRoomMembers(string[] dbFiles, Dictionary<string, ContactRow> contacts, LoadResult res)
    {
        foreach (var db in dbFiles)
        {
            if (!Path.GetFileName(db).StartsWith("ChatRoom", StringComparison.OrdinalIgnoreCase)) continue;
            using var c = Open(db);
            var tables = Tables(c);
            if (!tables.Contains("ChatRoom")) continue;
            var cols = Columns(c, "ChatRoom");
            using var cmd = c.CreateCommand();
            cmd.CommandText = cols.Contains("RoomData")
                ? "SELECT ChatRoomName, RoomData FROM ChatRoom"
                : (cols.Contains("UserNameList") ? "SELECT ChatRoomName, UserNameList, DisplayNameList FROM ChatRoom" : "");
            if (cmd.CommandText == "") continue;
            try
            {
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var room = S(V(r, "ChatRoomName"));
                    var data = V(r, "RoomData") as byte[];
                    if (data != null) ParseRoomData(room, data, contacts);
                }
            }
            catch { }
        }
    }

    static void ParseRoomData(string room, byte[] data, Dictionary<string, ContactRow> contacts)
    {
        var root = Proto.TryDecode(data);
        if (root == null) return;
        foreach (var member in root.Sub(1).Concat(root.Sub(2)))
        {
            string un = null, dn = null;
            if (member.Fields.TryGetValue(1, out var f1))
                foreach (var o in f1) if (o is byte[] b) { un = Encoding.UTF8.GetString(b); break; }
            if (member.Fields.TryGetValue(2, out var f2))
                foreach (var o in f2) if (o is byte[] b) { dn = Encoding.UTF8.GetString(b); break; }
            if (string.IsNullOrEmpty(un)) continue;
            if (!contacts.TryGetValue(un, out var row)) contacts[un] = row = new ContactRow { UserName = un };
            if (!string.IsNullOrEmpty(dn)) { row.Alias = dn; }
            // 记录群内显示名
            if (!string.IsNullOrEmpty(dn) && dn != un)
                roomDisplayName[room + "|" + un] = dn;
            else if (roomDisplayName.ContainsKey(room + "|" + un))
                roomDisplayName.Remove(room + "|" + un);
        }
    }

    static readonly Dictionary<string, string> roomDisplayName = new();

    public static string RoomDisplayName(string room, string member)
        => roomDisplayName.TryGetValue(room + "|" + member, out var v) ? v : null;

    static void LoadV3Messages(List<string> msgDbs, Dictionary<string, ContactRow> contacts, Store store, LoadResult res)
    {
        var sessions = new Dictionary<string, SessionInfo>();
        long id = 0;

        foreach (var db in msgDbs)
        {
            try
            {
                using var c = Open(db);
                if (!Tables(c).Contains("MSG")) { res.Warnings.Add($"{Path.GetFileName(db)} 无 MSG 表"); continue; }
                var cols = Columns(c, "MSG");
                bool hasStrTalker = cols.Contains("StrTalker");

                string nameMapSql = "";
                if (!hasStrTalker && Tables(c).Contains("Name2ID"))
                {
                    var n2i = new Dictionary<long, string>();
                    try
                    {
                        using var cmdN = c.CreateCommand();
                        cmdN.CommandText = "SELECT Id, UsrName FROM Name2ID";
                        using var rn = cmdN.ExecuteReader();
                        while (rn.Read()) n2i[L(V(rn, "Id"))] = S(V(rn, "UsrName"));
                    }
                    catch { }
                    store.WxId ??= "";
                    nameMapCache[db] = n2i;
                }

                using var cmd = c.CreateCommand();
                cmd.CommandText = @"SELECT localId, TalkerId, MsgSvrID, Type, SubType, IsSender, CreateTime,
                                           StrContent, CompressContent, BytesExtra
                                    FROM MSG ORDER BY CreateTime ASC, localId ASC";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string talker = hasStrTalker ? S(V(r, "StrTalker")) : "";
                    if (!hasStrTalker && nameMapCache.TryGetValue(db, out var map))
                    {
                        var tid = L(V(r, "TalkerId"));
                        map.TryGetValue(tid, out talker);
                    }
                    if (string.IsNullOrEmpty(talker)) continue;

                    var m = new MsgItem
                    {
                        Id = ++id,
                        Talker = talker,
                        Type = (int)L(V(r, "Type")),
                        SubType = (int)L(V(r, "SubType")),
                        IsSender = L(V(r, "IsSender")) == 1,
                        Time = L(V(r, "CreateTime")),
                        MsgSvrId = L(V(r, "MsgSvrID")),
                        Content = S(V(r, "StrContent")),
                        SourceDb = Path.GetFileName(db),
                    };
                    if (m.IsSender) m.Sender = store.WxId;
                    var extra = V(r, "BytesExtra") as byte[];
                    m.RawExtra = extra;
                    m.ExtraPath = BytesExtraUtil.FindMediaPath(extra);
                    var sender = BytesExtraUtil.FindSender(extra);
                    if (!string.IsNullOrEmpty(sender) && !m.IsSender) m.Sender = sender;

                    var cc = V(r, "CompressContent") as byte[];
                    if (cc is { Length: > 0 } && (m.Type == 49 || string.IsNullOrEmpty(m.Content)))
                    {
                        var un = Z.Unpack(cc);
                        if (un != null) m.Content = Encoding.UTF8.GetString(un);
                    }

                    AddMsg(sessions, store, m);
                }
            }
            catch (Exception ex) { res.Warnings.Add($"读取 {Path.GetFileName(db)} 失败: {ex.Message}"); }
        }

        // 未在消息库中出现的会话（如只有会话列表）
        MergeV3SessionList(msgDbs, sessions, store);
        store.Sessions = sessions.Values.ToList();
        if (string.IsNullOrEmpty(store.WxId))
            store.WxId = GuessSelfV3(sessions, store);
    }

    static readonly Dictionary<string, Dictionary<long, string>> nameMapCache = new();

    static string GuessSelfV3(Dictionary<string, SessionInfo> sessions, Store store)
    {
        foreach (var s in sessions.Values)
            foreach (var m in s.Messages)
                if (m.IsSender && !string.IsNullOrEmpty(m.Sender)) return m.Sender;
        return "";
    }

    static void MergeV3SessionList(List<string> dbFiles, Dictionary<string, SessionInfo> sessions, Store store)
    {
        var mm = dbFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("MicroMsg.db", StringComparison.OrdinalIgnoreCase));
        if (mm == null) return;
        try
        {
            using var c = Open(mm);
            if (!Tables(c).Contains("Session")) return;
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT strUsrName, MAX(nTime) AS t, strContent, nMsgType FROM Session GROUP BY strUsrName";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var talker = S(V(r, "strUsrName"));
                if (string.IsNullOrEmpty(talker)) continue;
                if (!sessions.TryGetValue(talker, out var s))
                {
                    s = new SessionInfo { Talker = talker };
                    sessions[talker] = s;
                }
                var t = L(V(r, "t"));
                if (t > s.LastTime) s.LastTime = t;
                if (string.IsNullOrEmpty(s.LastText)) s.LastText = S(V(r, "strContent"));
            }
        }
        catch { }
    }

    static void AddMsg(Dictionary<string, SessionInfo> sessions, Store store, MsgItem m)
    {
        if (!sessions.TryGetValue(m.Talker, out var s))
            sessions[m.Talker] = s = new SessionInfo
            {
                Talker = m.Talker,
                DisplayName = store.DisplayName.TryGetValue(m.Talker, out var dn) ? dn : m.Talker,
                Remark = store.DisplayName.TryGetValue(m.Talker, out var r2) ? r2 : "",
            };
        if (s.Talker.StartsWith("@chatroom")) s.DisplayName = s.DisplayName == s.Talker ? s.Talker : s.DisplayName;
        if (m.Time > s.LastTime) { s.LastTime = m.Time; s.LastText = m.PreviewText; }
        s.Messages.Add(m);
        s.MsgCount++;
    }

    // ---------- v4 ----------

    static void LoadV4Contacts(string[] dbFiles, Dictionary<string, ContactRow> contacts, LoadResult res)
    {
        var db = dbFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("contact.db", StringComparison.OrdinalIgnoreCase));
        if (db == null) { res.Warnings.Add("未找到 contact.db"); return; }
        using var c = Open(db);
        var tables = Tables(c);
        string table = tables.Contains("contact") ? "contact" : tables.FirstOrDefault(t => t.Contains("Contact", StringComparison.Ordinal));
        if (table == null) { res.Warnings.Add("contact.db 无联系人表"); return; }

        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT username, nick_name, remark FROM [{table}]";
        try
        {
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var un = S(V(r, "username"));
                if (string.IsNullOrEmpty(un)) continue;
                contacts[un] = new ContactRow
                {
                    UserName = un,
                    NickName = S(V(r, "nick_name")),
                    Remark = S(V(r, "remark")),
                };
            }
        }
        catch (Exception ex) { res.Warnings.Add("读取联系人失败: " + ex.Message); }
    }

    static void LoadV4Messages(List<string> msgDbs, Dictionary<string, ContactRow> contacts, Store store, LoadResult res)
    {
        var sessions = new Dictionary<string, SessionInfo>();
        long id = 0;

        foreach (var db in msgDbs)
        {
            try
            {
                using var c = Open(db);
                var tables = Tables(c).Where(t => t.StartsWith("Msg_", StringComparison.OrdinalIgnoreCase)).ToList();

                var n2i = new Dictionary<string, string>();   // 表名 -> username
                if (Tables(c).Contains("Name2Id"))
                {
                    try
                    {
                        using var cmdN = c.CreateCommand();
                        cmdN.CommandText = "SELECT user_name FROM Name2Id";
                        using var rn = cmdN.ExecuteReader();
                        while (rn.Read())
                        {
                            var un = S(V(rn, "user_name"));
                            n2i["Msg_" + Md5Hex(un)] = un;
                        }
                    }
                    catch { }
                }

                foreach (var t in tables)
                {
                    n2i.TryGetValue(t, out var talker);
                    if (string.IsNullOrEmpty(talker)) continue;

                    var cols = Columns(c, t);
                    string sel = "local_id, local_type, create_time, message_content, WCDB_CT_message_content";
                    if (!cols.Contains("WCDB_CT_message_content")) sel = "local_id, local_type, create_time, message_content";
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = $"SELECT {sel} FROM [{t.Replace("'", "''")}] ORDER BY create_time ASC, local_id ASC";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var m = new MsgItem
                        {
                            Id = ++id,
                            Talker = talker,
                            Type = (int)L(V(r, "local_type")),
                            Time = L(V(r, "create_time")),
                            SourceDb = Path.GetFileName(db),
                        };
                        if (m.Time > 100000000000) m.Time /= 1000;   // 毫秒归一化
                        var raw = V(r, "message_content");
                        var ctCol = cols.Contains("WCDB_CT_message_content") ? L(V(r, "WCDB_CT_message_content")) : 0;
                        string content;
                        if (raw is byte[] bb)
                        {
                            if (ctCol == 4) { var un = Z.Unpack(bb); content = un != null ? Encoding.UTF8.GetString(un) : ""; }
                            else content = Encoding.UTF8.GetString(bb);
                        }
                        else content = S(raw);
                        m.Content = content ?? "";

                        // 群聊: "wxid_x:\n内容"
                        if (talker.EndsWith("@chatroom") && m.Content.Contains(":\n"))
                        {
                            int i = m.Content.IndexOf(":\n", StringComparison.Ordinal);
                            var sender = m.Content[..i];
                            if (sender.Length < 64 && !sender.Contains(' '))
                            {
                                m.Sender = sender;
                                m.Content = m.Content[(i + 2)..];
                            }
                        }
                        if (m.Sender == store.WxId || (!string.IsNullOrEmpty(store.WxId) && m.Sender == store.WxId)) m.IsSender = true;

                        AddMsg(sessions, store, m);
                    }
                }
            }
            catch (Exception ex) { res.Warnings.Add($"读取 {Path.GetFileName(db)} 失败: {ex.Message}"); }
        }

        MergeV4SessionList(msgDbs, sessions, store);
        store.Sessions = sessions.Values.ToList();
        if (string.IsNullOrEmpty(store.WxId))
            store.WxId = sessions.Values.SelectMany(s => s.Messages).FirstOrDefault(m => m.IsSender)?.Sender ?? "";
    }

    static void MergeV4SessionList(List<string> dbFiles, Dictionary<string, SessionInfo> sessions, Store store)
    {
        var db = dbFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("session.db", StringComparison.OrdinalIgnoreCase));
        if (db == null) return;
        try
        {
            using var c = Open(db);
            var tables = Tables(c);
            var t = tables.FirstOrDefault(x => x.Equals("SessionTable", StringComparison.OrdinalIgnoreCase))
                 ?? tables.FirstOrDefault(x => x.Contains("Session", StringComparison.Ordinal));
            if (t == null) return;
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT username, summary, last_timestamp FROM [{t}]";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var talker = S(V(r, "username"));
                if (string.IsNullOrEmpty(talker)) continue;
                if (!sessions.TryGetValue(talker, out var s))
                {
                    s = new SessionInfo { Talker = talker };
                    sessions[talker] = s;
                }
                var ts = L(V(r, "last_timestamp"));
                if (ts > 100000000000) ts /= 1000;
                if (ts > s.LastTime) s.LastTime = ts;
                if (string.IsNullOrEmpty(s.LastText)) s.LastText = S(V(r, "summary"));
            }
        }
        catch { }
    }

    public static string Md5Hex(string s)
    {
        var b = MD5.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(b).ToLowerInvariant();
    }

    // ---------- 媒体解析（v3 图片/视频） ----------

    public class MediaIndex
    {
        Dictionary<string, List<string>> byStem = new(StringComparer.OrdinalIgnoreCase);
        public string Root;

        public static MediaIndex Build(string wxDir, string wxId)
        {
            var idx = new MediaIndex { Root = wxDir };
            var roots = new List<string>();
            if (!string.IsNullOrEmpty(wxId)) roots.Add(Path.Combine(wxDir, wxId, "FileStorage"));
            roots.Add(Path.Combine(wxDir, "FileStorage"));
            roots.Add(wxDir);
            foreach (var r in roots)
            {
                if (!Directory.Exists(r)) continue;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(r, "*.dat", SearchOption.AllDirectories))
                        idx.AddFile(f);
                    foreach (var f in Directory.EnumerateFiles(r, "*.mp4", SearchOption.AllDirectories))
                        idx.AddFile(f);
                    break;   // 只用第一个命中的根，避免重复
                }
                catch { }
            }
            return idx;
        }

        void AddFile(string path)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            if (!byStem.TryGetValue(stem, out var l)) byStem[stem] = l = new();
            l.Add(path);
        }

        /// <summary>ExtraPath 形如 THUMBNAIL_DIRPATH://th_xxx 或相对路径</summary>
        public List<string> Resolve(MsgItem m, bool wantThumb)
        {
            var results = new List<string>();
            var p = (m.ExtraPath ?? "").Replace('/', '\\');
            if (string.IsNullOrEmpty(p)) return results;

            if (p.StartsWith("THUMBNAIL_DIRPATH", StringComparison.OrdinalIgnoreCase))
            {
                var stem = Path.GetFileNameWithoutExtension(p);
                if (stem.StartsWith("th_", StringComparison.OrdinalIgnoreCase)) stem = stem[3..];
                // 优先大图
                if (!wantThumb)
                {
                    foreach (var c in new[] { stem, stem + "_h" })
                        if (byStem.TryGetValue(c, out var l)) { results.AddRange(l); break; }
                }
                if (results.Count == 0 && byStem.TryGetValue("th_" + stem, out var lt)) results.AddRange(lt);
                if (results.Count == 0 && wantThumb && byStem.TryGetValue(stem, out var lt2)) results.AddRange(lt2);
                return results;
            }

            // 相对路径
            var name = Path.GetFileNameWithoutExtension(p);
            if (byStem.TryGetValue(name, out var l2)) results.AddRange(l2);
            return results;
        }

        public List<string> ResolveVideo(MsgItem m)
        {
            var results = new List<string>();
            var p = (m.ExtraPath ?? "").Replace('/', '\\');
            if (!string.IsNullOrEmpty(p) && !p.StartsWith("THUMBNAIL", StringComparison.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(p);
                if (byStem.TryGetValue(name, out var l)) results.AddRange(l.Where(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)));
            }
            if (results.Count == 0 && byStem.Count > 0)
            {
                // 按时间月份目录兜底搜索
                try
                {
                    var dt = DateTimeOffset.FromUnixTimeSeconds(m.Time).LocalDateTime;
                    var monthDir = Path.Combine(Root, "FileStorage", "Video", dt.ToString("yyyy-MM"));
                    if (Directory.Exists(monthDir))
                        results.AddRange(Directory.GetFiles(monthDir, "*.mp4", SearchOption.AllDirectories)
                            .Where(f => Math.Abs((new FileInfo(f).CreationTime - dt).TotalDays) < 45)
                            .OrderBy(f => Math.Abs((new FileInfo(f).CreationTime - dt).TotalMinutes)).Take(1));
                }
                catch { }
            }
            return results;
        }
    }
}

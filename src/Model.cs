using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace NiuMa
{
    [DataContract]
    public sealed class Settings
    {
        [DataMember] public int IdleSeconds = 60;
        [DataMember] public int RetentionDays = 90;
        [DataMember] public bool Topmost;
        [DataMember] public bool CloseToTray = true;
        [DataMember] public int SchemaVersion = 2;
        [DataMember] public List<string> Categories = new List<string> { "工作", "沟通", "学习", "娱乐", "其他" };
        [DataMember] public List<AppRule> AppRules = new List<AppRule>();
        [DataMember] public string Theme = "Light";
        [DataMember] public bool SnapMini = true;
        [DataMember] public bool AutoHideMini;
        [DataMember] public bool MiniMode;
        [DataMember] public double? FullLeft, FullTop, MiniLeft, MiniTop;
        [DataMember] public double FullWidth = 492, FullHeight = 744, MiniWidth = 306, MiniHeight = 192;
        private Dictionary<string, AppRule> ruleIndex;
        public int Revision { get; private set; }
        public void Normalize()
        {
            if (IdleSeconds < 15 || IdleSeconds > 1800) IdleSeconds = 60;
            if (RetentionDays < 7 || RetentionDays > 3650) RetentionDays = 90;
            if (SchemaVersion < 2) { SnapMini = true; FullWidth = 492; FullHeight = 744; MiniWidth = 306; MiniHeight = 192; SchemaVersion = 2; }
            if (Categories == null) Categories = new List<string> { "工作", "沟通", "学习", "娱乐", "其他" };
            Categories = Categories.Where(c => !String.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct().Take(12).ToList();
            if (!Categories.Contains("其他")) Categories.Add("其他");
            if (AppRules == null) AppRules = new List<AppRule>();
            AppRules = AppRules.Where(r => r != null && !String.IsNullOrEmpty(r.Id)).GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();
            foreach (AppRule rule in AppRules) if (!Categories.Contains(rule.Category)) rule.Category = "其他";
            if (Theme != "Dark" && Theme != "System") Theme = "Light";
            if (!Finite(FullWidth) || FullWidth < 280 || FullWidth > 2000) FullWidth = 492;
            if (!Finite(FullHeight) || FullHeight < 300 || FullHeight > 1600) FullHeight = 744;
            if (!Finite(MiniWidth) || MiniWidth < 280 || MiniWidth > 650) MiniWidth = 306;
            if (!Finite(MiniHeight) || MiniHeight < 174 || MiniHeight > 450) MiniHeight = 192;
            if (FullLeft.HasValue && !Finite(FullLeft.Value)) FullLeft = null;
            if (FullTop.HasValue && !Finite(FullTop.Value)) FullTop = null;
            if (MiniLeft.HasValue && !Finite(MiniLeft.Value)) MiniLeft = null;
            if (MiniTop.HasValue && !Finite(MiniTop.Value)) MiniTop = null;
            Changed();
        }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        public void Changed() { ruleIndex = null; Revision++; }
        public AppRule Rule(string id)
        {
            if (ruleIndex == null) ruleIndex = AppRules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
            AppRule rule; return id != null && ruleIndex.TryGetValue(id, out rule) ? rule : null;
        }
        public bool Excluded(string id) { AppRule rule = Rule(id); return rule != null && rule.Excluded; }
        public string Category(AppStats app)
        {
            AppRule rule = Rule(app.Id); if (rule != null) return rule.Category;
            string name = (app.Name ?? "").ToLowerInvariant();
            string inferred = name.Contains("微信") || name.Contains("钉钉") || name.Contains("outlook") || name == "qq" ? "沟通" : name.Contains("visual studio") || name.Contains("figma") || name.Contains("word") || name.Contains("excel") || name.Contains("photoshop") || name.Contains("terminal") ? "工作" : "其他";
            return Categories.Contains(inferred) ? inferred : "其他";
        }
        public void Assign(AppStats app, string category, bool excluded)
        {
            AppRule rule = Rule(app.Id);
            if (rule == null) { rule = new AppRule { Id = app.Id }; AppRules.Add(rule); }
            rule.Name = app.Name; rule.Category = Categories.Contains(category) ? category : "其他"; rule.Excluded = excluded; Changed();
        }
        public void RemoveCategory(string name)
        {
            if (name == "其他") return;
            Categories.Remove(name); foreach (AppRule rule in AppRules) if (rule.Category == name) rule.Category = "其他"; Changed();
        }
    }

    [DataContract]
    public sealed class AppRule
    {
        [DataMember] public string Id, Name, Category;
        [DataMember] public bool Excluded;
    }

    [DataContract]
    public class Counters
    {
        [DataMember] public double ActiveSeconds;
        [DataMember] public double ScreenSeconds;
        [DataMember] public long Keys;
        [DataMember] public long Clicks;
        [DataMember] public long LeftClicks;
        [DataMember] public long RightClicks;
        [DataMember] public long MiddleClicks;
        [DataMember] public double Scroll;
        [DataMember] public double MousePixels;
        public void AddInput(InputBatch input)
        {
            Keys += input.Keys; LeftClicks += input.Left; RightClicks += input.Right;
            MiddleClicks += input.Middle; Clicks += input.Left + input.Right + input.Middle;
            Scroll += input.Scroll; MousePixels += input.Distance;
        }
    }

    [DataContract]
    public sealed class AppStats : Counters
    {
        [DataMember] public string Id;
        [DataMember] public string Name;
        [DataMember] public string FilePath;
    }

    [DataContract]
    public sealed class DayStats : Counters
    {
        [DataMember] public string Date;
        [DataMember] public Counters[] Hours;
        [DataMember] public List<AppStats> Apps;
        private Dictionary<string, AppStats> appIndex;
        public DayStats(string date) { Date = date; Normalize(); }
        public void Normalize()
        {
            if (Hours == null || Hours.Length != 24) Hours = new Counters[24];
            for (int i = 0; i < Hours.Length; i++) if (Hours[i] == null) Hours[i] = new Counters();
            if (Apps == null) Apps = new List<AppStats>();
            Apps = Apps.Where(a => a != null && !String.IsNullOrEmpty(a.Id)).ToList();
            appIndex = null;
        }
        public AppStats App(AppIdentity identity)
        {
            if (appIndex == null) appIndex = Apps.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            AppStats app; appIndex.TryGetValue(identity.Id, out app);
            if (app == null)
            {
                app = new AppStats { Id = identity.Id, Name = identity.Name, FilePath = identity.FilePath };
                Apps.Add(app); appIndex[identity.Id] = app;
            }
            return app;
        }
    }

    public sealed class AppIdentity
    {
        public string Id, Name, FilePath;
        public static readonly AppIdentity Unknown = new AppIdentity { Id = "system", Name = "桌面 / 系统", FilePath = "" };
    }

    public sealed class InputBatch
    {
        public DateTime Time;
        public IntPtr Window;
        public long Keys, Left, Right, Middle;
        public double Scroll, Distance;
    }

    public sealed class Store
    {
        public readonly string Root;
        public readonly string DaysDirectory;
        public string Warning = "";
        private readonly HashSet<string> damaged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public Store(string root)
        {
            Root = Path.GetFullPath(root); DaysDirectory = Path.Combine(Root, "days");
            Directory.CreateDirectory(DaysDirectory);
        }
        public static string Key(DateTime date) { return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        public DayStats LoadDay(DateTime date)
        {
            string key = Key(date);
            DayStats day = Read<DayStats>(Path.Combine(DaysDirectory, key + ".json"));
            if (day == null) return new DayStats(key);
            day.Date = key; day.Normalize(); return day;
        }
        public Settings LoadSettings()
        {
            Settings value = Read<Settings>(Path.Combine(Root, "settings.json")) ?? new Settings();
            value.Normalize(); return value;
        }
        public void SaveSettings(Settings settings) { Write(Path.Combine(Root, "settings.json"), settings); }
        public void SaveDay(DayStats day)
        {
            DateTime date;
            if (!DateTime.TryParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) throw new ArgumentException("统计日期无效");
            Write(Path.Combine(DaysDirectory, day.Date + ".json"), day);
        }
        public List<DateTime> ListDates()
        {
            HashSet<DateTime> dates = new HashSet<DateTime>();
            foreach (string path in Directory.GetFiles(DaysDirectory))
            {
                string name = Path.GetFileName(path); DateTime date;
                if ((name.Length == 15 && name.EndsWith(".json")) || (name.Length == 19 && name.EndsWith(".json.bak")))
                    if (DateTime.TryParseExact(name.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) dates.Add(date);
            }
            return dates.OrderBy(d => d).ToList();
        }
        public T Read<T>(string path) where T : class
        {
            foreach (string candidate in new[] { path, path + ".bak" })
            {
                if (!File.Exists(candidate)) continue;
                try
                {
                    using (FileStream stream = File.OpenRead(candidate))
                    {
                        T result = new DataContractJsonSerializer(typeof(T)).ReadObject(stream) as T;
                        if (result == null) continue;
                        if (candidate != path) Warning = "已从备份恢复数据";
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is SerializationException || ex is UnauthorizedAccessException || ex is System.Xml.XmlException)) throw;
                    if (candidate == path) damaged.Add(path);
                    Warning = "部分数据文件无法读取，请查看 data 文件夹";
                }
            }
            return null;
        }
        public void Write<T>(string path, T value)
        {
            string temporary = path + ".tmp";
            using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                stream.Flush(true);
            }
            if (File.Exists(path) && damaged.Contains(path))
            {
                File.Move(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
                File.Move(temporary, path); damaged.Remove(path);
            }
            else if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
            else File.Move(temporary, path);
        }
        public void Prune(int retentionDays, DateTime today)
        {
            DateTime cutoff = today.Date.AddDays(1 - retentionDays);
            foreach (string path in Directory.GetFiles(DaysDirectory))
            {
                string filename = Path.GetFileName(path);
                if (!(filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || filename.EndsWith(".json.bak", StringComparison.OrdinalIgnoreCase) || filename.EndsWith(".json.tmp", StringComparison.OrdinalIgnoreCase))) continue;
                DateTime date;
                if (filename.Length >= 10 && DateTime.TryParseExact(filename.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) && date < cutoff)
                    File.Delete(path);
            }
        }
    }

    public sealed class StatsEngine
    {
        private readonly Store store;
        private readonly Dictionary<string, DayStats> days = new Dictionary<string, DayStats>();
        private readonly HashSet<string> dirty = new HashSet<string>();
        private readonly LinkedList<string> recent = new LinkedList<string>();
        private readonly Dictionary<string, LinkedListNode<string>> nodes = new Dictionary<string, LinkedListNode<string>>();
        private Settings settings;
        public int CachedDayCount { get { return days.Count; } }
        public StatsEngine(Store store, Settings settings = null) { this.store = store; this.settings = settings ?? new Settings(); }
        public DayStats Day(DateTime date)
        {
            string key = Store.Key(date); DayStats day;
            if (!days.TryGetValue(key, out day)) { day = store.LoadDay(date); days[key] = day; }
            LinkedListNode<string> node;
            if (nodes.TryGetValue(key, out node)) recent.Remove(node);
            nodes[key] = recent.AddLast(key); TrimCache(key);
            return day;
        }
        private void TrimCache(string protect = null)
        {
            while (days.Count > 32)
            {
                LinkedListNode<string> candidate = recent.First;
                while (candidate != null && (dirty.Contains(candidate.Value) || candidate.Value == protect)) candidate = candidate.Next;
                if (candidate == null) break;
                string key = candidate.Value; recent.Remove(candidate); nodes.Remove(key); days.Remove(key);
            }
        }
        public void Reload() { days.Clear(); dirty.Clear(); recent.Clear(); nodes.Clear(); }
        // Split at local hour boundaries so a midnight transition belongs to both correct dates.
        public void AddInterval(DateTime start, DateTime end, bool screenOn, DateTime activeUntil, AppIdentity app)
        {
            if (!screenOn || end <= start) return;
            while (start < end)
            {
                DateTime boundary = start.Date.AddHours(start.Hour + 1);
                DateTime stop = boundary < end ? boundary : end;
                double screen = (stop - start).TotalSeconds;
                double active = Math.Max(0, ( (activeUntil < stop ? activeUntil : stop) - start).TotalSeconds);
                if (settings.Excluded((app ?? AppIdentity.Unknown).Id)) active = 0;
                DayStats day = Day(start); Counters hour = day.Hours[start.Hour];
                day.ScreenSeconds += screen; hour.ScreenSeconds += screen;
                day.ActiveSeconds += active; hour.ActiveSeconds += active;
                if (active > 0) day.App(app ?? AppIdentity.Unknown).ActiveSeconds += active;
                dirty.Add(day.Date); start = stop;
            }
        }
        public void AddInput(InputBatch input, AppIdentity app)
        {
            if (settings.Excluded((app ?? AppIdentity.Unknown).Id)) return;
            DayStats day = Day(input.Time);
            day.AddInput(input); day.Hours[input.Time.Hour].AddInput(input); day.App(app ?? AppIdentity.Unknown).AddInput(input);
            dirty.Add(day.Date);
        }
        public void Save()
        {
            foreach (string key in dirty.ToArray()) { store.SaveDay(days[key]); dirty.Remove(key); }
            TrimCache();
        }
        public void Reset(DateTime date)
        {
            DayStats empty = new DayStats(Store.Key(date)); days[empty.Date] = empty; dirty.Add(empty.Date); Save();
        }
    }

    public static class CsvExport
    {
        private static string Field(string value)
        {
            value = value ?? "";
            if (value.Length > 0 && "=+-@\t\r".IndexOf(value[0]) >= 0) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        private static string Number(double value) { return value.ToString("0.##", CultureInfo.InvariantCulture); }
        private static void Row(StringBuilder output, string date, string kind, string label, Counters value)
        {
            output.AppendLine(String.Join(",", new[] { Field(date), Field(kind), Field(label), Number(value.ActiveSeconds), Number(value.ScreenSeconds), Number(value.Keys), Number(value.Clicks), Number(value.LeftClicks), Number(value.RightClicks), Number(value.MiddleClicks), Number(value.Scroll), Number(value.MousePixels) }));
        }
        public static void Save(string path, DayStats day)
        {
            StringBuilder csv = new StringBuilder("日期,类型,项目,活跃秒数,开屏秒数,按键次数,鼠标点击,左键次数,右键次数,中键及侧键次数,滚动格数,移动像素\r\n");
            Row(csv, day.Date, "总计", "全天", day);
            foreach (AppStats app in day.Apps.OrderByDescending(a => a.ActiveSeconds)) Row(csv, day.Date, "软件", app.Name, app);
            for (int i = 0; i < 24; i++) Row(csv, day.Date, "小时", i.ToString("00") + ":00", day.Hours[i]);
            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace NiuMa
{
    [DataContract]
    public sealed class BackupManifest
    {
        [DataMember] public string Format = "niumastats-backup";
        [DataMember] public int Version = 2;
        [DataMember] public string Created = DateTime.Now.ToString("s");
    }
    public sealed class BackupPackage
    {
        public Settings Settings;
        public readonly List<DayStats> Days = new List<DayStats>();
    }
    public sealed class ImportResult { public int Imported, Skipped; public string SafetyBackup; }

    public static class DataManager
    {
        private static T Decode<T>(Stream stream) { return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream); }
        private static void Encode<T>(Stream stream, T value) { new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value); }
        private static void Entry<T>(ZipArchive archive, string name, T value)
        {
            using (Stream output = archive.CreateEntry(name, CompressionLevel.Optimal).Open()) Encode(output, value);
        }
        public static void Backup(Store store, Settings settings, string path)
        {
            string full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full));
            string temporary = full + ".tmp";
            try
            {
                using (FileStream file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    using (ZipArchive zip = new ZipArchive(file, ZipArchiveMode.Create, true))
                    {
                        Entry(zip, "manifest.json", new BackupManifest()); Entry(zip, "settings.json", settings);
                        foreach (DateTime date in store.ListDates()) Entry(zip, "days/" + Store.Key(date) + ".json", store.LoadDay(date));
                    }
                    file.Flush(true);
                }
                if (File.Exists(full)) File.Replace(temporary, full, full + ".bak", true); else File.Move(temporary, full);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public static BackupPackage Inspect(string path)
        {
            BackupPackage package = new BackupPackage(); bool manifestFound = false;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long bytes = 0;
            using (FileStream input = File.OpenRead(path))
            using (ZipArchive zip = new ZipArchive(input, ZipArchiveMode.Read))
            {
                if (zip.Entries.Count > 3662) throw new InvalidDataException("备份包含的文件过多。");
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string name = entry.FullName;
                    if (!seen.Add(name)) throw new InvalidDataException("备份中存在重复文件。");
                    bytes += entry.Length;
                    if (entry.Length > 4 * 1024 * 1024 || bytes > 64 * 1024 * 1024) throw new InvalidDataException("备份数据超过大小限制。");
                    using (Stream data = entry.Open())
                    {
                        if (name == "manifest.json")
                        {
                            BackupManifest manifest = Decode<BackupManifest>(data);
                            if (manifest == null || manifest.Format != "niumastats-backup" || manifest.Version < 1 || manifest.Version > 2) throw new InvalidDataException("这不是兼容的牛马统计备份。");
                            manifestFound = true;
                        }
                        else if (name == "settings.json") { package.Settings = Decode<Settings>(data); if (package.Settings == null) throw new InvalidDataException("设置文件无效。"); package.Settings.Normalize(); }
                        else
                        {
                            DateTime date;
                            if (name.Length != 20 || !name.StartsWith("days/", StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal) || !DateTime.TryParseExact(name.Substring(5, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) throw new InvalidDataException("备份中包含不支持的文件路径。");
                            DayStats day = Decode<DayStats>(data); Validate(day, Store.Key(date)); package.Days.Add(day);
                        }
                    }
                }
            }
            if (!manifestFound || package.Settings == null) throw new InvalidDataException("备份缺少格式说明或设置。");
            package.Days.Sort((a, b) => String.CompareOrdinal(a.Date, b.Date)); return package;
        }
        private static bool ValidNumber(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= 0; }
        private static void ValidateCounters(Counters value)
        {
            if (value == null || !ValidNumber(value.ActiveSeconds) || !ValidNumber(value.ScreenSeconds) || !ValidNumber(value.Scroll) || !ValidNumber(value.MousePixels) || value.Keys < 0 || value.Clicks < 0 || value.LeftClicks < 0 || value.RightClicks < 0 || value.MiddleClicks < 0) throw new InvalidDataException("备份中有无效的统计数值。");
        }
        private static void Validate(DayStats day, string expected)
        {
            DateTime date;
            if (!DateTime.TryParseExact(expected, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) throw new InvalidDataException("每日统计日期无效。");
            if (day == null || day.Date != expected || day.Hours == null || day.Hours.Length != 24 || day.Apps == null || day.Apps.Count > 1500) throw new InvalidDataException("每日统计结构不完整。");
            ValidateCounters(day); foreach (Counters hour in day.Hours) ValidateCounters(hour);
            if (day.ScreenSeconds > 90000 || day.ActiveSeconds > day.ScreenSeconds + 0.1) throw new InvalidDataException("每日时长超出有效范围。");
            foreach (Counters hour in day.Hours) if (hour.ScreenSeconds > 7200 || hour.ActiveSeconds > hour.ScreenSeconds + 0.1) throw new InvalidDataException("小时统计超出有效范围。");
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AppStats app in day.Apps)
            {
                ValidateCounters(app);
                if (app.ActiveSeconds > 90000 || app.Keys > 1000000000 || app.Clicks > 1000000000 || app.Scroll > 1000000000 || app.MousePixels > 1e13) throw new InvalidDataException("软件统计超出有效范围。");
                if (String.IsNullOrEmpty(app.Id) || app.Id.Length > 32768 || !ids.Add(app.Id) || (app.Name != null && app.Name.Length > 512)) throw new InvalidDataException("备份中有无效的软件信息。");
            }
            day.Normalize();
        }
        private static string SafetyBackup(Store store, Settings settings, string reason)
        {
            string path = Path.Combine(store.Root, "backups", reason + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".zip");
            Backup(store, settings, path); return path;
        }
        private static void RestoreBytes(string path, byte[] bytes)
        {
            if (bytes == null) { if (File.Exists(path)) File.Delete(path); return; }
            string temporary = path + ".rollback"; File.WriteAllBytes(temporary, bytes);
            if (File.Exists(path))
            {
                string backup = path + ".rollback-backup";
                File.Replace(temporary, path, backup, true); if (File.Exists(backup)) File.Delete(backup);
            }
            else File.Move(temporary, path);
        }
        public static ImportResult Restore(Store store, Settings current, BackupPackage package, bool overwrite, bool includeSettings)
        {
            // Validate the complete package before touching any destination file.
            foreach (DayStats day in package.Days) Validate(day, day.Date);
            HashSet<string> existing = new HashSet<string>(store.ListDates().Select(Store.Key));
            List<DayStats> chosen = package.Days.Where(d => overwrite || !existing.Contains(d.Date)).ToList();
            ImportResult result = new ImportResult { Imported = chosen.Count, Skipped = package.Days.Count - chosen.Count };
            if (chosen.Count == 0 && !includeSettings) return result;
            result.SafetyBackup = SafetyBackup(store, current, "before-restore");
            Dictionary<string, byte[]> originals = new Dictionary<string, byte[]>();
            foreach (DayStats day in chosen)
            {
                string path = Path.Combine(store.DaysDirectory, day.Date + ".json");
                originals[path] = File.Exists(path) ? File.ReadAllBytes(path) : null;
                originals[path + ".bak"] = File.Exists(path + ".bak") ? File.ReadAllBytes(path + ".bak") : null;
            }
            string settingsPath = Path.Combine(store.Root, "settings.json");
            if (includeSettings)
            {
                originals[settingsPath] = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
                originals[settingsPath + ".bak"] = File.Exists(settingsPath + ".bak") ? File.ReadAllBytes(settingsPath + ".bak") : null;
            }
            try { foreach (DayStats day in chosen) store.SaveDay(day); if (includeSettings) store.SaveSettings(package.Settings); }
            catch
            {
                foreach (KeyValuePair<string, byte[]> file in originals)
                {
                    byte[] currentBytes = File.Exists(file.Key) ? File.ReadAllBytes(file.Key) : null;
                    if (file.Value == null && currentBytes == null) continue;
                    if (file.Value != null && currentBytes != null && file.Value.SequenceEqual(currentBytes)) continue;
                    RestoreBytes(file.Key, file.Value);
                }
                throw;
            }
            return result;
        }
        public static int ClearBefore(Store store, Settings settings, DateTime cutoff)
        {
            List<DateTime> dates = store.ListDates().Where(d => d < cutoff.Date && d < DateTime.Today).ToList();
            if (dates.Count == 0) return 0;
            SafetyBackup(store, settings, "before-cleanup");
            foreach (DateTime date in dates)
            {
                string path = Path.Combine(store.DaysDirectory, Store.Key(date) + ".json");
                foreach (string candidate in new[] { path, path + ".bak", path + ".tmp" }) if (File.Exists(candidate)) File.Delete(candidate);
            }
            return dates.Count;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.IO.Compression;
using System.Runtime.Serialization.Json;

namespace NiuMa
{
    internal static class DemoData
    {
        public static void Fill(StatsEngine engine)
        {
            AppIdentity code = new AppIdentity { Id = "demo-code", Name = "Visual Studio Code", FilePath = "" };
            AppIdentity chrome = new AppIdentity { Id = "demo-chrome", Name = "Google Chrome", FilePath = "" };
            AppIdentity figma = new AppIdentity { Id = "demo-figma", Name = "Figma", FilePath = "" };
            AppIdentity chat = new AppIdentity { Id = "demo-chat", Name = "微信", FilePath = "" };
            AppIdentity[] apps = { code, chrome, figma, chat };
            int[] minutes = { 104, 62, 38, 18 };
            long[] keys = { 1642, 533, 276, 395 }, clicks = { 348, 265, 472, 199 };
            DateTime cursor = DateTime.Today.AddHours(9).AddMinutes(7);
            for (int i = 0; i < apps.Length; i++)
            {
                DateTime end = cursor.AddMinutes(minutes[i]); engine.AddInterval(cursor, end, true, end, apps[i]);
                engine.AddInput(new InputBatch { Time = cursor, Keys = keys[i], Left = clicks[i] - 20, Right = 17, Middle = 3, Scroll = 260 + i * 160, Distance = 30500 + i * 22000 }, apps[i]);
                cursor = end;
                DateTime breakEnd = cursor.AddMinutes(i == 0 ? 12 : i == 1 ? 10 : 9); engine.AddInterval(cursor, breakEnd, true, cursor, apps[i]); cursor = breakEnd;
            }
            int[] previousMinutes = { 273, 351, 187, 312, 59, 108 };
            for (int i = 0; i < 6; i++)
            {
                DateTime start = DateTime.Today.AddDays(i - 6).AddHours(9); DateTime end = start.AddMinutes(previousMinutes[i]);
                engine.AddInterval(start, end.AddMinutes(37), true, end, code);
                engine.AddInput(new InputBatch { Time = start, Keys = 2000 + i * 143, Left = 630 + i * 23, Scroll = 150 + i * 42, Distance = 77000 + i * 1777 }, code);
            }
        }
    }

    internal static class SelfTests
    {
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static void Near(double actual, double expected, string message) { Assert(Math.Abs(actual - expected) < 0.001, message + ": " + actual + " != " + expected); }
        internal static void OnInputThread(Action action)
        {
            Exception failure = null;
            Thread worker = new Thread(delegate() { try { action(); } catch (Exception ex) { failure = ex; } }) { IsBackground = true };
            worker.Start(); Assert(worker.Join(2000), "input worker completed");
            if (failure != null) throw new InvalidOperationException("input worker failed", failure);
        }
        internal static bool PumpUntil(Func<bool> condition, int milliseconds)
        {
            if (condition()) return true;
            DispatcherFrame frame = new DispatcherFrame();
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            DispatcherTimer poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
            poll.Tick += delegate { if (condition() || watch.ElapsedMilliseconds >= milliseconds) frame.Continue = false; };
            poll.Start(); try { Dispatcher.PushFrame(frame); } finally { poll.Stop(); }
            return condition();
        }
        public static int Run(string reportPath)
        {
            List<string> results = new List<string>(); int failures = 0;
            string report = Path.GetFullPath(reportPath); Directory.CreateDirectory(Path.GetDirectoryName(report));
            string testRoot = Path.Combine(Path.GetDirectoryName(report), "test-data-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(testRoot);
            AppIdentity app = new AppIdentity { Id = "test.exe", Name = "测试软件", FilePath = "C:\\test.exe" };
            Action<string, Action> test = delegate(string name, Action action)
            {
                try { action(); results.Add("PASS " + name); }
                catch (Exception ex) { failures++; results.Add("FAIL " + name + "\n" + ex); }
            };
            test("midnight and hour boundaries preserve all seconds", delegate
            {
                Store store = new Store(Path.Combine(testRoot, "midnight")); StatsEngine engine = new StatsEngine(store);
                DateTime start = new DateTime(2026, 10, 11, 23, 59, 58); DateTime end = start.AddSeconds(5);
                engine.AddInterval(start, end, true, end, app);
                Near(engine.Day(start).ActiveSeconds, 2, "previous day"); Near(engine.Day(end).ActiveSeconds, 3, "next day");
                Near(engine.Day(start).Hours[23].ScreenSeconds, 2, "hour 23"); Near(engine.Day(end).Hours[0].ScreenSeconds, 3, "hour 0");
                Near(engine.Day(end).Apps[0].ActiveSeconds, 3, "software attribution");
            });
            test("idle threshold clips activity but preserves screen time", delegate
            {
                StatsEngine engine = new StatsEngine(new Store(Path.Combine(testRoot, "idle"))); DateTime start = new DateTime(2026, 10, 12, 10, 59, 59);
                engine.AddInterval(start, start.AddSeconds(4), true, start.AddSeconds(1.5), app);
                DayStats day = engine.Day(start); Near(day.ScreenSeconds, 4, "screen"); Near(day.ActiveSeconds, 1.5, "active");
                Near(day.Hours[10].ActiveSeconds, 1, "first hour"); Near(day.Hours[11].ActiveSeconds, 0.5, "next hour");
                engine.AddInterval(start.AddMinutes(1), start.AddMinutes(2), true, start, app);
                Near(day.ScreenSeconds, 64, "idle screen"); Near(day.ActiveSeconds, 1.5, "no idle activity");
            });
            test("screen-off and invalid intervals add no usage", delegate
            {
                StatsEngine engine = new StatsEngine(new Store(Path.Combine(testRoot, "off"))); DateTime now = DateTime.Today;
                engine.AddInterval(now, now.AddHours(4), false, now.AddHours(4), app); engine.AddInterval(now, now.AddSeconds(-1), true, now, app);
                Near(engine.Day(now).ScreenSeconds, 0, "off"); Assert(engine.Day(now).Apps.Count == 0, "no app created");
            });
            test("input aggregates agree across day, hour and software", delegate
            {
                StatsEngine engine = new StatsEngine(new Store(Path.Combine(testRoot, "inputs"))); DateTime now = DateTime.Today.AddHours(13);
                engine.AddInput(new InputBatch { Time = now, Keys = 17, Left = 8, Right = 3, Middle = 2, Scroll = 2.5, Distance = 345.75 }, app);
                DayStats day = engine.Day(now); Assert(day.Keys == 17 && day.Clicks == 13, "day counts");
                Assert(day.Apps[0].Keys == day.Hours[13].Keys && day.Apps[0].Clicks == day.Clicks, "aligned input counts");
                Near(day.Scroll, 2.5, "fractional wheel"); Near(day.MousePixels, 345.75, "distance");
            });
            test("atomic save round-trip and corrupted-primary recovery", delegate
            {
                Store store = new Store(Path.Combine(testRoot, "recovery")); StatsEngine engine = new StatsEngine(store); DateTime now = DateTime.Today;
                engine.AddInterval(now, now.AddSeconds(45), true, now.AddSeconds(45), app); engine.Save();
                engine.AddInput(new InputBatch { Time = now, Keys = 8 }, app); engine.Save();
                DayStats restored = new StatsEngine(store).Day(now); Near(restored.ActiveSeconds, 45, "round-trip time"); Assert(restored.Keys == 8, "round-trip input");
                string path = Path.Combine(store.DaysDirectory, Store.Key(now) + ".json");
                File.WriteAllText(path, "{ broken"); DayStats recovered = new Store(store.Root).LoadDay(now); Near(recovered.ActiveSeconds, 45, "backup recovered");
                Assert(File.Exists(path + ".bak"), "backup exists"); Assert(!File.Exists(path + ".tmp"), "no leftover temporary");
            });
            test("retention deletes only dated statistic files in range", delegate
            {
                Store store = new Store(Path.Combine(testRoot, "retention")); DateTime today = new DateTime(2026, 10, 12);
                store.SaveDay(new DayStats(Store.Key(today.AddDays(-7)))); store.SaveDay(new DayStats(Store.Key(today.AddDays(-6))));
                File.WriteAllText(Path.Combine(store.DaysDirectory, "notes.txt"), "keep"); store.Prune(7, today);
                Assert(!File.Exists(Path.Combine(store.DaysDirectory, Store.Key(today.AddDays(-7)) + ".json")), "old removed");
                Assert(File.Exists(Path.Combine(store.DaysDirectory, Store.Key(today.AddDays(-6)) + ".json")), "boundary kept"); Assert(File.Exists(Path.Combine(store.DaysDirectory, "notes.txt")), "unrelated preserved");
            });
            test("CSV preserves Chinese, quotes and neutralizes formulas", delegate
            {
                DayStats day = new DayStats("2026-10-12"); day.App(new AppIdentity { Id = "danger", Name = "=SUM(1,2)\"测试", FilePath = "" }).Keys = 3;
                string csvPath = Path.Combine(testRoot, "export.csv"); CsvExport.Save(csvPath, day); byte[] bytes = File.ReadAllBytes(csvPath); string csv = File.ReadAllText(csvPath);
                Assert(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "UTF8 BOM"); Assert(csv.Contains("\"'=SUM(1,2)\"\"测试\""), "formula and quote handling");
                Assert(csv.Split('\n').Length == 28, "summary + software + 24 hours");
            });
            test("foreground software totals equal daily active totals", delegate
            {
                StatsEngine engine = new StatsEngine(new Store(Path.Combine(testRoot, "demo"))); DemoData.Fill(engine); DayStats day = engine.Day(DateTime.Today);
                Near(day.ActiveSeconds, 222 * 60, "demo active"); Near(day.ScreenSeconds, 262 * 60, "demo screen");
                Near(day.Apps.Sum(a => a.ActiveSeconds), day.ActiveSeconds, "app sum"); Near(day.Hours.Sum(h => h.ActiveSeconds), day.ActiveSeconds, "hour sum"); Assert(day.Keys == 2846 && day.Clicks == 1284, "demo inputs");
            });
            test("native APIs resolve foreground and report idle clock", delegate
            {
                Assert(Native.IdleSeconds() >= 0, "idle nonnegative"); AppIdentity foreground = new AppResolver().Resolve(Native.GetForegroundWindow()); Assert(foreground != null && foreground.Id != null, "foreground identity");
            });
            test("legacy settings migrate without losing user preferences", delegate
            {
                Store store = new Store(Path.Combine(testRoot, "legacy"));
                File.WriteAllText(Path.Combine(store.Root, "settings.json"), "{\"CloseToTray\":true,\"IdleSeconds\":120,\"RetentionDays\":30,\"Topmost\":true}");
                Settings settings = store.LoadSettings(); Assert(settings.Topmost && settings.IdleSeconds == 120 && settings.RetentionDays == 30, "old preferences");
                Assert(settings.SnapMini && settings.Categories.Count == 5 && settings.Theme == "Light" && settings.FullWidth == 492 && settings.AppRules.Count == 0, "new defaults");
                store.SaveSettings(settings); Assert(store.LoadSettings().SchemaVersion == 2, "schema persisted");
            });
            test("category reassignment updates history and category deletion falls back", delegate
            {
                Settings settings = new Settings(); AppStats stats = new AppStats { Id = "Code.exe", Name = "Visual Studio Code" };
                Assert(settings.Category(stats) == "工作", "inferred category"); settings.Categories.Add("创作"); settings.Assign(stats, "创作", false);
                Assert(settings.Category(stats) == "创作" && settings.Rule("code.exe") != null, "custom category case insensitive");
                settings.RemoveCategory("创作"); Assert(settings.Category(stats) == "其他", "deleted category fallback");
            });
            test("excluded software adds screen time but no active or input totals", delegate
            {
                Settings settings = new Settings(); settings.Assign(new AppStats { Id = app.Id, Name = app.Name }, "其他", true);
                StatsEngine engine = new StatsEngine(new Store(Path.Combine(testRoot, "excluded")), settings); DateTime now = DateTime.Today;
                engine.AddInterval(now, now.AddSeconds(25), true, now.AddSeconds(25), app); engine.AddInput(new InputBatch { Time = now, Keys = 5, Left = 3 }, app);
                DayStats day = engine.Day(now); Near(day.ScreenSeconds, 25, "screen preserved"); Near(day.ActiveSeconds, 0, "no active"); Assert(day.Keys == 0 && day.Clicks == 0 && day.Apps.Count == 0, "no inputs or apps");
                settings.Rule(app.Id).Excluded = false; settings.Changed(); engine.AddInput(new InputBatch { Time = now, Keys = 2 }, app); Assert(day.Keys == 2, "recording restored");
            });
            test("history cache stays bounded without dropping unsaved days", delegate
            {
                Store store = new Store(Path.Combine(testRoot, "cache")); StatsEngine engine = new StatsEngine(store);
                for (int i = 0; i < 180; i++) engine.Day(DateTime.Today.AddDays(-i)); Assert(engine.CachedDayCount <= 32, "history bound");
                DateTime now = DateTime.Today; engine.AddInput(new InputBatch { Time = now, Keys = 9 }, app);
                for (int i = 180; i < 250; i++) engine.Day(now.AddDays(-i)); engine.Save(); Assert(store.LoadDay(now).Keys == 9, "dirty day saved");
            });
            test("ZIP backup restores missing days and explicitly overwrites conflicts", delegate
            {
                Store source = new Store(Path.Combine(testRoot, "backup-source")); Settings settings = new Settings(); settings.Assign(new AppStats { Id = app.Id, Name = app.Name }, "工作", true);
                DateTime now = DateTime.Today; DayStats first = new DayStats(Store.Key(now.AddDays(-1))); first.Keys = 21; source.SaveDay(first); DayStats second = new DayStats(Store.Key(now)); second.Keys = 35; source.SaveDay(second);
                string zip = Path.Combine(testRoot, "backup.zip"); DataManager.Backup(source, settings, zip); BackupPackage package = DataManager.Inspect(zip); Assert(package.Days.Count == 2 && package.Settings.Excluded(app.Id), "backup contents");
                Store target = new Store(Path.Combine(testRoot, "backup-target")); DayStats existing = new DayStats(Store.Key(now)); existing.Keys = 99; target.SaveDay(existing);
                ImportResult result = DataManager.Restore(target, new Settings(), package, false, false); Assert(result.Imported == 1 && result.Skipped == 1 && File.Exists(result.SafetyBackup), "missing only"); Assert(target.LoadDay(now).Keys == 99, "local preserved");
                result = DataManager.Restore(target, new Settings(), package, true, true); Assert(target.LoadDay(now).Keys == 35 && target.LoadSettings().Excluded(app.Id), "overwrite and settings");
            });
            test("restore rollback returns every changed file after a write failure", delegate
            {
                Store store = new Store(Path.Combine(testRoot, "rollback")); DateTime today = DateTime.Today;
                DayStats first = new DayStats(Store.Key(today.AddDays(-1))) { Keys = 71 }; DayStats second = new DayStats(Store.Key(today)) { Keys = 72 }; store.SaveDay(first); store.SaveDay(second);
                BackupPackage package = new BackupPackage { Settings = new Settings() }; package.Days.Add(new DayStats(first.Date) { Keys = 1 }); package.Days.Add(new DayStats(second.Date) { Keys = 2 });
                string blocked = Path.Combine(store.DaysDirectory, second.Date + ".json.tmp"); bool failed = false;
                using (FileStream block = new FileStream(blocked, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    try { DataManager.Restore(store, new Settings(), package, true, false); } catch (IOException) { failed = true; }
                }
                Assert(failed, "expected failure"); Assert(store.LoadDay(today.AddDays(-1)).Keys == 71 && store.LoadDay(today).Keys == 72, "all originals restored");
            });
            test("ZIP validation rejects path traversal and invalid numeric values", delegate
            {
                string zipPath = Path.Combine(testRoot, "bad-backup.zip");
                using (FileStream file = File.Create(zipPath)) using (ZipArchive zip = new ZipArchive(file, ZipArchiveMode.Create))
                using (Stream entry = zip.CreateEntry("../outside.json").Open()) using (StreamWriter writer = new StreamWriter(entry)) writer.Write("{}");
                bool failed = false; try { DataManager.Inspect(zipPath); } catch (InvalidDataException) { failed = true; } Assert(failed, "traversal rejected");
                BackupPackage bad = new BackupPackage { Settings = new Settings() }; bad.Days.Add(new DayStats(Store.Key(DateTime.Today)) { ActiveSeconds = Double.NaN });
                failed = false; try { DataManager.Restore(new Store(Path.Combine(testRoot, "invalid")), new Settings(), bad, true, false); } catch (InvalidDataException) { failed = true; } Assert(failed, "NaN rejected");
            });
            test("manual cleanup creates recoverable backup and leaves today", delegate
            {
                Store store = new Store(Path.Combine(testRoot, "cleanup")); store.SaveDay(new DayStats(Store.Key(DateTime.Today.AddDays(-5))) { Keys = 47 }); store.SaveDay(new DayStats(Store.Key(DateTime.Today)) { Keys = 10 });
                Assert(DataManager.ClearBefore(store, new Settings(), DateTime.Today.AddDays(1)) == 1, "one old date cleaned"); Assert(store.LoadDay(DateTime.Today).Keys == 10, "today intact");
                string zip = Directory.GetFiles(Path.Combine(store.Root, "backups"), "*.zip").Single(); Assert(DataManager.Inspect(zip).Days.Count == 2, "safe backup contains both days");
            });
            test("mini placement fits disconnected displays and snaps within threshold", delegate
            {
                Rect work = new Rect(0, 0, 1440, 900); Rect fit = Placement.Fit(new Rect(-5000, -800, 306, 192), work); Assert(fit.Left == 0 && fit.Top == 0, "offscreen clamped");
                Rect snap = Placement.Snap(new Rect(1130, 12, 306, 192), work, 18); Assert(snap.Right == 1440 && snap.Top == 0, "edge snap");
                Rect stay = Placement.Snap(new Rect(500, 250, 306, 192), work, 18); Assert(stay.Left == 500 && stay.Top == 250, "central unchanged");
            });
            test("daily report PNG supports populated and empty days", delegate
            {
                StatsEngine engine = new StatsEngine(new Store(Path.Combine(testRoot, "report"))); DemoData.Fill(engine);
                var image = ReportBuilder.Build(engine.Day(DateTime.Today), new Settings()); Assert(image.PixelWidth == 1120 && image.PixelHeight == 1520 && image.IsFrozen, "report size");
                string png = Path.Combine(testRoot, "report.png"); ReportBuilder.Save(image, png); Assert(new FileInfo(png).Length > 10000, "report saved");
                var empty = ReportBuilder.Build(new DayStats(Store.Key(DateTime.Today)), new Settings()); Assert(empty.PixelWidth == 1120, "empty report");
            });
            test("real Windows hooks, pause, lock and suspend lifecycle", delegate
            {
                StatsEngine engine = new StatsEngine(new Store(Path.Combine(testRoot, "native")));
                Window owner = new Window { Width = 100, Height = 100, Left = -12000, Top = -12000, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
                Tracker tracker = new Tracker(engine, new Settings());
                try
                {
                    owner.Show(); tracker.Start(owner); Assert(tracker.HookHealthy, tracker.HookError);
                    Thread.Sleep(100); tracker.Tick(); DayStats day = engine.Day(DateTime.Today);
                    if (tracker.ScreenAvailable) Assert(day.ScreenSeconds > 0.05, "real screen seconds");
                    tracker.TogglePause(); double paused = day.ScreenSeconds; Thread.Sleep(100); tracker.Tick(); Near(day.ScreenSeconds, paused, "paused frozen");
                    tracker.TogglePause();
                    IntPtr handle = new WindowInteropHelper(owner).Handle;
                    Native.SendMessage(handle, 0x02B1, new IntPtr(7), IntPtr.Zero);
                    double locked = day.ScreenSeconds; Thread.Sleep(100); tracker.Tick(); Near(day.ScreenSeconds, locked, "lock frozen"); Assert(!tracker.ScreenAvailable, "locked state");
                    Native.SendMessage(handle, 0x02B1, new IntPtr(8), IntPtr.Zero);
                    Native.SendMessage(handle, 0x0218, new IntPtr(4), IntPtr.Zero);
                    double suspended = day.ScreenSeconds; Thread.Sleep(100); tracker.Tick(); Near(day.ScreenSeconds, suspended, "suspend frozen"); Assert(!tracker.ScreenAvailable, "suspend state");
                    Native.SendMessage(handle, 0x0218, new IntPtr(18), IntPtr.Zero);
                    Thread.Sleep(100); tracker.Tick(); if (tracker.ScreenAvailable) Assert(day.ScreenSeconds > suspended, "resumed recording");
                    engine.Save(); Assert(File.Exists(Path.Combine(testRoot, "native", "days", Store.Key(DateTime.Today) + ".json")), "native stats saved");
                }
                finally { tracker.Dispose(); owner.Close(); }
                Assert(!tracker.HookHealthy, "hooks disposed");
            });
            test("input batches reach UI dispatcher without the one-second sampler", delegate
            {
                Store store = new Store(Path.Combine(testRoot, "live-input")); Settings settings = new Settings(); StatsEngine engine = new StatsEngine(store, settings);
                Tracker tracker = new Tracker(engine, settings, delegate { return IntPtr.Zero; }); DayStats day = engine.Day(DateTime.Today);
                Dispatcher uiDispatcher = Dispatcher.CurrentDispatcher;
                int notifications = 0;
                tracker.InputChanged += delegate { Assert(uiDispatcher.CheckAccess(), "UI dispatcher access"); notifications++; };
                try
                {
                    System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                    OnInputThread(delegate
                    {
                        tracker.RecordKeyboard(0x100, new Native.KeyHook { Key = 65 }); tracker.RecordKeyboard(0x100, new Native.KeyHook { Key = 65 }); tracker.RecordKeyboard(0x101, new Native.KeyHook { Key = 65 });
                        tracker.RecordKeyboard(0x104, new Native.KeyHook { Key = 18 }); tracker.RecordKeyboard(0x105, new Native.KeyHook { Key = 18 });
                        tracker.RecordKeyboard(0x100, new Native.KeyHook { Key = 90, Flags = 0x10 });
                        tracker.RecordMouse(0x201, new Native.MouseHook()); tracker.RecordMouse(0x204, new Native.MouseHook()); tracker.RecordMouse(0x207, new Native.MouseHook()); tracker.RecordMouse(0x20B, new Native.MouseHook());
                        tracker.RecordMouse(0x20A, new Native.MouseHook { MouseData = 120u << 16 }); tracker.RecordMouse(0x20E, new Native.MouseHook { MouseData = unchecked((uint)(-240 << 16)) });
                        tracker.RecordMouse(0x200, new Native.MouseHook { Point = new Native.Point { X = 0, Y = 0 } });
                        tracker.RecordMouse(0x200, new Native.MouseHook { Point = new Native.Point { X = 3, Y = 4 } });
                        tracker.RecordMouse(0x200, new Native.MouseHook { Flags = 1, Point = new Native.Point { X = 1000, Y = 1000 } });
                        tracker.RecordMouse(0x200, new Native.MouseHook { Point = new Native.Point { X = 6, Y = 8 } });
                        for (int i = 0; i < 100; i++) { tracker.RecordKeyboard(0x100, new Native.KeyHook { Key = 66 }); tracker.RecordKeyboard(0x101, new Native.KeyHook { Key = 66 }); }
                        for (int i = 0; i < 200; i++) tracker.RecordMouse(0x201, new Native.MouseHook());
                        tracker.RecordMouse(0x201, new Native.MouseHook { Flags = 1 });
                    });
                    Assert(PumpUntil(delegate { return day.Keys == 102 && day.Clicks == 204; }, 500), "live counts within 500 ms without Tick");
                    double latency = watch.Elapsed.TotalMilliseconds;
                    Assert(notifications == 1, "burst coalesced into one notification"); Near(day.Scroll, 3, "both wheel directions"); Near(day.MousePixels, 10, "injected move ignored"); Near(day.ScreenSeconds, 0, "input flush never samples duration");
                    Assert(day.Hours[DateTime.Now.Hour].Keys == 102 && day.Apps.Single().Clicks == 204, "hour and app updated immediately");
                    PumpUntil(delegate { return false; }, 120); Assert(notifications == 1, "no idle notifications");
                    tracker.TogglePause(); OnInputThread(delegate { tracker.RecordKeyboard(0x100, new Native.KeyHook { Key = 67 }); tracker.RecordKeyboard(0x101, new Native.KeyHook { Key = 67 }); tracker.RecordMouse(0x201, new Native.MouseHook()); });
                    PumpUntil(delegate { return false; }, 120); Assert(day.Keys == 102 && day.Clicks == 204 && notifications == 1, "paused input ignored"); tracker.TogglePause();
                    settings.Assign(day.App(AppIdentity.Unknown), "其他", true);
                    OnInputThread(delegate { tracker.RecordKeyboard(0x100, new Native.KeyHook { Key = 67 }); tracker.RecordKeyboard(0x101, new Native.KeyHook { Key = 67 }); tracker.RecordMouse(0x201, new Native.MouseHook()); });
                    Assert(PumpUntil(delegate { return notifications == 2; }, 500), "excluded batch consumed"); Assert(day.Keys == 102 && day.Clicks == 204, "excluded counts ignored");
                    settings.Rule(AppIdentity.Unknown.Id).Excluded = false; settings.Changed();
                    OnInputThread(delegate { tracker.RecordKeyboard(0x100, new Native.KeyHook { Key = 67 }); tracker.RecordKeyboard(0x101, new Native.KeyHook { Key = 67 }); tracker.RecordMouse(0x201, new Native.MouseHook()); });
                    Assert(PumpUntil(delegate { return day.Keys == 103 && day.Clicks == 205; }, 500), "counts resume");
                    int beforeDispose = notifications;
                    OnInputThread(delegate { tracker.RecordKeyboard(0x100, new Native.KeyHook { Key = 68 }); tracker.RecordKeyboard(0x101, new Native.KeyHook { Key = 68 }); });
                    tracker.Dispose(); PumpUntil(delegate { return false; }, 120);
                    Assert(day.Keys == 104 && notifications == beforeDispose, "dispose drains pending input without late UI callbacks");
                    engine.Save(); Assert(store.LoadDay(DateTime.Today).Keys == 104, "live counts persisted");
                    File.WriteAllText(Path.Combine(testRoot, "input-latency.txt"), "Input burst to model/UI event: " + latency.ToString("0.0") + " ms");
                }
                finally { tracker.Dispose(); }
            });
            test("input listener recovery distinguishes silence from idle and tick rollover", delegate
            {
                Assert(Tracker.InputHooksStale(10000, 9500, 2000), "fresh OS input with silent hooks triggers recovery");
                Assert(!Tracker.InputHooksStale(10000, 1000, 2000), "idle does not trigger recovery");
                Assert(!Tracker.InputHooksStale(10000, 9500, 9400), "working callbacks remain installed");
                Assert(!Tracker.InputHooksStale(10000, 11000, 2000), "future synthetic timestamp ignored");
                Assert(Tracker.InputHooksStale(500, 200, unchecked(500u - 5000u)), "GetTickCount rollover handled");
            });
            test("UI reuses rows, filters categories, caps icons and expands dock", delegate
            {
                Application application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                Store store = new Store(Path.Combine(testRoot, "ui")); Settings settings = new Settings(); StatsEngine engine = new StatsEngine(store, settings); DemoData.Fill(engine);
                Shell shell = new Shell(application, engine, store, settings, true, null, "apps", 0);
                try { shell.VerifyUi(delegate(bool condition, string message) { Assert(condition, message); }, Path.Combine(testRoot, "ui-benchmark.txt")); }
                finally { shell.Exit(); }
            });
            results.Add("\n" + (results.Count - failures) + " passed, " + failures + " failed");
            File.WriteAllText(report, String.Join(Environment.NewLine, results), new UTF8Encoding(false)); return failures == 0 ? 0 : 1;
        }
    }
}

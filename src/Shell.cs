using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace NiuMa
{
    internal sealed partial class Shell
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string message);
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        private readonly Application application;
        private readonly StatsEngine engine;
        private readonly Store store;
        private readonly Settings settings;
        private readonly bool demo;
        private readonly string renderPath, initialView;
        private readonly uint showMessage;
        private readonly Window window;
        private readonly DispatcherTimer timer;
        private readonly Dictionary<string, ImageSource> icons = new Dictionary<string, ImageSource>();
        private readonly Queue<string> iconOrder = new Queue<string>();
        private bool darkTheme;
        private int visualTick;
        private DateTime lastTrendRefresh = DateTime.MinValue;
        private Tracker tracker;
        private Forms.NotifyIcon tray;
        private Forms.ToolStripMenuItem trayPause;
        private DateTime selectedDate = DateTime.Today;
        private DateTime lastSave = DateTime.Now, lastPrune = DateTime.Today;
        private bool exiting, compact, dateFollowing = true;
        private double fullWidth = 492, fullHeight = 744;
        private string selectedTab = "overview", inputWarning = "";
        private readonly CultureInfo chinese = CultureInfo.GetCultureInfo("zh-CN");

        public Shell(Application application, StatsEngine engine, Store store, Settings settings, bool demo, string renderPath, string initialView, uint showMessage)
        {
            this.application = application; this.engine = engine; this.store = store; this.settings = settings;
            this.demo = demo; this.renderPath = renderPath; this.initialView = initialView; this.showMessage = showMessage;
            if (initialView == "dark" || initialView == "mini-dark") settings.Theme = "Dark";
            darkTheme = Palette.IsDark(settings);
            using (Stream xaml = Assembly.GetExecutingAssembly().GetManifestResourceStream("NiuMa.MainWindow.xaml")) window = (Window)XamlReader.Load(xaml);
            Palette.Apply(window, darkTheme);
            application.MainWindow = window;
            if (renderPath == null)
            {
                Rect work = SystemParameters.WorkArea;
                window.MinHeight = Math.Min(window.MinHeight, Math.Max(300, work.Height - 24));
                window.MinWidth = Math.Min(window.MinWidth, Math.Max(280, work.Width - 24));
                window.Height = Math.Min(window.Height, work.Height - 24);
                window.Width = Math.Min(window.Width, work.Width - 24);
            }
            application.SessionEnding += delegate { if (tracker != null) tracker.Tick(); Save(); };
            using (Stream icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("NiuMa.app.ico")) { BitmapFrame frame = BitmapFrame.Create(icon); frame.Freeze(); window.Icon = frame; }
            WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(5), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(12), UseAeroCaptionButtons = false });
            window.Topmost = settings.Topmost;
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += delegate { OnTick(); };
            Bind();
            BindFeatures();
            window.SourceInitialized += delegate
            {
                int corners = 2; IntPtr handle = new WindowInteropHelper(window).Handle;
                try { Native.DwmSetWindowAttribute(handle, 33, ref corners, sizeof(int)); } catch (DllNotFoundException) { }
                HwndSource.FromHwnd(handle).AddHook(OnMessage);
                if (!demo && renderPath == null)
                {
                    tracker = new Tracker(engine, settings); tracker.InputChanged += OnInputChanged; tracker.Start(window);
                    inputWarning = tracker.HookError;
                    CreateTray();
                    TryPrune(); timer.Start();
                }
            };
            window.Loaded += delegate
            {
                SelectTab(initialView == "apps" || initialView == "trends" ? initialView : "overview");
                if (initialView == "mini" || initialView == "mini-dark" || (!demo && renderPath == null && settings.MiniMode)) ToggleCompact();
                RestorePosition();
                Refresh(true);
                if (renderPath != null) window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(initialView == "data" ? (Action)ShowDataManager : initialView == "settings" ? (Action)ShowSettings : Render));
                else if (demo) timer.Start();
            };
            window.Closing += delegate(object sender, CancelEventArgs e)
            {
                if (exiting) return;
                if (settings.CloseToTray && tray != null) { e.Cancel = true; window.Hide(); Save(); }
                else { e.Cancel = true; Exit(); }
            };
        }
        private T Find<T>(string name) where T : class { return window.FindName(name) as T; }
        private void Text(string name, string value) { TextBlock text = Find<TextBlock>(name); if (text.Text != value) text.Text = value; }
        private void Click(string name, Action action) { Find<Button>(name).Click += delegate { action(); }; }
        private Brush Color(string value) { return Palette.Get(value, darkTheme); }
        private static string Count(long value) { return value.ToString("N0", CultureInfo.InvariantCulture); }
        internal static string Duration(double seconds)
        {
            int minutes = (int)Math.Floor(Math.Max(0, seconds) / 60);
            if (minutes == 0) return seconds > 0 ? "不足 1 分钟" : "0 分钟";
            return minutes >= 60 ? (minutes / 60) + " 时 " + (minutes % 60).ToString("00") + " 分" : minutes + " 分钟";
        }
        private static string ShortTime(double seconds) { int minutes = (int)(Math.Max(0, seconds) / 60); return minutes >= 60 ? minutes / 60 + "h " + (minutes % 60).ToString("00") + "m" : minutes + "m"; }
        private void Bind()
        {
            MouseButtonEventHandler drag = delegate(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left && !(e.OriginalSource is Button)) { try { window.DragMove(); } catch (InvalidOperationException) { } } };
            Find<Grid>("DragArea").MouseLeftButtonDown += drag;
            Find<Grid>("MiniDragArea").MouseLeftButtonDown += drag;
            Click("CloseButton", delegate { window.Close(); }); Click("MiniCloseButton", delegate { window.Close(); });
            Click("MinimizeButton", delegate { window.WindowState = WindowState.Minimized; });
            Click("CompactButton", ToggleCompact); Click("ExpandButton", ToggleCompact);
            Click("PinButton", delegate { settings.Topmost = !settings.Topmost; window.Topmost = settings.Topmost; SaveSettings(); Refresh(false); });
            Click("SettingsButton", ShowSettings); Click("PauseButton", TogglePause); Click("MiniPauseButton", TogglePause); Click("ExportButton", Export);
            Click("OverviewTab", delegate { SelectTab("overview"); }); Click("AppsTab", delegate { SelectTab("apps"); });
            Click("AllAppsButton", delegate { SelectTab("apps"); }); Click("TrendsTab", delegate { SelectTab("trends"); });
            Click("PrevButton", delegate { selectedDate = selectedDate.AddDays(-1); dateFollowing = false; Refresh(true); });
            Click("NextButton", delegate { if (selectedDate < DateTime.Today) { selectedDate = selectedDate.AddDays(1); dateFollowing = selectedDate == DateTime.Today; Refresh(true); } });
            Click("TodayButton", delegate { selectedDate = DateTime.Today; dateFollowing = true; Refresh(true); });
            window.SizeChanged += delegate { Refresh(false); };
            window.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape && compact) ToggleCompact();
                else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control) { TogglePause(); e.Handled = true; }
            };
        }
        private IntPtr OnMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if ((uint)message == showMessage) { Restore(); handled = true; }
            if (message == 0x0232) { SnapWindow(); SchedulePositionSave(); }
            return IntPtr.Zero;
        }
        public void Show()
        {
            if (renderPath != null) { window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -12000; window.Top = -12000; }
            window.Show();
        }
        private void Restore() { ExpandDock(); window.Show(); window.WindowState = WindowState.Normal; window.Activate(); Refresh(true); }
        private void CreateTray()
        {
            tray = new Forms.NotifyIcon { Text = "牛马统计 · 正在记录", Visible = true };
            using (Stream icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("NiuMa.app.ico")) tray.Icon = new System.Drawing.Icon(icon);
            tray.DoubleClick += delegate { Restore(); };
            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开牛马统计", null, delegate { Restore(); });
            trayPause = (Forms.ToolStripMenuItem)menu.Items.Add("暂停统计", null, delegate { TogglePause(); });
            menu.Items.Add("迷你窗", null, delegate { if (!compact) ToggleCompact(); Restore(); });
            menu.Items.Add("导出今天", null, delegate { selectedDate = DateTime.Today; dateFollowing = true; Restore(); Export(); });
            menu.Items.Add("牛马日报", null, delegate { selectedDate = DateTime.Today; dateFollowing = true; Restore(); ShowReport(); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("设置", null, delegate { Restore(); ShowSettings(); });
            menu.Items.Add("数据管理", null, delegate { Restore(); ShowDataManager(); });
            menu.Items.Add("退出", null, delegate { Exit(); }); tray.ContextMenuStrip = menu;
        }
        private void TogglePause()
        {
            if (tracker == null) { if (demo) MessageBox.Show(window, "当前是演示预览，不采集真实数据。", "牛马统计"); return; }
            tracker.TogglePause(); Save(); Refresh(false);
            if (trayPause != null) trayPause.Text = tracker.Paused ? "继续统计" : "暂停统计";
        }
        private void ToggleCompact()
        {
            ExpandDock(); CapturePosition(); placementChanging = true;
            compact = !compact;
            if (compact)
            {
                fullWidth = window.Width; fullHeight = window.Height;
                Find<Grid>("FullRoot").Visibility = Visibility.Collapsed; Find<Grid>("MiniRoot").Visibility = Visibility.Visible;
                window.MinWidth = 280; window.MinHeight = 174; window.Width = settings.MiniWidth; window.Height = settings.MiniHeight;
            }
            else
            {
                Find<Grid>("FullRoot").Visibility = Visibility.Visible; Find<Grid>("MiniRoot").Visibility = Visibility.Collapsed;
                Rect work = SystemParameters.WorkArea;
                window.MinWidth = Math.Min(440, Math.Max(280, work.Width - 24)); window.MinHeight = Math.Min(640, Math.Max(300, work.Height - 24));
                window.Width = Math.Min(fullWidth, work.Width - 24); window.Height = Math.Min(fullHeight, work.Height - 24);
            }
            placementChanging = false;
            if (!demo && renderPath == null) { settings.MiniMode = compact; RestorePosition(); SchedulePositionSave(); }
            Refresh(false);
        }
        private void SelectTab(string tab)
        {
            selectedTab = tab;
            foreach (string item in new[] { "overview", "apps", "trends" })
            {
                string name = item == "overview" ? "Overview" : item == "apps" ? "Apps" : "Trends";
                Find<UIElement>(name + "Panel").Visibility = item == tab ? Visibility.Visible : Visibility.Collapsed;
                Find<Button>(name + "Tab").Background = item == tab ? Color("#E5E9DE") : Brushes.Transparent;
                Find<Button>(name + "Tab").FontWeight = item == tab ? FontWeights.SemiBold : FontWeights.Normal;
            }
            Refresh(true);
        }
        private void OnTick()
        {
            if (tracker != null) { tracker.Tick(); inputWarning = tracker.HookError ?? ""; }
            if (dateFollowing && selectedDate != DateTime.Today) { selectedDate = DateTime.Today; rowsDate = ""; }
            if ((DateTime.Now - lastSave).TotalSeconds >= 10) Save();
            if (lastPrune != DateTime.Today) { TryPrune(); lastPrune = DateTime.Today; }
            UpdateTray();
            if (settings.Theme == "System" && visualTick % 30 == 0 && darkTheme != Palette.IsDark(settings)) ApplyTheme();
            if (++visualTick % 2 == 0 && window.IsVisible && window.WindowState != WindowState.Minimized) Refresh(false);
        }
        private void OnInputChanged(object sender, EventArgs e)
        {
            if (exiting || !window.IsVisible || window.WindowState == WindowState.Minimized) return;
            if (dateFollowing && selectedDate != DateTime.Today) { selectedDate = DateTime.Today; rowsDate = ""; Refresh(true); return; }
            if (selectedDate == DateTime.Today) RefreshInputCounters(engine.Day(selectedDate));
        }
        private void RefreshInputCounters(DayStats day)
        {
            Text("MiniMetricsText", "键盘 " + Count(day.Keys) + " · 鼠标 " + Count(day.Clicks));
            if (compact) return;
            Text("KeysText", Count(day.Keys)); Text("ClicksText", Count(day.Clicks));
            Text("KeyRateText", day.Keys == 0 ? "等待第一下敲击" : "平均 " + (day.Keys / Math.Max(1, day.ActiveSeconds / 60)).ToString("0.0") + " 次 / 活跃分钟");
            string distance = day.MousePixels >= 1000 ? (day.MousePixels / 1000).ToString("0.0") + "k px" : day.MousePixels.ToString("0") + " px";
            Text("MouseSubText", "滚动 " + day.Scroll.ToString("N0") + " 格 · 移动 " + distance);
            Text("MouseDetailText", "左键 " + Count(day.LeftClicks) + "  /  右键 " + Count(day.RightClicks) + "  /  中键及侧键 " + Count(day.MiddleClicks));
            Text("DistanceText", "滚动 " + day.Scroll.ToString("N0") + " 格 · 鼠标移动 " + day.MousePixels.ToString("N0") + " px");
            if (selectedTab == "apps" && rowsDate == day.Date)
                foreach (AppRowView view in allRows.Values) SetText(view.Input, "键 " + Count(view.App.Keys) + "  /  鼠 " + Count(view.App.Clicks));
        }
        private void TryPrune() { try { store.Prune(settings.RetentionDays, DateTime.Today); } catch (IOException) { store.Warning = "旧记录清理失败"; } catch (UnauthorizedAccessException) { store.Warning = "旧记录无法清理"; } }
        private void Save()
        {
            if (demo || renderPath != null) return;
            try { engine.Save(); lastSave = DateTime.Now; }
            catch (IOException) { store.Warning = "数据保存失败，请检查磁盘空间"; }
            catch (UnauthorizedAccessException) { store.Warning = "数据目录无法写入"; }
        }
        private void SaveSettings() { if (!demo) store.SaveSettings(settings); }
        public void Exit()
        {
            if (exiting) return; exiting = true; timer.Stop();
            if (positionTimer != null) positionTimer.Stop(); if (dockTimer != null) dockTimer.Stop();
            CapturePosition(); if (!demo && renderPath == null) SaveSettings();
            if (tracker != null) { tracker.InputChanged -= OnInputChanged; tracker.Dispose(); tracker = null; }
            Save(); if (tray != null) { tray.Visible = false; tray.Icon.Dispose(); tray.Dispose(); tray = null; }
            window.Close(); application.Shutdown();
        }
        private void Refresh(bool force)
        {
            DayStats day = engine.Day(selectedDate);
            Text("DateText", selectedDate.ToString("M 月 d 日", chinese));
            Text("WeekText", selectedDate.ToString("dddd", chinese) + (selectedDate == DateTime.Today ? " · 今天" : " · 历史"));
            Text("HeroTag", selectedDate == DateTime.Today ? "TODAY" : "HISTORY");
            Find<Button>("NextButton").IsEnabled = selectedDate < DateTime.Today;
            bool paused = tracker != null && tracker.Paused;
            string status = demo ? "演示数据" : paused ? "已暂停" : tracker != null && !tracker.ScreenAvailable ? "屏幕已离开" : tracker != null && tracker.Idle ? "空闲中" : "正在记录";
            if (inputWarning.Length > 0) status = "监听异常";
            Text("StatusText", status); Text("MiniStatusText", "● " + status);
            Find<Ellipse>("StatusDot").Fill = Color(paused || inputWarning.Length > 0 ? "#C69958" : "#6F9565");
            Find<Button>("PauseButton").Content = paused ? "▶  继续" : "Ⅱ  暂停";
            Find<Button>("MiniPauseButton").Content = paused ? "继续" : "暂停";
            Find<Button>("PinButton").Foreground = settings.Topmost ? Color("#BE8E54") : Color("#546157");
            Text("PrivacyText", inputWarning.Length > 0 ? inputWarning : store.Warning.Length > 0 ? store.Warning : "仅计次数 · 数据留在本机");
            int minutes = (int)(Math.Max(0, day.ActiveSeconds) / 60);
            Text("HoursText", (minutes / 60).ToString()); Text("MinutesText", (minutes % 60).ToString("00"));
            Text("MiniTimeText", (minutes / 60) + "h " + (minutes % 60).ToString("00") + "m");
            RefreshInputCounters(day);
            Text("DockTimeText", (minutes / 60) + "h");
            if (compact) { UpdateTray(); return; }
            double percent = day.ScreenSeconds > 0 ? Math.Min(1, day.ActiveSeconds / day.ScreenSeconds) : 0;
            Text("ActivePercent", (percent * 100).ToString("0") + "% 活跃");
            double available = Math.Max(0, window.ActualWidth - 86);
            Find<Border>("HeroProgress").Width = available * percent;
            Text("ScreenText", "开屏 " + Duration(day.ScreenSeconds)); Text("IdleText", "非活跃 " + Duration(day.ScreenSeconds - day.ActiveSeconds));
            Text("AppsSummaryText", day.Apps.Count + " 款软件 · 累计活跃 " + Duration(day.ActiveSeconds));
            if (selectedTab == "overview") { DrawApps(day); DrawHourly(day); }
            else if (selectedTab == "apps") { DrawApps(day); DrawCategories(day); }
            else if (force || (DateTime.Now - lastTrendRefresh).TotalSeconds >= 15) { DrawTrends(day); lastTrendRefresh = DateTime.Now; }
        }

        private ImageSource AppIcon(AppStats app)
        {
            ImageSource result;
            if (icons.TryGetValue(app.Id, out result)) return result;
            result = null;
            try
            {
                if (!String.IsNullOrEmpty(app.FilePath) && File.Exists(app.FilePath))
                {
                    using (System.Drawing.Icon icon = System.Drawing.Icon.ExtractAssociatedIcon(app.FilePath))
                    {
                        if (icon != null)
                        {
                            using (System.Drawing.Bitmap bitmap = icon.ToBitmap())
                            using (MemoryStream stream = new MemoryStream())
                            {
                                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png); stream.Position = 0;
                                BitmapImage image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); result = image;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { if (!(ex is IOException || ex is ArgumentException || ex is System.ComponentModel.Win32Exception || ex is UnauthorizedAccessException)) throw; }
            while (icons.Count >= 64 && iconOrder.Count > 0) icons.Remove(iconOrder.Dequeue());
            icons[app.Id] = result; iconOrder.Enqueue(app.Id); return result;
        }
        private UIElement IconElement(AppStats app)
        {
            ImageSource icon = AppIcon(app);
            if (icon != null) return new Image { Source = icon, Width = 25, Height = 25 };
            string name = app.Name ?? "?";
            if (name.Contains("Visual Studio"))
                return new Viewbox { Width = 23, Height = 23, Child = new System.Windows.Shapes.Path { Fill = Color("#248FCD"), Data = Geometry.Parse("M 23,0 L 9,12 3,7 0,9 6,15 0,21 3,23 9,18 23,30 30,27 30,3 Z M 23,7 L 23,23 14,15 Z") } };
            if (name.Contains("Chrome"))
            {
                Grid mark = new Grid { Width = 23, Height = 23 };
                mark.Children.Add(new Ellipse { Fill = Color("#D99C53") });
                mark.Children.Add(new Ellipse { Fill = Color("#F6F5EF"), Width = 13, Height = 13 });
                mark.Children.Add(new Ellipse { Fill = Color("#7197AF"), Width = 10, Height = 10 }); return mark;
            }
            if (name == "Figma")
            {
                Canvas mark = new Canvas { Width = 18, Height = 27 };
                string[] fills = { "#EB785C", "#EFAD92", "#A78ACA", "#6FAED5", "#81AD85" };
                for (int i = 0; i < 5; i++) { Ellipse circle = new Ellipse { Width = 9, Height = 9, Fill = Color(fills[i]) }; Canvas.SetLeft(circle, i % 2 * 9); Canvas.SetTop(circle, i / 2 * 9); mark.Children.Add(circle); } return mark;
            }
            if (name.Contains("微信")) return new TextBlock { Text = "●●", FontSize = 16, Foreground = Color("#71A470"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            return new TextBlock { Text = name.Substring(0, Math.Min(2, name.Length)).ToUpperInvariant(), FontFamily = new FontFamily("Bahnschrift"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Color("#7F9275"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        private sealed class AppRowView
        {
            public AppStats App;
            public Grid Row;
            public TextBlock Time, Share, Input;
            public Button Category;
            public ColumnDefinition Fill, Empty;
        }
        private readonly Dictionary<string, AppRowView> topRows = new Dictionary<string, AppRowView>();
        private readonly Dictionary<string, AppRowView> allRows = new Dictionary<string, AppRowView>();
        private string rowsDate = "", categoryFingerprint = "";
        private List<AppStats> rankedApps;
        private DateTime rankedAt = DateTime.MinValue;
        private string categoryFilter = "全部分类";
        private int appPage;
        private AppRowView AppRow(AppStats app, double total, bool detail)
        {
            AppRowView view = new AppRowView { App = app };
            Grid row = new Grid { Margin = new Thickness(0, 0, 0, detail ? 14 : 10), MinHeight = detail ? 54 : 37 };
            view.Row = row; row.Tag = app.Id;
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Border mark = new Border { Width = 30, Height = 32, CornerRadius = new CornerRadius(8), Background = Color("#ECEEE4"), Child = IconElement(app), VerticalAlignment = VerticalAlignment.Top };
            row.Children.Add(mark);
            StackPanel lines = new StackPanel { Margin = new Thickness(9, 0, 1, 0) }; Grid.SetColumn(lines, 1); row.Children.Add(lines);
            Grid header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock label = new TextBlock { Text = app.Name, FontSize = 11, Foreground = Color("#52624D"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
            header.Children.Add(label);
            TextBlock time = new TextBlock { Text = Duration(app.ActiveSeconds), FontSize = 10, Foreground = Color("#7E8875"), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(time, 1); header.Children.Add(time); lines.Children.Add(header);
            view.Time = time;
            Grid barline = new Grid { Margin = new Thickness(0, 5, 0, 0), Height = 14 }; barline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); barline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            Grid bar = new Grid { Height = 4 };
            double fraction = total > 0 ? Math.Min(1, app.ActiveSeconds / total) : 0;
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star) }); bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - fraction, GridUnitType.Star) });
            view.Fill = bar.ColumnDefinitions[0]; view.Empty = bar.ColumnDefinitions[1];
            Border track = new Border { CornerRadius = new CornerRadius(2), Background = Color("#E7EADF") }; Grid.SetColumnSpan(track, 2); bar.Children.Add(track);
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = Color("#91A781") }); barline.Children.Add(bar);
            TextBlock share = new TextBlock { Text = (fraction * 100).ToString("0") + "%", FontFamily = new FontFamily("Bahnschrift"), FontSize = 9, Foreground = Color("#9BA38E"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right }; Grid.SetColumn(share, 1); barline.Children.Add(share); lines.Children.Add(barline);
            view.Share = share;
            if (detail)
            {
                Grid bottom = new Grid { Margin = new Thickness(0, 5, 0, 0) };
                view.Input = new TextBlock { FontSize = 9, Foreground = Color("#9AA18F"), VerticalAlignment = VerticalAlignment.Center };
                view.Category = new Button { FontSize = 9, Padding = new Thickness(7, 3, 7, 3), Background = Color("#ECEEE3"), Foreground = Color("#809171"), HorizontalAlignment = HorizontalAlignment.Right };
                view.Category.Click += delegate { ShowAppMenu(app, view.Category); };
                bottom.Children.Add(view.Input); bottom.Children.Add(view.Category); lines.Children.Add(bottom);
            }
            row.ToolTip = app.Name + "\n前台活跃 " + Duration(app.ActiveSeconds) + "\n" + app.FilePath;
            return view;
        }
        private void DrawApps(DayStats day)
        {
            if (rowsDate != day.Date) { rowsDate = day.Date; topRows.Clear(); allRows.Clear(); rankedApps = null; appPage = 0; Find<StackPanel>("TopAppsPanel").Children.Clear(); Find<StackPanel>("AppListPanel").Children.Clear(); }
            if (rankedApps == null || rankedApps.Count != day.Apps.Count || (DateTime.Now - rankedAt).TotalSeconds >= 5) { rankedApps = day.Apps.OrderByDescending(a => a.ActiveSeconds).ThenByDescending(a => a.Keys + a.Clicks).ToList(); rankedAt = DateTime.Now; }
            List<AppStats> ordered = rankedApps;
            Find<Border>("EmptyApps").Visibility = ordered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (selectedTab == "overview") UpdateRows(Find<StackPanel>("TopAppsPanel"), topRows, ordered.Take(3).ToList(), day.ActiveSeconds, false);
            else
            {
                List<AppStats> filtered = ordered.Where(a => categoryFilter == "全部分类" || settings.Category(a) == categoryFilter).ToList();
                int pages = Math.Max(1, (filtered.Count + 49) / 50); appPage = Math.Min(appPage, pages - 1);
                UpdateRows(Find<StackPanel>("AppListPanel"), allRows, filtered.Skip(appPage * 50).Take(50).ToList(), day.ActiveSeconds, true);
                Find<StackPanel>("AppPager").Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
                Text("AppPageText", (appPage + 1) + " / " + pages); Find<Button>("AppPagePrev").IsEnabled = appPage > 0; Find<Button>("AppPageNext").IsEnabled = appPage + 1 < pages;
            }
        }
        private static void SetText(TextBlock text, string value) { if (text.Text != value) text.Text = value; }
        private void UpdateRows(StackPanel panel, Dictionary<string, AppRowView> views, List<AppStats> apps, double total, bool detail)
        {
            if (apps.Count == 0)
            {
                views.Clear();
                if (detail) { if (panel.Children.Count != 1 || !Equals(((FrameworkElement)panel.Children[0]).Tag, "empty")) { panel.Children.Clear(); panel.Children.Add(new TextBlock { Tag = "empty", Text = "这个分类还没有使用记录。", Foreground = Color("#929C87"), FontSize = 12, Margin = new Thickness(0, 20, 0, 0) }); } }
                else if (panel.Children.Count > 0) panel.Children.Clear();
                return;
            }
            bool changed = panel.Children.Count != apps.Count;
            if (!changed) for (int i = 0; i < apps.Count; i++) if (!String.Equals(((FrameworkElement)panel.Children[i]).Tag as string, apps[i].Id)) { changed = true; break; }
            if (changed) panel.Children.Clear();
            HashSet<string> visible = new HashSet<string>(apps.Select(a => a.Id));
            foreach (string id in views.Keys.Where(id => !visible.Contains(id)).ToArray()) views.Remove(id);
            foreach (AppStats app in apps)
            {
                AppRowView view;
                if (!views.TryGetValue(app.Id, out view)) { view = AppRow(app, total, detail); views[app.Id] = view; }
                view.App = app;
                if (changed) panel.Children.Add(view.Row);
                SetText(view.Time, Duration(app.ActiveSeconds)); double fraction = total > 0 ? Math.Min(1, app.ActiveSeconds / total) : 0;
                SetText(view.Share, (fraction * 100).ToString("0") + "%"); view.Fill.Width = new GridLength(fraction, GridUnitType.Star); view.Empty.Width = new GridLength(1 - fraction, GridUnitType.Star);
                if (detail) { SetText(view.Input, "键 " + Count(app.Keys) + "  /  鼠 " + Count(app.Clicks)); string tag = settings.Excluded(app.Id) ? "已排除 ▾" : settings.Category(app) + " ▾"; if (!Equals(view.Category.Content, tag)) view.Category.Content = tag; }
            }
        }
        private Border[] hourScreens, hourActive;
        private Grid[] hourSlots;
        private void DrawHourly(DayStats day)
        {
            Grid chart = Find<Grid>("HourlyChart");
            if (hourScreens == null)
            {
                chart.Children.Clear(); chart.ColumnDefinitions.Clear(); hourScreens = new Border[24]; hourActive = new Border[24]; hourSlots = new Grid[24];
                for (int i = 0; i < 24; i++)
                {
                    chart.ColumnDefinitions.Add(new ColumnDefinition()); Grid slot = new Grid { Margin = new Thickness(1.5, 0, 1.5, 0) };
                    hourScreens[i] = new Border { Background = Color("#DCE1D1"), CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Bottom };
                    hourActive[i] = new Border { CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Bottom };
                    slot.Children.Add(hourScreens[i]); slot.Children.Add(hourActive[i]); Grid.SetColumn(slot, i); chart.Children.Add(slot); hourSlots[i] = slot;
                }
            }
            for (int hour = 0; hour < 24; hour++)
            {
                hourScreens[hour].Height = Math.Max(2, Math.Min(42, day.Hours[hour].ScreenSeconds / 3600 * 42));
                hourActive[hour].Height = Math.Max(2, Math.Min(42, day.Hours[hour].ActiveSeconds / 3600 * 42));
                hourActive[hour].Visibility = day.Hours[hour].ActiveSeconds > 0 ? Visibility.Visible : Visibility.Collapsed;
                hourActive[hour].Background = Color(hour == DateTime.Now.Hour && selectedDate == DateTime.Today ? "#D3A36F" : "#7F9971");
                hourSlots[hour].ToolTip = hour.ToString("00") + ":00\n活跃 " + Duration(day.Hours[hour].ActiveSeconds) + "\n开屏 " + Duration(day.Hours[hour].ScreenSeconds) + "\n键盘 " + Count(day.Hours[hour].Keys) + " · 鼠标 " + Count(day.Hours[hour].Clicks);
            }
        }
        private void DrawTrends(DayStats selected)
        {
            DateTime start = selectedDate.AddDays(-6); List<DayStats> days = Enumerable.Range(0, 7).Select(i => engine.Day(start.AddDays(i))).ToList();
            Text("TrendRangeText", start.ToString("M 月 d 日") + " — " + selectedDate.ToString("M 月 d 日") + " · 点击日期柱查看当天");
            Text("WeekTotalText", "7 天累计 " + Duration(days.Sum(d => d.ActiveSeconds)) + " · 日均 " + Duration(days.Sum(d => d.ActiveSeconds) / 7));
            Grid chart = Find<Grid>("WeekChart"); chart.Children.Clear(); chart.ColumnDefinitions.Clear();
            double max = Math.Max(3600, days.Max(d => d.ActiveSeconds));
            for (int i = 0; i < 7; i++)
            {
                chart.ColumnDefinitions.Add(new ColumnDefinition()); DateTime date = start.AddDays(i); DayStats day = days[i];
                Grid column = new Grid { Margin = new Thickness(5, 0, 5, 0), Cursor = Cursors.Hand, ToolTip = date.ToString("M 月 d 日") + "\n活跃 " + Duration(day.ActiveSeconds) + "\n键盘 " + Count(day.Keys) + " 次" };
                column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
                StackPanel bar = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
                bar.Children.Add(new TextBlock { Text = day.ActiveSeconds == 0 ? "—" : ShortTime(day.ActiveSeconds).Replace(" ", ""), FontFamily = new FontFamily("Bahnschrift"), FontSize = 9, Foreground = Color("#7E8B70"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 5) });
                bar.Children.Add(new Border { Background = Color(i == 6 ? "#2E5140" : "#ADBC94"), CornerRadius = new CornerRadius(5, 5, 2, 2), Height = Math.Max(3, day.ActiveSeconds / max * 102), MaxWidth = 29 }); column.Children.Add(bar);
                TextBlock label = new TextBlock { Text = date.ToString("MM.dd"), FontFamily = new FontFamily("Bahnschrift"), FontSize = 9, Foreground = Color("#91A080"), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center }; Grid.SetRow(label, 1); column.Children.Add(label);
                column.MouseLeftButtonUp += delegate { selectedDate = date; dateFollowing = date == DateTime.Today; Refresh(true); };
                Grid.SetColumn(column, i); chart.Children.Add(column);
            }
            StackPanel hours = Find<StackPanel>("HourDetailPanel"); hours.Children.Clear();
            for (int i = 0; i < 24; i++)
            {
                Counters hour = selected.Hours[i]; if (hour.ScreenSeconds == 0 && hour.Keys == 0 && hour.Clicks == 0) continue;
                Grid row = new Grid { Margin = new Thickness(1, 0, 1, 15) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(57) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = i.ToString("00") + ":00", FontFamily = new FontFamily("Bahnschrift"), FontSize = 12, Foreground = Color("#8B9780"), VerticalAlignment = VerticalAlignment.Center });
                TextBlock time = new TextBlock { Text = Duration(hour.ActiveSeconds), FontSize = 11, Foreground = Color("#52674A"), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(time, 1); row.Children.Add(time);
                TextBlock counts = new TextBlock { Text = "键 " + Count(hour.Keys) + "  /  鼠 " + Count(hour.Clicks), FontSize = 9, Foreground = Color("#9BA58F"), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(counts, 2); row.Children.Add(counts); hours.Children.Add(row);
            }
            if (hours.Children.Count == 0) hours.Children.Add(new TextBlock { Text = "这一天还没有时段记录。", FontSize = 12, Foreground = Color("#98A18D") });
        }

        private void Export()
        {
            if (tracker != null) tracker.Tick();
            SaveFileDialog dialog = new SaveFileDialog { Title = "导出当前日期的统计", Filter = "CSV 表格 (*.csv)|*.csv", FileName = "牛马统计-" + Store.Key(selectedDate) + ".csv", DefaultExt = ".csv", AddExtension = true };
            if (dialog.ShowDialog(window) != true) return;
            try { CsvExport.Save(dialog.FileName, engine.Day(selectedDate)); MessageBox.Show(window, "已导出总计、软件和 24 小时时段数据。\n" + dialog.FileName, "导出完成", MessageBoxButton.OK, MessageBoxImage.Information); }
            catch (IOException ex) { MessageBox.Show(window, "无法导出，请检查文件是否被占用。\n" + ex.Message, "牛马统计"); }
            catch (UnauthorizedAccessException ex) { MessageBox.Show(window, "没有权限写入这个位置。\n" + ex.Message, "牛马统计"); }
        }
        private void Render()
        {
            try
            {
                if (initialView == "report") { ReportBuilder.Save(ReportBuilder.Build(engine.Day(selectedDate), settings), renderPath); Exit(); return; }
                window.UpdateLayout();
                double scale = 1.5;
                RenderTargetBitmap bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(window); PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(renderPath)));
                using (FileStream output = File.Create(renderPath)) encoder.Save(output);
            }
            catch (Exception ex) { File.WriteAllText(renderPath + ".error.txt", ex.ToString()); Environment.ExitCode = 1; }
            Exit();
        }

        private bool IsStartupEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run")) return key != null && key.GetValue("NiuMaStats") != null;
        }
        private void SetStartup(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"))
            {
                if (enabled) key.SetValue("NiuMaStats", "\"" + Assembly.GetExecutingAssembly().Location + "\"", RegistryValueKind.String);
                else key.DeleteValue("NiuMaStats", false);
            }
        }
        private TextBlock SettingLabel(string title, string subtitle)
        { return new TextBlock { Text = title + "\n" + subtitle, FontSize = 11, LineHeight = 21, Foreground = Color("#65765B"), Margin = new Thickness(0, 12, 0, 5) }; }
        private void ShowSettings()
        {
            StackPanel body; Window dialog = Dialog("设置", 420, 725, out body);
            body.Children.Add(new TextBlock { Text = "让记录，刚刚好。", FontSize = 21, FontWeight = FontWeights.SemiBold });
            body.Children.Add(SettingLabel("多久没有操作，算作离开？", "离开期间只累计开屏，不累计软件活跃时长。"));
            int[] idleValues = { 30, 60, 120, 300 }; ComboBox idle = new ComboBox { Height = 30, FontSize = 11, ItemsSource = new[] { "30 秒", "1 分钟（推荐）", "2 分钟", "5 分钟" }, SelectedIndex = Math.Max(0, Array.IndexOf(idleValues, settings.IdleSeconds)) }; body.Children.Add(idle);
            body.Children.Add(SettingLabel("保留多久的日统计？", "超过保留天数的旧记录会自动清理。"));
            int[] retentionValues = { 7, 30, 90, 365 }; ComboBox retention = new ComboBox { Height = 30, FontSize = 11, ItemsSource = new[] { "7 天", "30 天", "90 天（推荐）", "365 天" }, SelectedIndex = Math.Max(0, Array.IndexOf(retentionValues, settings.RetentionDays)) }; body.Children.Add(retention);
            CheckBox startup = new CheckBox { Content = "登录 Windows 时自动启动", IsChecked = IsStartupEnabled(), FontSize = 11, Margin = new Thickness(0, 20, 0, 10) };
            CheckBox toTray = new CheckBox { Content = "关闭窗口时收起到托盘", IsChecked = settings.CloseToTray, FontSize = 11, Margin = new Thickness(0, 0, 0, 10) };
            CheckBox topmost = new CheckBox { Content = "窗口始终置顶", IsChecked = settings.Topmost, FontSize = 11, Margin = new Thickness(0, 0, 0, 13) }; body.Children.Add(startup); body.Children.Add(toTray); body.Children.Add(topmost);
            CheckBox snap = new CheckBox { Content = "迷你窗靠近边缘时自动吸附", IsChecked = settings.SnapMini, FontSize = 11, Margin = new Thickness(0, 0, 0, 10) };
            CheckBox autoHide = new CheckBox { Content = "贴边后自动收起，移入时展开", IsChecked = settings.AutoHideMini, FontSize = 11, Margin = new Thickness(0, 0, 0, 10) }; body.Children.Add(snap); body.Children.Add(autoHide);
            body.Children.Add(new TextBlock { Text = "界面外观", FontSize = 11, Foreground = Color("#65765B"), Margin = new Thickness(0, 2, 0, 6) });
            ComboBox theme = new ComboBox { ItemsSource = new[] { "奶油白", "深色", "跟随 Windows" }, SelectedIndex = settings.Theme == "Dark" ? 1 : settings.Theme == "System" ? 2 : 0, Height = 30, FontSize = 11, Margin = new Thickness(0, 0, 0, 14) }; body.Children.Add(theme);
            Border note = new Border { Background = Color("#E9EDE0"), CornerRadius = new CornerRadius(10), Padding = new Thickness(13) };
            note.Child = new TextBlock { Text = "只保存统计数字和软件名称 / 路径。\n不保存输入内容、窗口标题或截图；不联网。\n长按按键不重复计数，模拟输入不累计。\n开屏指解锁且显示器亮起的运行时间。", FontSize = 10, LineHeight = 19, Foreground = Color("#809171"), TextWrapping = TextWrapping.Wrap }; body.Children.Add(note);
            Button folder = new Button { Content = "打开本地数据文件夹 ↗", FontSize = 11, Padding = new Thickness(9), Margin = new Thickness(0, 13, 0, 0), Background = Color("#EEEFE6"), BorderThickness = new Thickness(0), Cursor = Cursors.Hand };
            folder.Click += delegate { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + store.Root + "\"") { UseShellExecute = true }); }; body.Children.Add(folder);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 17, 0, 0) };
            Button cancel = new Button { Content = "取消", Padding = new Thickness(17, 8, 17, 8), Background = Color("#ECEEE3"), BorderThickness = new Thickness(0), FontSize = 11, Margin = new Thickness(0, 0, 9, 0) };
            Button save = new Button { Content = "保存设置", Padding = new Thickness(17, 8, 17, 8), Background = Color("#2F513E"), Foreground = Brushes.White, BorderThickness = new Thickness(0), FontSize = 11 };
            cancel.Click += delegate { dialog.Close(); };
            save.Click += delegate
            {
                if (demo) { dialog.Close(); return; }
                try
                {
                    if (tracker != null) tracker.SettingsChanged();
                    if ((startup.IsChecked == true) != IsStartupEnabled()) SetStartup(startup.IsChecked == true);
                    settings.IdleSeconds = idleValues[idle.SelectedIndex]; settings.RetentionDays = retentionValues[retention.SelectedIndex];
                    settings.CloseToTray = toTray.IsChecked == true; settings.Topmost = topmost.IsChecked == true; window.Topmost = settings.Topmost;
                    settings.SnapMini = snap.IsChecked == true; settings.AutoHideMini = autoHide.IsChecked == true; settings.Theme = theme.SelectedIndex == 1 ? "Dark" : theme.SelectedIndex == 2 ? "System" : "Light";
                    SaveSettings(); Save(); TryPrune(); ApplyTheme(); Refresh(true); dialog.Close();
                }
                catch (Exception ex) { MessageBox.Show(dialog, "设置没有保存成功。\n" + ex.Message, "牛马统计"); }
            };
            buttons.Children.Add(cancel); buttons.Children.Add(save); body.Children.Add(buttons);
            if (initialView == "settings" && renderPath != null) RenderDialog(dialog); else dialog.ShowDialog();
        }
    }
}

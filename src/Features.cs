using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace NiuMa
{
    public static class Placement
    {
        public static Rect Fit(Rect bounds, Rect work)
        {
            double width = Math.Min(bounds.Width, work.Width), height = Math.Min(bounds.Height, work.Height);
            return new Rect(Math.Max(work.Left, Math.Min(work.Right - width, bounds.Left)), Math.Max(work.Top, Math.Min(work.Bottom - height, bounds.Top)), width, height);
        }
        public static Rect Snap(Rect bounds, Rect work, double threshold)
        {
            double left = bounds.Left, top = bounds.Top;
            if (Math.Abs(left - work.Left) <= threshold) left = work.Left;
            else if (Math.Abs(bounds.Right - work.Right) <= threshold) left = work.Right - bounds.Width;
            if (Math.Abs(top - work.Top) <= threshold) top = work.Top;
            else if (Math.Abs(bounds.Bottom - work.Bottom) <= threshold) top = work.Bottom - bounds.Height;
            return Fit(new Rect(left, top, bounds.Width, bounds.Height), work);
        }
    }

    internal sealed partial class Shell
    {
        private DispatcherTimer positionTimer, dockTimer;
        private bool placementChanging, docked, updatingFilter;
        private Rect beforeDock;
        private string trayFingerprint = "";
        private void BindFeatures()
        {
            Click("ReportButton", ShowReport); Click("DataButton", ShowDataManager); Click("CategoriesButton", ShowCategories);
            Click("AppPagePrev", delegate { if (appPage > 0) appPage--; Refresh(true); });
            Click("AppPageNext", delegate { appPage++; Refresh(true); });
            UpdateFilter();
            Find<ComboBox>("CategoryFilter").SelectionChanged += delegate
            {
                if (updatingFilter) return; categoryFilter = Find<ComboBox>("CategoryFilter").SelectedItem as string ?? "全部分类"; appPage = 0; Refresh(true);
            };
            positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            positionTimer.Tick += delegate { positionTimer.Stop(); CapturePosition(); if (!demo && renderPath == null) SaveSettings(); };
            dockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
            dockTimer.Tick += delegate { dockTimer.Stop(); if (!window.IsMouseOver) CollapseDock(); };
            window.LocationChanged += delegate { SchedulePositionSave(); };
            window.SizeChanged += delegate { SchedulePositionSave(); };
            window.MouseEnter += delegate { dockTimer.Stop(); ExpandDock(); };
            window.MouseLeave += delegate { if (compact && settings.AutoHideMini && !docked && renderPath == null) { dockTimer.Stop(); dockTimer.Start(); } };
        }
        private void UpdateFilter()
        {
            updatingFilter = true; ComboBox filter = Find<ComboBox>("CategoryFilter");
            filter.ItemsSource = new[] { "全部分类" }.Concat(settings.Categories).ToArray();
            if (categoryFilter != "全部分类" && !settings.Categories.Contains(categoryFilter)) categoryFilter = "全部分类";
            filter.SelectedItem = categoryFilter; updatingFilter = false;
        }
        private void UpdateTray()
        {
            if (tray == null) return;
            string status = tracker.Paused ? "已暂停" : !tracker.ScreenAvailable ? "屏幕已离开" : tracker.Idle ? "空闲中" : "正在记录";
            string value = "牛马统计 · " + status + " · " + ShortTime(engine.Day(DateTime.Today).ActiveSeconds);
            if (trayFingerprint != value) { trayFingerprint = value; tray.Text = value; }
        }
        private void ApplyTheme()
        {
            darkTheme = Palette.IsDark(settings); Palette.Apply(window, darkTheme);
            topRows.Clear(); allRows.Clear(); Find<StackPanel>("TopAppsPanel").Children.Clear(); Find<StackPanel>("AppListPanel").Children.Clear();
            hourScreens = null; categoryFingerprint = ""; Refresh(true);
        }
        private Rect WorkArea()
        {
            HwndSource source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            System.Drawing.Rectangle area = Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle).WorkingArea;
            Matrix transform = source.CompositionTarget.TransformFromDevice;
            Point start = transform.Transform(new Point(area.Left, area.Top)), end = transform.Transform(new Point(area.Right, area.Bottom));
            return new Rect(start, end);
        }
        private void SchedulePositionSave()
        {
            if (placementChanging || docked || demo || renderPath != null || positionTimer == null || window.WindowState != WindowState.Normal) return;
            positionTimer.Stop(); positionTimer.Start();
        }
        private void CapturePosition()
        {
            if (demo || renderPath != null || window.WindowState != WindowState.Normal || Double.IsNaN(window.Left) || Double.IsNaN(window.Top)) return;
            Rect bounds = docked ? beforeDock : new Rect(window.Left, window.Top, window.Width, window.Height);
            if (compact) { settings.MiniLeft = bounds.Left; settings.MiniTop = bounds.Top; settings.MiniWidth = bounds.Width; settings.MiniHeight = bounds.Height; }
            else { settings.FullLeft = bounds.Left; settings.FullTop = bounds.Top; settings.FullWidth = bounds.Width; settings.FullHeight = bounds.Height; }
            settings.MiniMode = compact;
        }
        private void RestorePosition()
        {
            if (demo || renderPath != null) return;
            double? left = compact ? settings.MiniLeft : settings.FullLeft, top = compact ? settings.MiniTop : settings.FullTop;
            placementChanging = true;
            if (left.HasValue && top.HasValue) { window.Left = left.Value; window.Top = top.Value; }
            window.Width = compact ? settings.MiniWidth : settings.FullWidth; window.Height = compact ? settings.MiniHeight : settings.FullHeight;
            Rect fitted = Placement.Fit(new Rect(window.Left, window.Top, window.Width, window.Height), WorkArea());
            window.Left = fitted.Left; window.Top = fitted.Top; window.Width = fitted.Width; window.Height = fitted.Height;
            placementChanging = false;
        }
        private void SnapWindow()
        {
            if (!compact || !settings.SnapMini || docked) return;
            Rect snapped = Placement.Snap(new Rect(window.Left, window.Top, window.Width, window.Height), WorkArea(), 18);
            window.Left = snapped.Left; window.Top = snapped.Top;
        }
        private void CollapseDock(bool testPreview = false)
        {
            if (!compact || docked || !settings.AutoHideMini || (demo && !testPreview) || !window.IsVisible) return;
            Rect work = WorkArea(); bool atLeft = Math.Abs(window.Left - work.Left) < 2, atRight = Math.Abs(window.Left + window.Width - work.Right) < 2;
            if (!atLeft && !atRight) return;
            CapturePosition(); beforeDock = new Rect(window.Left, window.Top, window.Width, window.Height); docked = true; placementChanging = true;
            Find<Grid>("MiniRoot").Visibility = Visibility.Collapsed; Find<Border>("DockRoot").Visibility = Visibility.Visible;
            window.ResizeMode = ResizeMode.NoResize; window.MinWidth = 32; window.MinHeight = 100; window.Width = 32; window.Height = 112;
            window.Left = atLeft ? work.Left : work.Right - 32; placementChanging = false;
        }
        private void ExpandDock()
        {
            if (!docked) return; placementChanging = true; docked = false;
            Find<Grid>("MiniRoot").Visibility = Visibility.Visible; Find<Border>("DockRoot").Visibility = Visibility.Collapsed;
            window.Width = beforeDock.Width; window.Height = beforeDock.Height; window.Left = beforeDock.Left; window.Top = beforeDock.Top;
            window.MinWidth = 280; window.MinHeight = 174; window.ResizeMode = ResizeMode.CanResize; placementChanging = false;
        }
        private void DrawCategories(DayStats day)
        {
            var groups = day.Apps.GroupBy(settings.Category).Select(g => new { Name = g.Key, Seconds = g.Sum(a => a.ActiveSeconds) }).OrderByDescending(g => g.Seconds).Where(g => g.Seconds > 0).ToArray();
            string key = day.Date + ":" + settings.Revision + ":" + String.Join(",", groups.Select(g => g.Name + ((int)(g.Seconds / 60))));
            if (key == categoryFingerprint) return; categoryFingerprint = key;
            StackPanel panel = Find<StackPanel>("CategorySummaryPanel"); panel.Children.Clear();
            Grid rail = new Grid { Height = 5 }; string[] colors = { "#75916C", "#D3A36F", "#91A781", "#7197AF", "#A78ACA" };
            for (int i = 0; i < groups.Length; i++)
            {
                rail.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(groups[i].Seconds, GridUnitType.Star) });
                Border segment = new Border { Background = Color(colors[i % colors.Length]), Margin = new Thickness(0, 0, 2, 0), CornerRadius = new CornerRadius(2) }; Grid.SetColumn(segment, i); rail.Children.Add(segment);
            }
            if (groups.Length > 0) panel.Children.Add(rail);
            WrapPanel legend = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var group in groups) legend.Children.Add(new TextBlock { Text = group.Name + " " + (day.ActiveSeconds == 0 ? 0 : group.Seconds / day.ActiveSeconds * 100).ToString("0") + "%", Foreground = Color("#86917D"), FontSize = 9, Margin = new Thickness(0, 0, 15, 5) });
            panel.Children.Add(legend);
        }
        private void ShowAppMenu(AppStats app, Button anchor)
        {
            ContextMenu menu = new ContextMenu { Background = Color("#F6F5EF"), Foreground = Color("#24352E") };
            foreach (string category in settings.Categories)
            {
                string chosen = category; MenuItem item = new MenuItem { Header = category, IsCheckable = true, IsChecked = settings.Category(app) == category };
                item.Click += delegate { settings.Assign(app, chosen, settings.Excluded(app.Id)); SaveSettings(); categoryFingerprint = ""; Refresh(true); }; menu.Items.Add(item);
            }
            menu.Items.Add(new Separator()); MenuItem excluded = new MenuItem { Header = settings.Excluded(app.Id) ? "恢复记录此软件" : "不再记录此软件", ToolTip = "只影响之后的活跃时长和输入次数，已有记录保留。" };
            excluded.Click += delegate { if (tracker != null) tracker.Tick(); settings.Assign(app, settings.Category(app), !settings.Excluded(app.Id)); SaveSettings(); Refresh(true); }; menu.Items.Add(excluded);
            anchor.ContextMenu = menu; menu.PlacementTarget = anchor; menu.IsOpen = true;
        }
        private Window Dialog(string title, int width, int height, out StackPanel body)
        {
            Window dialog = new Window { Title = "牛马统计 · " + title, Width = width, Height = Math.Min(height, SystemParameters.WorkArea.Height - 30), ResizeMode = ResizeMode.NoResize, Background = Color("#F6F5EF"), Foreground = Color("#24352E"), FontFamily = new FontFamily("Microsoft YaHei UI"), Owner = window, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
            dialog.Resources.MergedDictionaries.Add(window.Resources);
            body = new StackPanel { Margin = new Thickness(24, 18, 24, 18) }; dialog.Content = new ScrollViewer { Content = body, Background = Color("#F6F5EF"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; return dialog;
        }
        private Button ActionButton(string text, Action action)
        {
            Button button = new Button { Content = text, FontSize = 11, Padding = new Thickness(12, 9, 12, 9), Background = Color("#E9EDE0"), Margin = new Thickness(0, 8, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Left };
            button.Click += delegate { try { action(); } catch (Exception ex) { MessageBox.Show(window, ex.Message, "牛马统计", MessageBoxButton.OK, MessageBoxImage.Error); } }; return button;
        }
        private void ShowReport()
        {
            if (tracker != null) tracker.Tick(); BitmapSource image = ReportBuilder.Build(engine.Day(selectedDate), settings);
            StackPanel body; Window dialog = Dialog("牛马日报", 440, 704, out body);
            body.Children.Add(new Image { Source = image, Stretch = Stretch.Uniform });
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ActionButton("复制图片", delegate { Clipboard.SetImage(image); }));
            Button export = ActionButton("保存 PNG ↗", delegate
            {
                SaveFileDialog file = new SaveFileDialog { FileName = "牛马日报-" + Store.Key(selectedDate) + ".png", Filter = "PNG 图片|*.png", AddExtension = true, DefaultExt = ".png" };
                if (file.ShowDialog(dialog) == true) { ReportBuilder.Save(image, file.FileName); dialog.Close(); }
            }); export.Margin = new Thickness(10, 8, 0, 0); buttons.Children.Add(export); body.Children.Add(buttons); dialog.ShowDialog();
        }
        private void ShowCategories()
        {
            StackPanel body; Window dialog = Dialog("管理分类", 390, 420, out body);
            body.Children.Add(new TextBlock { Text = "给时间分个类。", FontSize = 21, FontWeight = FontWeights.SemiBold });
            body.Children.Add(new TextBlock { Text = "改分类会同步用于历史统计和日报。", FontSize = 10, Foreground = Color("#8A9188"), Margin = new Thickness(0, 8, 0, 14) });
            ListBox list = new ListBox { Height = 150, Background = Color("#EEEFE6"), Foreground = Color("#24352E"), BorderThickness = new Thickness(0), FontSize = 12, ItemsSource = settings.Categories.ToArray() }; body.Children.Add(list);
            TextBox name = new TextBox { Height = 30, MaxLength = 12, FontSize = 12, Padding = new Thickness(7, 4, 7, 4), Margin = new Thickness(0, 12, 0, 0) }; body.Children.Add(name);
            body.Children.Add(ActionButton("＋ 添加分类", delegate
            {
                string category = name.Text.Trim(); if (category.Length == 0) return;
                if (settings.Categories.Count >= 12) throw new InvalidOperationException("最多保留 12 个分类。");
                if (!settings.Categories.Contains(category)) settings.Categories.Add(category);
                settings.Changed(); SaveSettings(); list.ItemsSource = settings.Categories.ToArray(); name.Text = ""; UpdateFilter(); Refresh(true);
            }));
            body.Children.Add(ActionButton("删除选中分类（软件归入其他）", delegate
            {
                string category = list.SelectedItem as string; if (category == null || category == "其他") return;
                settings.RemoveCategory(category); SaveSettings(); list.ItemsSource = settings.Categories.ToArray(); UpdateFilter(); Refresh(true);
            })); dialog.ShowDialog();
        }
        private void RenderDialog(Window dialog)
        {
            dialog.WindowStartupLocation = WindowStartupLocation.Manual; dialog.Left = -12000; dialog.Top = -12000;
            dialog.Loaded += delegate
            {
                dialog.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(delegate
                {
                    try
                    {
                        dialog.UpdateLayout(); FrameworkElement surface = (FrameworkElement)dialog.Content;
                        RenderTargetBitmap bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth * 1.5), (int)Math.Ceiling(surface.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
                        bitmap.Render(surface); ReportBuilder.Save(bitmap, renderPath); dialog.Close(); Exit();
                    }
                    catch (Exception ex) { File.WriteAllText(renderPath + ".error.txt", ex.ToString()); Environment.ExitCode = 1; dialog.Close(); Exit(); }
                }));
            }; dialog.Show();
        }
        internal void VerifyUi(Action<bool, string> assert, string benchmark)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -12000; window.Top = -12000; window.ShowInTaskbar = false; window.ShowActivated = false;
            Show(); timer.Stop(); SelectTab("apps");
            Grid first = allRows.Values.First().Row;
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 300; i++) Refresh(false);
            stopwatch.Stop(); assert(allRows.Values.Any(row => ReferenceEquals(row.Row, first)), "row reused");
            var allocatedBefore = GC.GetTotalMemory(true); for (int i = 0; i < 1000; i++) Refresh(false); var allocatedAfter = GC.GetTotalMemory(true);
            File.WriteAllText(benchmark, "300 refreshes: " + stopwatch.Elapsed.TotalMilliseconds.ToString("0.0") + " ms\nRetained managed bytes over 1000 refreshes: " + (allocatedAfter - allocatedBefore));
            Find<ComboBox>("CategoryFilter").SelectedItem = "工作"; assert(allRows.Count == 2, "category filter");
            Find<ComboBox>("CategoryFilter").SelectedItem = "娱乐"; assert(allRows.Count == 0, "empty category"); Refresh(false);
            Find<ComboBox>("CategoryFilter").SelectedItem = "全部分类";
            for (int i = 0; i < 120; i++) AppIcon(new AppStats { Id = "icon-test-" + i, Name = "Test", FilePath = "" }); assert(icons.Count <= 64, "icon cache capped");
            settings.Theme = "Dark"; ApplyTheme(); assert(Equals(window.Background.ToString(), "#FF1B241F"), "theme applied");
            settings.AutoHideMini = true; ToggleCompact(); Rect work = WorkArea(); window.Left = work.Left; window.Top = work.Top + 50;
            CollapseDock(true); assert(docked && window.Width == 32 && Find<Border>("DockRoot").Visibility == Visibility.Visible, "dock collapses");
            ExpandDock(); assert(!docked && window.Width == beforeDock.Width && Find<Grid>("MiniRoot").Visibility == Visibility.Visible, "dock expands");
            ToggleCompact();
            VerifyLiveInput(assert, benchmark);
        }
        private void VerifyLiveInput(Action<bool, string> assert, string benchmark)
        {
            // Exercise the same input processing as the native callbacks on a
            // worker thread, without injecting any input into the user's desktop.
            tracker = new Tracker(engine, settings, delegate { return IntPtr.Zero; });
            tracker.InputChanged += OnInputChanged;
            try
            {
                DayStats today = engine.Day(DateTime.Today);
                engine.AddInput(new InputBatch { Time = DateTime.Now }, AppIdentity.Unknown);
                selectedDate = DateTime.Today; dateFollowing = true; categoryFilter = "全部分类"; SelectTab("apps"); Refresh(true);
                AppRowView appView = allRows[AppIdentity.Unknown.Id]; Grid row = appView.Row; DateTime ranking = rankedAt;
                long keys = today.Keys, clicks = today.Clicks; double scroll = today.Scroll, distance = today.MousePixels;
                Action press = delegate { tracker.RecordKeyboard(0x100, new Native.KeyHook { Key = 65 }); tracker.RecordKeyboard(0x101, new Native.KeyHook { Key = 65 }); };
                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                SelfTests.OnInputThread(delegate
                {
                    press(); tracker.RecordMouse(0x201, new Native.MouseHook()); tracker.RecordMouse(0x20A, new Native.MouseHook { MouseData = 120u << 16 });
                    tracker.RecordMouse(0x200, new Native.MouseHook { Point = new Native.Point { X = 0, Y = 0 } }); tracker.RecordMouse(0x200, new Native.MouseHook { Point = new Native.Point { X = 3, Y = 4 } });
                });
                assert(SelfTests.PumpUntil(delegate { return Find<TextBlock>("KeysText").Text == Count(keys + 1) && Find<TextBlock>("ClicksText").Text == Count(clicks + 1); }, 500), "main counts render within 500 ms with sampler stopped");
                double latency = watch.Elapsed.TotalMilliseconds;
                assert(appView.Input.Text == "键 1  /  鼠 1", "existing software row updates live");
                assert(Find<TextBlock>("DistanceText").Text == "滚动 " + (scroll + 1).ToString("N0") + " 格 · 鼠标移动 " + (distance + 5).ToString("N0") + " px", "wheel and distance update live");
                assert(ReferenceEquals(allRows[AppIdentity.Unknown.Id].Row, row) && rankedAt == ranking, "input does not rebuild or reorder software rows");
                ToggleCompact(); SelfTests.OnInputThread(delegate { press(); tracker.RecordMouse(0x204, new Native.MouseHook()); });
                assert(SelfTests.PumpUntil(delegate { return Find<TextBlock>("MiniMetricsText").Text == "键盘 " + Count(keys + 2) + " · 鼠标 " + Count(clicks + 2); }, 500), "mini counts update live");
                ToggleCompact();
                selectedDate = DateTime.Today.AddDays(-1); dateFollowing = false; Refresh(true);
                string historyKeys = Find<TextBlock>("KeysText").Text, historyClicks = Find<TextBlock>("ClicksText").Text, historyMini = Find<TextBlock>("MiniMetricsText").Text;
                SelfTests.OnInputThread(delegate { press(); tracker.RecordMouse(0x201, new Native.MouseHook()); });
                assert(SelfTests.PumpUntil(delegate { return today.Keys == keys + 3 && today.Clicks == clicks + 3; }, 500), "today keeps collecting while viewing history");
                assert(Find<TextBlock>("KeysText").Text == historyKeys && Find<TextBlock>("ClicksText").Text == historyClicks && Find<TextBlock>("MiniMetricsText").Text == historyMini, "history counts remain unchanged");
                selectedDate = DateTime.Today; dateFollowing = true; Refresh(true);
                window.Hide(); string hiddenKeys = Find<TextBlock>("KeysText").Text;
                SelfTests.OnInputThread(press); assert(SelfTests.PumpUntil(delegate { return today.Keys == keys + 4; }, 500), "hidden window keeps collecting");
                assert(Find<TextBlock>("KeysText").Text == hiddenKeys, "hidden UI does not refresh");
                window.Show(); Refresh(true); assert(Find<TextBlock>("KeysText").Text == Count(keys + 4), "restored window shows latest counts");
                window.WindowState = WindowState.Minimized; string minimizedKeys = Find<TextBlock>("KeysText").Text;
                SelfTests.OnInputThread(press); assert(SelfTests.PumpUntil(delegate { return today.Keys == keys + 5; }, 500), "minimized window keeps collecting");
                assert(Find<TextBlock>("KeysText").Text == minimizedKeys, "minimized UI does not refresh");
                window.WindowState = WindowState.Normal; Refresh(true);
                File.AppendAllText(benchmark, "\nInput to main numeric labels: " + latency.ToString("0.0") + " ms\nMain/mini/app/history/hidden/minimized input checks passed.");
            }
            finally { tracker.InputChanged -= OnInputChanged; tracker.Dispose(); tracker = null; }
        }
    }
}

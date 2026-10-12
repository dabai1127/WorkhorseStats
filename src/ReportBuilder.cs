using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace NiuMa
{
    internal static class ReportBuilder
    {
        private static Brush Brush(string color) { return Palette.Get(color, false); }
        private static void Put(Canvas canvas, UIElement child, double x, double y) { Canvas.SetLeft(child, x); Canvas.SetTop(child, y); canvas.Children.Add(child); }
        private static TextBlock Label(string text, double size, string color, double width = Double.NaN, bool bold = false, bool number = false)
        {
            return new TextBlock { Text = text, FontFamily = new FontFamily(number ? "Bahnschrift" : "Microsoft YaHei UI"), FontSize = size, Foreground = Brush(color), Width = width, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
        }
        private static void Box(Canvas canvas, double x, double y, double width, double height, string color, double radius)
        { Put(canvas, new Border { Width = width, Height = height, Background = Brush(color), CornerRadius = new CornerRadius(radius) }, x, y); }
        public static BitmapSource Build(DayStats day, Settings settings)
        {
            Canvas canvas = new Canvas { Width = 560, Height = 760, Background = Brush("#F6F5EF") };
            using (Stream icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("NiuMa.app.ico"))
            {
                BitmapFrame logo = BitmapDecoder.Create(icon, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.OrderByDescending(f => f.PixelWidth).First(); logo.Freeze();
                Put(canvas, new Image { Source = logo, Width = 36, Height = 36 }, 30, 24);
            }
            Put(canvas, Label("NIUMA / DAILY", 11, "#677A5E", 170, false, true), 74, 37);
            Put(canvas, Label(day.Date.Replace('-', '.'), 14, "#798B6D", 120, false, true), 408, 34);
            Put(canvas, Label("牛马日报", 29, "#24352E", 360, true), 32, 78);
            Put(canvas, Label(day.ActiveSeconds > 0 ? "把忙碌，算清楚。" : "今天的记录，还是一张白纸。", 12, "#8A9188", 400), 33, 120);
            Box(canvas, 32, 157, 496, 140, "#223F35", 20);
            Put(canvas, Label("屏幕活跃", 11, "#B9CABB"), 53, 176);
            int minutes = (int)(day.ActiveSeconds / 60);
            Put(canvas, Label(minutes / 60 + "h " + (minutes % 60).ToString("00") + "m", 45, "#F3F5E5", 300, false, true), 50, 200);
            double ratio = day.ScreenSeconds == 0 ? 0 : Math.Min(1, day.ActiveSeconds / day.ScreenSeconds);
            Put(canvas, Label((ratio * 100).ToString("0") + "% 活跃", 11, "#CBDBB4"), 396, 221);
            Box(canvas, 53, 262, 454, 4, "#415B49", 2); Box(canvas, 53, 262, 454 * ratio, 4, "#D2DFA0", 2);
            Put(canvas, Label("开屏 " + Shell.Duration(day.ScreenSeconds), 10, "#A9BEAA"), 53, 275);
            Put(canvas, Label("离开 / 排除 " + Shell.Duration(day.ScreenSeconds - day.ActiveSeconds), 10, "#A9BEAA", 190), 327, 275);
            Box(canvas, 32, 311, 240, 78, "#EFEEE5", 12); Box(canvas, 288, 311, 240, 78, "#F2EADF", 12);
            Put(canvas, Label("键盘敲击", 10, "#737F6D"), 47, 323); Put(canvas, Label(day.Keys.ToString("N0") + " 次", 26, "#24352E", 210, false, true), 47, 343);
            Put(canvas, Label("鼠标点击", 10, "#947B67"), 303, 323); Put(canvas, Label(day.Clicks.ToString("N0") + " 次", 26, "#24352E", 210, false, true), 303, 343);
            Put(canvas, Label("时间都去哪了", 13, "#24352E", 250, true), 32, 411);
            AppStats[] apps = day.Apps.OrderByDescending(a => a.ActiveSeconds).Take(4).ToArray();
            if (apps.Length == 0) Put(canvas, Label("还没有软件使用记录。", 12, "#8A9188"), 32, 448);
            for (int i = 0; i < apps.Length; i++)
            {
                AppStats app = apps[i]; double y = 443 + i * 38;
                Put(canvas, Label((i + 1).ToString("00"), 11, "#A0AC93", 25, false, true), 32, y + 1);
                Put(canvas, Label(app.Name, 11, "#52624D", 265), 62, y);
                Put(canvas, Label(settings.Category(app), 9, "#8F9C7E", 54), 340, y + 2);
                Put(canvas, Label(Shell.Duration(app.ActiveSeconds), 10, "#7E8875", 120), 412, y + 1);
                Box(canvas, 62, y + 23, 464, 3, "#E7EADF", 2);
                Box(canvas, 62, y + 23, 464 * (day.ActiveSeconds > 0 ? Math.Min(1, app.ActiveSeconds / day.ActiveSeconds) : 0), 3, "#91A781", 2);
            }
            var categories = day.Apps.GroupBy(settings.Category).Select(g => new { Name = g.Key, Seconds = g.Sum(a => a.ActiveSeconds) }).OrderByDescending(g => g.Seconds).Where(g => g.Seconds > 0).Take(4).ToArray();
            double x = 32;
            foreach (var category in categories)
            {
                string text = category.Name + " " + (day.ActiveSeconds > 0 ? category.Seconds / day.ActiveSeconds * 100 : 0).ToString("0") + "%";
                Box(canvas, x, 608, 116, 26, "#E9EDE0", 7); Put(canvas, Label(text, 10, "#809171", 104), x + 10, 614); x += 127;
            }
            Put(canvas, Label("一天的节奏", 11, "#24352E", 230, true), 32, 651);
            for (int i = 0; i < 24; i++)
            {
                double height = Math.Max(2, Math.Min(31, day.Hours[i].ActiveSeconds / 3600 * 31));
                Box(canvas, 32 + i * 20.8, 702 - height, 16, height, day.Hours[i].ActiveSeconds > 0 ? "#7F9971" : "#DCE1D1", 2);
            }
            Put(canvas, Label("00", 8, "#9DA292", 20, false, true), 32, 708); Put(canvas, Label("12", 8, "#9DA292", 20, false, true), 274, 708); Put(canvas, Label("24", 8, "#9DA292", 20, false, true), 511, 708);
            Put(canvas, Label("牛马统计 · 当前已记录数据 · 本机生成", 8, "#A0A594", 400), 32, 739);
            canvas.Measure(new Size(560, 760)); canvas.Arrange(new Rect(0, 0, 560, 760)); canvas.UpdateLayout();
            RenderTargetBitmap bitmap = new RenderTargetBitmap(1120, 1520, 192, 192, PixelFormats.Pbgra32); bitmap.Render(canvas); bitmap.Freeze(); return bitmap;
        }
        public static void Save(BitmapSource bitmap, string path)
        {
            PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (FileStream stream = File.Create(path)) encoder.Save(stream);
        }
    }
}

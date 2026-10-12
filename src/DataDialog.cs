using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace NiuMa
{
    internal sealed partial class Shell
    {
        private void ShowDataManager()
        {
            if (tracker != null) tracker.Tick(); Save();
            StackPanel body; Window dialog = Dialog("数据管理", 430, 690, out body);
            body.Children.Add(new TextBlock { Text = "数据，都在自己手里。", FontSize = 21, FontWeight = FontWeights.SemiBold });
            TextBlock summary = new TextBlock { FontSize = 11, Foreground = Color("#65765B"), LineHeight = 23, TextWrapping = TextWrapping.Wrap };
            Action update = delegate
            {
                var dates = store.ListDates(); long bytes = Directory.GetFiles(store.DaysDirectory).Sum(p => new FileInfo(p).Length);
                string backups = Path.Combine(store.Root, "backups"); long backupBytes = Directory.Exists(backups) ? Directory.GetFiles(backups, "*.zip").Sum(p => new FileInfo(p).Length) : 0;
                summary.Text = dates.Count + " 天记录 · " + (bytes / 1024.0).ToString("0.0") + " KB\n" + (dates.Count > 0 ? Store.Key(dates.First()) + " — " + Store.Key(dates.Last()) : "开始记录后，这里会显示日期范围。") + "\n安全备份 " + (backupBytes / 1024.0).ToString("0.0") + " KB · 排除 " + settings.AppRules.Count(r => r.Excluded) + " 款软件";
            }; update();
            body.Children.Add(new Border { Child = summary, Background = Color("#E9EDE0"), CornerRadius = new CornerRadius(12), Padding = new Thickness(15), Margin = new Thickness(0, 15, 0, 8) });
            body.Children.Add(ActionButton("备份全部数据 → ZIP", delegate
            {
                SaveFileDialog file = new SaveFileDialog { FileName = "牛马统计备份-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".zip", Filter = "牛马统计备份|*.zip", DefaultExt = ".zip", AddExtension = true };
                if (file.ShowDialog(dialog) == true) { if (tracker != null) tracker.Tick(); Save(); DataManager.Backup(store, settings, file.FileName); MessageBox.Show(dialog, "每日统计、分类和偏好已备份。", "备份完成"); }
            }));
            body.Children.Add(ActionButton("从 ZIP 恢复数据", delegate
            {
                OpenFileDialog file = new OpenFileDialog { Filter = "牛马统计备份|*.zip", Title = "选择牛马统计备份" };
                if (file.ShowDialog(dialog) != true) return;
                BackupPackage package = DataManager.Inspect(file.FileName);
                StackPanel previewBody; Window preview = Dialog("恢复预览", 380, 340, out previewBody); preview.Owner = dialog;
                previewBody.Children.Add(new TextBlock { Text = "准备导入 " + package.Days.Count + " 天记录", FontSize = 18, FontWeight = FontWeights.SemiBold });
                previewBody.Children.Add(new TextBlock { Text = package.Days.Count > 0 ? package.Days.First().Date + " — " + package.Days.Last().Date : "备份中只有设置。", FontSize = 11, Foreground = Color("#8A9188"), Margin = new Thickness(0, 10, 0, 15) });
                CheckBox overwrite = new CheckBox { Content = "覆盖本机同日期记录", FontSize = 11, Margin = new Thickness(0, 0, 0, 12) };
                CheckBox preferences = new CheckBox { Content = "一并恢复分类、排除和偏好", FontSize = 11, Margin = new Thickness(0, 0, 0, 12) }; previewBody.Children.Add(overwrite); previewBody.Children.Add(preferences);
                previewBody.Children.Add(new TextBlock { Text = "默认只恢复缺失日期；恢复前自动保存安全备份。\n登录启动项保持当前 Windows 设置。", FontSize = 10, LineHeight = 19, Foreground = Color("#8A9188"), TextWrapping = TextWrapping.Wrap });
                previewBody.Children.Add(ActionButton("确认恢复", delegate
                {
                    bool wasPaused = tracker == null || tracker.Paused;
                    if (!wasPaused) tracker.TogglePause(); Save();
                    try
                    {
                        ImportResult result = DataManager.Restore(store, settings, package, overwrite.IsChecked == true, preferences.IsChecked == true);
                        if (preferences.IsChecked == true)
                        {
                            Settings restored = store.LoadSettings();
                            foreach (FieldInfo field in typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance)) field.SetValue(settings, field.GetValue(restored));
                            settings.Normalize(); settings.MiniMode = compact; window.Topmost = settings.Topmost; ApplyTheme(); RestorePosition(); UpdateFilter();
                        }
                        engine.Reload(); categoryFingerprint = ""; rowsDate = ""; Refresh(true); update(); preview.Close();
                        MessageBox.Show(dialog, "已导入 " + result.Imported + " 天，保留本机已有 " + result.Skipped + " 天。" + (result.SafetyBackup == null ? "" : "\n恢复前的安全备份保存在 data/backups。"), "恢复完成");
                    }
                    finally { if (!wasPaused) tracker.TogglePause(); }
                })); preview.ShowDialog();
            }));
            body.Children.Add(ActionButton("批量导出全部日期 → CSV", delegate
            {
                using (Forms.FolderBrowserDialog picker = new Forms.FolderBrowserDialog { Description = "选择导出目录；会创建独立的统计导出子文件夹。" })
                {
                    if (picker.ShowDialog() != Forms.DialogResult.OK) return;
                    if (tracker != null) tracker.Tick(); Save();
                    string directory = Path.Combine(picker.SelectedPath, "牛马统计导出-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")); Directory.CreateDirectory(directory);
                    var dates = store.ListDates(); foreach (DateTime date in dates) CsvExport.Save(Path.Combine(directory, "牛马统计-" + Store.Key(date) + ".csv"), store.LoadDay(date));
                    MessageBox.Show(dialog, "已导出 " + dates.Count + " 天记录。\n" + directory, "导出完成");
                }
            }));
            body.Children.Add(new TextBlock { Text = "清理旧记录", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 9) });
            DatePicker cutoff = new DatePicker { SelectedDate = DateTime.Today.AddDays(-90), DisplayDateEnd = DateTime.Today, FontSize = 11 }; body.Children.Add(cutoff);
            body.Children.Add(ActionButton("清理此日期之前的记录", delegate
            {
                DateTime date = cutoff.SelectedDate ?? DateTime.Today.AddDays(-90); int count = store.ListDates().Count(d => d < date && d < DateTime.Today);
                if (count == 0) { MessageBox.Show(dialog, "这个日期之前没有可清理的记录。", "牛马统计"); return; }
                if (MessageBox.Show(dialog, "将清理 " + count + " 天旧记录，今天的数据保留。\n清理前会自动备份，之后可从 ZIP 恢复。", "清理旧记录", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                Save(); DataManager.ClearBefore(store, settings, date); engine.Reload(); rowsDate = ""; update(); Refresh(true);
            }));
            body.Children.Add(new TextBlock { Text = "排除的软件", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 5) });
            StackPanel exclusions = new StackPanel(); body.Children.Add(exclusions);
            Action showExclusions = null;
            showExclusions = delegate
            {
                exclusions.Children.Clear();
                foreach (AppRule rule in settings.AppRules.Where(r => r.Excluded).ToArray())
                {
                    AppRule selected = rule;
                    exclusions.Children.Add(ActionButton((rule.Name ?? rule.Id) + " · 恢复统计", delegate { selected.Excluded = false; settings.Changed(); SaveSettings(); showExclusions(); update(); Refresh(true); }));
                }
                if (exclusions.Children.Count == 0) exclusions.Children.Add(new TextBlock { Text = "在软件列表中点击分类标签，可排除指定软件。", FontSize = 10, Foreground = Color("#8A9188"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) });
            }; showExclusions();
            body.Children.Add(ActionButton("打开本地数据文件夹 ↗", delegate { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + store.Root + "\"") { UseShellExecute = true }); }));
            if (initialView == "data" && renderPath != null) RenderDialog(dialog); else dialog.ShowDialog();
        }
    }
}

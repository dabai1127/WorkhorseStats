using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

[assembly: AssemblyTitle("牛马统计")]
[assembly: AssemblyDescription("本地屏幕、软件、键盘和鼠标使用统计")]
[assembly: AssemblyProduct("牛马统计")]
[assembly: AssemblyVersion("1.1.1.0")]
[assembly: AssemblyFileVersion("1.1.1.0")]

namespace NiuMa
{
    internal static class Program
    {
        internal static string Argument(string[] args, string flag)
        {
            int index = Array.IndexOf(args, flag);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        [STAThread]
        public static void Main(string[] args)
        {
            string root = Argument(args, "--data-dir") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            bool demo = Array.IndexOf(args, "--demo") >= 0;
            string render = Argument(args, "--render");
            if (Array.IndexOf(args, "--self-test") >= 0)
            {
                Environment.Exit(SelfTests.Run(Argument(args, "--report") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "artifacts", "test-report.txt")));
                return;
            }
            Mutex mutex = null;
            string snapshot = Argument(args, "--backup-to");
            if (snapshot != null)
            {
                try { Store source = new Store(root); DataManager.Backup(source, source.LoadSettings(), snapshot); }
                catch (Exception ex) { File.WriteAllText(snapshot + ".error.txt", ex.ToString()); Environment.ExitCode = 1; }
                return;
            }
            try
            {
                string token;
                using (SHA256 hash = SHA256.Create()) token = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToLowerInvariant()))).Replace("-", "").Substring(0, 20);
                uint showMessage = Shell.RegisterWindowMessage("NiuMaStats.Show." + token);
                bool created;
                if (!demo && render == null)
                {
                    mutex = new Mutex(true, "Local\\NiuMaStats." + token, out created);
                    if (!created) { Shell.PostMessage(new IntPtr(0xffff), showMessage, IntPtr.Zero, IntPtr.Zero); return; }
                }
                Store store = new Store(root);
                Settings settings = store.LoadSettings();
                StatsEngine engine = new StatsEngine(store, settings);
                if (demo) DemoData.Fill(engine);
                Application application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                Shell shell = new Shell(application, engine, store, settings, demo, render, Argument(args, "--view"), showMessage);
                application.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                {
                    try { File.AppendAllText(Path.Combine(root, "error.log"), DateTime.Now.ToString("s") + " " + e.Exception + Environment.NewLine); } catch { }
                    MessageBox.Show("发生了一个错误，已写入 data/error.log。\n" + e.Exception.Message, "牛马统计", MessageBoxButton.OK, MessageBoxImage.Error);
                    e.Handled = true; shell.Exit();
                };
                shell.Show(); application.Run();
            }
            catch (Exception ex)
            {
                if (render != null)
                {
                    try { File.WriteAllText(render + ".error.txt", ex.ToString()); } catch { }
                }
                else MessageBox.Show("无法启动牛马统计。\n" + ex.Message, "牛马统计", MessageBoxButton.OK, MessageBoxImage.Error);
                Environment.ExitCode = 1;
            }
            finally { if (mutex != null) mutex.Dispose(); }
        }
    }
}

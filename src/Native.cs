using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace NiuMa
{
    internal static class Native
    {
        internal delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct MouseHook { public Point Point; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] internal struct KeyHook { public uint Key, Scan, Flags, Time; public IntPtr ExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] internal struct LastInput { public uint Size, Time; }
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] internal static extern bool GetLastInputInfo(ref LastInput info);
        [DllImport("kernel32.dll")] internal static extern uint GetTickCount();
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] internal static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr OpenProcess(uint access, bool inherit, uint process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] internal static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("wtsapi32.dll")] internal static extern bool WTSRegisterSessionNotification(IntPtr window, int flags);
        [DllImport("wtsapi32.dll")] internal static extern bool WTSUnRegisterSessionNotification(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr RegisterPowerSettingNotification(IntPtr window, ref Guid setting, int flags);
        [DllImport("user32.dll")] internal static extern bool UnregisterPowerSettingNotification(IntPtr notification);
        [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        internal static double IdleSeconds()
        {
            LastInput input = new LastInput { Size = (uint)Marshal.SizeOf(typeof(LastInput)) };
            if (!GetLastInputInfo(ref input)) return 0;
            return unchecked(GetTickCount() - input.Time) / 1000.0;
        }
        internal static bool IsDesktopLocked()
        {
            IntPtr desktop = OpenInputDesktop(0, false, 0x0100);
            if (desktop == IntPtr.Zero) return true;
            CloseDesktop(desktop); return false;
        }
    }

    internal sealed class AppResolver
    {
        private readonly Dictionary<string, AppIdentity> cache = new Dictionary<string, AppIdentity>(StringComparer.OrdinalIgnoreCase);
        private IntPtr previousWindow;
        private uint previousProcess;
        private AppIdentity previousApp;
        private long previousTick;
        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            {"chrome", "Google Chrome"}, {"msedge", "Microsoft Edge"}, {"firefox", "Firefox"}, {"Code", "Visual Studio Code"},
            {"WeChat", "微信"}, {"Weixin", "微信"}, {"DingTalk", "钉钉"}, {"WXWork", "企业微信"}, {"QQ", "QQ"},
            {"WINWORD", "Microsoft Word"}, {"EXCEL", "Microsoft Excel"}, {"POWERPNT", "PowerPoint"}, {"OUTLOOK", "Outlook"},
            {"explorer", "文件资源管理器"}, {"WindowsTerminal", "Windows Terminal"}, {"notepad", "记事本"},
            {"Photoshop", "Adobe Photoshop"}, {"Figma", "Figma"}, {"Obsidian", "Obsidian"}, {"牛马统计", "牛马统计"}
        };
        public AppIdentity Resolve(IntPtr window)
        {
            uint id; Native.GetWindowThreadProcessId(window, out id);
            if (id == 0) return AppIdentity.Unknown;
            long tick = Stopwatch.GetTimestamp();
            if (window == previousWindow && id == previousProcess && previousApp != null && (tick - previousTick) / (double)Stopwatch.Frequency < 5) return previousApp;
            IntPtr process = Native.OpenProcess(0x1000, false, id);
            string path = "";
            if (process != IntPtr.Zero)
            {
                try { StringBuilder buffer = new StringBuilder(1024); int length = buffer.Capacity; if (Native.QueryFullProcessImageName(process, 0, buffer, ref length)) path = buffer.ToString(); else if (Marshal.GetLastWin32Error() == 122) { buffer = new StringBuilder(32768); length = buffer.Capacity; if (Native.QueryFullProcessImageName(process, 0, buffer, ref length)) path = buffer.ToString(); } }
                finally { Native.CloseHandle(process); }
            }
            string name;
            if (path.Length > 0) name = Path.GetFileNameWithoutExtension(path);
            else
            {
                try { using (Process fallback = Process.GetProcessById((int)id)) name = fallback.ProcessName; }
                catch (ArgumentException) { return AppIdentity.Unknown; }
                catch (System.ComponentModel.Win32Exception) { return AppIdentity.Unknown; }
                catch (InvalidOperationException) { return AppIdentity.Unknown; }
            }
            string key = path.Length > 0 ? path.ToLowerInvariant() : "process:" + name.ToLowerInvariant();
            AppIdentity app;
            if (cache.TryGetValue(key, out app)) { Remember(window, id, app, tick); return app; }
            string friendly;
            if (!Names.TryGetValue(name, out friendly)) friendly = name;
            app = new AppIdentity { Id = key, Name = friendly, FilePath = path };
            if (cache.Count >= 256) cache.Clear();
            cache[key] = app; Remember(window, id, app, tick); return app;
        }
        private void Remember(IntPtr window, uint id, AppIdentity app, long tick) { previousWindow = window; previousProcess = id; previousApp = app; previousTick = tick; }
    }

    internal sealed class Tracker : IDisposable
    {
        private readonly StatsEngine engine;
        private readonly Settings settings;
        private readonly AppResolver resolver = new AppResolver();
        private readonly Func<IntPtr> foregroundWindow;
        // Only transient virtual-key state for repeat suppression. No key values leave memory.
        private readonly HashSet<uint> held = new HashSet<uint>();
        private readonly List<InputBatch> pending = new List<InputBatch>();
        private readonly object inputGate = new object();
        private readonly ManualResetEvent hooksReady = new ManualResetEvent(false);
        private readonly Dispatcher dispatcher;
        private readonly DispatcherTimer inputTimer;
        private bool inputFlushQueued;
        private volatile bool disposed;
        private volatile int lastHookActivity = Environment.TickCount;
        private Thread inputThread;
        private uint inputThreadId;
        private readonly Native.HookProc keyCallback, mouseCallback;
        private IntPtr keyHook, mouseHook, powerNotification, window;
        private HwndSource source;
        private long lastStamp;
        private DateTime lastWall;
        private AppIdentity lastApp = AppIdentity.Unknown;
        private Native.Point lastMouse;
        private bool mouseKnown;
        private volatile bool locked, displayOn = true, suspended, paused, resetInput;
        public bool Paused { get { return paused; } }
        public bool HookHealthy { get { return keyHook != IntPtr.Zero && mouseHook != IntPtr.Zero; } }
        public bool ScreenAvailable { get { return !locked && displayOn && !suspended; } }
        public bool Idle { get { return Native.IdleSeconds() >= settings.IdleSeconds; } }
        public string ForegroundName { get { return lastApp.Name; } }
        public string HookError { get; private set; }
        public event EventHandler InputChanged;
        public Tracker(StatsEngine engine, Settings settings)
            : this(engine, settings, Native.GetForegroundWindow) { }
        internal Tracker(StatsEngine engine, Settings settings, Func<IntPtr> foregroundWindow)
        {
            this.engine = engine; this.settings = settings; keyCallback = Keyboard; mouseCallback = Mouse;
            this.foregroundWindow = foregroundWindow;
            dispatcher = Dispatcher.CurrentDispatcher;
            inputTimer = new DispatcherTimer(DispatcherPriority.Input, dispatcher) { Interval = TimeSpan.FromMilliseconds(75) };
            inputTimer.Tick += delegate { inputTimer.Stop(); FlushInput(); };
        }
        public void Start(Window owner)
        {
            window = new WindowInteropHelper(owner).Handle;
            source = HwndSource.FromHwnd(window); source.AddHook(WindowMessage);
            Native.WTSRegisterSessionNotification(window, 0);
            Guid consoleDisplay = new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47");
            powerNotification = Native.RegisterPowerSettingNotification(window, ref consoleDisplay, 0);
            locked = Native.IsDesktopLocked();
            inputThread = new Thread(delegate()
            {
                inputThreadId = Native.GetCurrentThreadId();
                InstallInputHooks();
                // Creating the application context before signaling ensures a live message queue.
                using (System.Windows.Forms.ApplicationContext context = new System.Windows.Forms.ApplicationContext())
                using (System.Windows.Forms.Timer monitor = new System.Windows.Forms.Timer { Interval = 2000 })
                {
                    monitor.Tick += delegate { CheckInputHooks(); }; monitor.Start();
                    System.Windows.Forms.Application.Idle += OnInputReady;
                    try { System.Windows.Forms.Application.Run(context); }
                    finally
                    {
                        monitor.Stop();
                        System.Windows.Forms.Application.Idle -= OnInputReady;
                        if (keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyHook);
                        if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
                        keyHook = mouseHook = IntPtr.Zero;
                    }
                }
            });
            inputThread.IsBackground = true; inputThread.Name = "NiuMa Input Counter"; inputThread.SetApartmentState(ApartmentState.STA); inputThread.Start();
            if (!hooksReady.WaitOne(3000)) HookError = "输入监听启动超时";
            lastApp = resolver.Resolve(foregroundWindow()); ResetClock();
        }
        private void InstallInputHooks()
        {
            if (keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyHook);
            if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
            keyHook = Native.SetWindowsHookEx(13, keyCallback, Native.GetModuleHandle(null), 0);
            int keyError = Marshal.GetLastWin32Error();
            mouseHook = Native.SetWindowsHookEx(14, mouseCallback, Native.GetModuleHandle(null), 0);
            int mouseError = Marshal.GetLastWin32Error();
            HookError = HookHealthy ? "" : "输入监听未就绪（" + keyError + " / " + mouseError + "）";
            lastHookActivity = Environment.TickCount; resetInput = true;
        }
        internal static bool InputHooksStale(uint now, uint lastInput, uint lastCallback)
        {
            uint inputAge = unchecked(now - lastInput), callbackAge = unchecked(now - lastCallback);
            return inputAge < 2000 && callbackAge >= 4000;
        }
        private void CheckInputHooks()
        {
            if (disposed) return;
            Native.LastInput input = new Native.LastInput { Size = (uint)Marshal.SizeOf(typeof(Native.LastInput)) };
            // Windows can silently remove low-level hooks after a scheduling stall.
            // Reinstall on their own thread only when Windows reports fresh input
            // but our callbacks have stopped; idle periods require no reinstallation.
            if (!HookHealthy || (Native.GetLastInputInfo(ref input) && InputHooksStale(Native.GetTickCount(), input.Time, unchecked((uint)lastHookActivity)))) InstallInputHooks();
        }
        private void OnInputReady(object sender, EventArgs e) { hooksReady.Set(); }
        private void ResetClock() { lastStamp = Stopwatch.GetTimestamp(); lastWall = DateTime.Now; resetInput = true; }
        public void Tick()
        {
            long stamp = Stopwatch.GetTimestamp(); DateTime now = DateTime.Now;
            double elapsed = (stamp - lastStamp) / (double)Stopwatch.Frequency;
            // A long scheduling gap or clock jump must never become invented screen time.
            if (!Paused && ScreenAvailable && elapsed > 0 && elapsed <= 5 && Math.Abs((now - lastWall).TotalSeconds - elapsed) < 2)
            {
                DateTime activeUntil = now.AddSeconds(settings.IdleSeconds - Native.IdleSeconds());
                engine.AddInterval(now.AddSeconds(-elapsed), now, true, activeUntil, lastApp);
            }
            FlushInput(); lastStamp = stamp; lastWall = now;
            lastApp = resolver.Resolve(foregroundWindow());
        }
        private void FlushInput()
        {
            InputBatch[] inputs;
            lock (inputGate) { inputs = pending.ToArray(); pending.Clear(); inputFlushQueued = false; }
            foreach (InputBatch batch in inputs) engine.AddInput(batch, resolver.Resolve(batch.Window));
            EventHandler changed = InputChanged;
            if (inputs.Length > 0 && !disposed && changed != null) changed(this, EventArgs.Empty);
        }
        private void ScheduleInputFlush()
        {
            // One dispatcher post per batch, even with a high polling-rate mouse.
            // The one-shot timer stays stopped while there is no input.
            lock (inputGate)
            {
                if (disposed || inputFlushQueued || pending.Count == 0) return;
                inputFlushQueued = true;
            }
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(delegate
            {
                if (disposed) return;
                lock (inputGate) { if (pending.Count == 0) return; }
                if (!inputTimer.IsEnabled) inputTimer.Start();
            }));
        }
        public void TogglePause() { Tick(); paused = !paused; ResetClock(); }
        public void SettingsChanged() { Tick(); ResetClock(); }
        private InputBatch Batch()
        {
            DateTime now = DateTime.Now; IntPtr foreground = foregroundWindow();
            InputBatch last = pending.Count > 0 ? pending[pending.Count - 1] : null;
            if (last != null && last.Window == foreground && last.Time.Date == now.Date && last.Time.Hour == now.Hour) return last;
            last = new InputBatch { Time = now, Window = foreground }; pending.Add(last); return last;
        }
        private IntPtr Keyboard(int code, IntPtr message, IntPtr data)
        {
            lastHookActivity = Environment.TickCount;
            if (code >= 0) RecordKeyboard(message.ToInt32(), (Native.KeyHook)Marshal.PtrToStructure(data, typeof(Native.KeyHook)));
            return Native.CallNextHookEx(keyHook, code, message, data);
        }
        internal void RecordKeyboard(int type, Native.KeyHook key)
        {
            if (disposed) return;
            if (resetInput) { held.Clear(); mouseKnown = false; resetInput = false; }
            if ((key.Flags & 0x10) == 0)
            {
                if (type == 0x101 || type == 0x105) held.Remove(key.Key);
                else if ((type == 0x100 || type == 0x104) && held.Add(key.Key) && !Paused && ScreenAvailable)
                {
                    lock (inputGate) Batch().Keys++;
                    ScheduleInputFlush();
                }
            }
        }
        private IntPtr Mouse(int code, IntPtr message, IntPtr data)
        {
            lastHookActivity = Environment.TickCount;
            if (code >= 0) RecordMouse(message.ToInt32(), (Native.MouseHook)Marshal.PtrToStructure(data, typeof(Native.MouseHook)));
            return Native.CallNextHookEx(mouseHook, code, message, data);
        }
        internal void RecordMouse(int type, Native.MouseHook mouse)
        {
            if (disposed) return;
            if (resetInput) { held.Clear(); mouseKnown = false; resetInput = false; }
            if ((mouse.Flags & 1) != 0) return;
            bool recorded = false;
            if (!Paused && ScreenAvailable)
            {
                lock (inputGate)
                {
                    if (type == 0x201) { Batch().Left++; recorded = true; }
                    else if (type == 0x204) { Batch().Right++; recorded = true; }
                    else if (type == 0x207 || type == 0x20B) { Batch().Middle++; recorded = true; }
                    else if (type == 0x20A || type == 0x20E)
                    {
                        double scroll = Math.Abs((short)(mouse.MouseData >> 16) / 120.0);
                        if (scroll > 0) { Batch().Scroll += scroll; recorded = true; }
                    }
                    else if (type == 0x200 && mouseKnown)
                    {
                        double x = mouse.Point.X - (double)lastMouse.X, y = mouse.Point.Y - (double)lastMouse.Y;
                        if (x != 0 || y != 0) { Batch().Distance += Math.Sqrt(x * x + y * y); recorded = true; }
                    }
                }
            }
            if (type == 0x200) { lastMouse = mouse.Point; mouseKnown = !Paused && ScreenAvailable; }
            if (recorded) ScheduleInputFlush();
        }
        private IntPtr WindowMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x02B1)
            {
                int state = wParam.ToInt32();
                if (state == 7 || state == 2 || state == 4) { Tick(); locked = true; ResetClock(); }
                else if (state == 8 || state == 1 || state == 3) { locked = Native.IsDesktopLocked(); ResetClock(); }
            }
            else if (message == 0x0218)
            {
                int power = wParam.ToInt32();
                if (power == 4) { Tick(); suspended = true; ResetClock(); }
                else if (power == 7 || power == 18) { suspended = false; locked = Native.IsDesktopLocked(); ResetClock(); }
                else if (power == 0x8013 && lParam != IntPtr.Zero)
                {
                    Guid setting = (Guid)Marshal.PtrToStructure(lParam, typeof(Guid));
                    if (setting == new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47"))
                    { Tick(); displayOn = Marshal.ReadInt32(lParam, 20) != 0; ResetClock(); }
                }
            }
            return IntPtr.Zero;
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; inputTimer.Stop();
            if (lastStamp != 0) Tick();
            if (inputThread != null)
            {
                Native.PostThreadMessage(inputThreadId, 0x0012, IntPtr.Zero, IntPtr.Zero);
                inputThread.Join(3000);
            }
            FlushInput();
            if (powerNotification != IntPtr.Zero) Native.UnregisterPowerSettingNotification(powerNotification);
            if (window != IntPtr.Zero) Native.WTSUnRegisterSessionNotification(window);
            if (source != null) source.RemoveHook(WindowMessage);
            keyHook = mouseHook = IntPtr.Zero;
            hooksReady.Dispose();
        }
    }
}

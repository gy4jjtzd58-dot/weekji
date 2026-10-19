using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace ChineseWeeklyPlanner
{
    internal sealed class DesktopEnvironmentChangedEventArgs : EventArgs
    {
        internal IntPtr Foreground { get; private set; }
        internal bool IsFullscreen { get; private set; }
        internal bool DesktopTransition { get; private set; }
        internal bool ShellReordered { get; private set; }

        internal DesktopEnvironmentChangedEventArgs(IntPtr foreground, bool fullscreen,
            bool desktopTransition, bool shellReordered)
        {
            Foreground = foreground;
            IsFullscreen = fullscreen;
            DesktopTransition = desktopTransition;
            ShellReordered = shellReordered;
        }
    }

    // One event source is shared by the application and both desktop components.
    // It observes Windows notifications; it never injects code, reads game data,
    // intercepts input, scans processes or changes another application's HWND.
    internal static class DesktopEnvironmentMonitor
    {
        private const uint EventSystemForeground = 0x0003;
        private const uint EventObjectReorder = 0x8004;
        private const uint EventObjectLocationChange = 0x800B;
        private const uint WinEventOutOfContextSkipOwnProcess = 0x0002;
        private const uint MonitorDefaultToNearest = 0x0002;
        private const int DwmExtendedFrameBounds = 9;
        private const int DwmCloaked = 14;
        private const int RunningD3DFullscreen = 3;
        private const string DesktopComponentProperty = "ChineseWeeklyPlanner.DesktopComponent.v1";
        private const double CheckIntervalMilliseconds = 150;

        private static readonly object gate = new object();
        private static readonly WinEventCallback callback = OnWinEvent;
        private static readonly uint ownProcess = GetCurrentProcessId();
        private static Dispatcher dispatcher;
        private static DispatcherTimer coalescingTimer;
        private static DispatcherTimer fullscreenRecoveryTimer;
        private static IntPtr foregroundHook;
        private static IntPtr shellReorderHook;
        private static IntPtr foregroundLocationHook;
        private static IntPtr observedForeground;
        private static uint shellProcess;
        private static uint locationProcess;
        private static int references;
        private static bool checkQueued;
        private static bool foregroundCheckQueued;
        private static int queueVersion;
        private static int coalescingVersion;
        private static bool pendingShellReordered;
        private static bool initialized;
        private static bool fullscreen;
        private static bool desktopForeground;
        private static long lastCheckTimestamp;

        internal static event EventHandler<DesktopEnvironmentChangedEventArgs> Changed;

        internal static bool IsFullscreen { get { return fullscreen; } }
        internal static bool IsDesktopForeground { get { return desktopForeground; } }
        internal static bool ForegroundHookActive { get { return foregroundHook != IntPtr.Zero; } }
        internal static bool ShellReorderHookActive { get { return shellReorderHook != IntPtr.Zero; } }

        internal static int SubscriptionHookCount
        {
            get
            {
                return (foregroundHook != IntPtr.Zero ? 1 : 0) +
                    (shellReorderHook != IntPtr.Zero ? 1 : 0) +
                    (foregroundLocationHook != IntPtr.Zero ? 1 : 0);
            }
        }

        internal static void Acquire(Dispatcher targetDispatcher)
        {
            if (targetDispatcher == null) throw new ArgumentNullException("targetDispatcher");
            targetDispatcher.VerifyAccess();
            if (references > 0)
            {
                if (dispatcher != targetDispatcher)
                    throw new InvalidOperationException("Desktop monitoring must use one UI dispatcher.");
                references++;
                return;
            }

            dispatcher = targetDispatcher;
            references = 1;
            initialized = false;
            coalescingTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
            coalescingTimer.Tick += OnCoalescingTick;
            fullscreenRecoveryTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
            fullscreenRecoveryTimer.Interval = TimeSpan.FromSeconds(2);
            fullscreenRecoveryTimer.Tick += OnFullscreenRecoveryTick;
            foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground,
                IntPtr.Zero, callback, 0, 0, WinEventOutOfContextSkipOwnProcess);
            CheckNow();
        }

        internal static void Release()
        {
            Dispatcher activeDispatcher = dispatcher;
            if (activeDispatcher == null) return;
            activeDispatcher.VerifyAccess();
            if (references == 0 || --references > 0) return;

            if (coalescingTimer != null)
            {
                coalescingTimer.Stop();
                coalescingTimer.Tick -= OnCoalescingTick;
                coalescingTimer = null;
            }
            if (fullscreenRecoveryTimer != null)
            {
                fullscreenRecoveryTimer.Stop();
                fullscreenRecoveryTimer.Tick -= OnFullscreenRecoveryTick;
                fullscreenRecoveryTimer = null;
            }
            RemoveHook(ref foregroundHook);
            RemoveHook(ref shellReorderHook);
            RemoveHook(ref foregroundLocationHook);
            lock (gate)
            {
                dispatcher = null;
                checkQueued = false;
                foregroundCheckQueued = false;
                queueVersion++;
                pendingShellReordered = false;
            }
            observedForeground = IntPtr.Zero;
            shellProcess = 0;
            locationProcess = 0;
            fullscreen = false;
            desktopForeground = false;
            initialized = false;
            lastCheckTimestamp = 0;
        }

        internal static void RequestCheck()
        {
            QueueCheck(false);
        }

        private static void OnWinEvent(IntPtr hook, uint eventType, IntPtr changedWindow,
            int objectId, int childId, uint eventThread, uint eventTime)
        {
            if (eventType == EventSystemForeground)
            {
                // Enter fullscreen before normal-priority UI work can restore
                // a desktop component. Only genuine foreground transitions
                // receive this leading edge; resize/reorder bursts stay bounded.
                QueueCheck(false, true);
                return;
            }
            if (eventType == EventObjectLocationChange)
            {
                if (changedWindow == observedForeground && objectId == 0 && childId == 0)
                    QueueCheck(false);
                return;
            }
            if (eventType == EventObjectReorder && objectId == 0 && childId == 0 &&
                IsDesktopShellWindow(changedWindow)) QueueCheck(true);
        }

        private static void QueueCheck(bool shellReordered)
        {
            QueueCheck(shellReordered, false);
        }

        private static void QueueCheck(bool shellReordered, bool foregroundChange)
        {
            lock (gate)
            {
                if (dispatcher == null || dispatcher.HasShutdownStarted || references == 0) return;
                pendingShellReordered |= shellReordered;
                if (foregroundChange)
                {
                    if (foregroundCheckQueued) return;
                    foregroundCheckQueued = true;
                    checkQueued = true;
                    int foregroundVersion = ++queueVersion;
                    try
                    {
                        dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(delegate
                        {
                            if (foregroundVersion != queueVersion || references == 0 || dispatcher == null) return;
                            if (coalescingTimer != null) coalescingTimer.Stop();
                            CheckNow();
                        }));
                    }
                    catch (InvalidOperationException) { foregroundCheckQueued = checkQueued = false; }
                    return;
                }
                if (checkQueued) return;
                checkQueued = true;
                int version = ++queueVersion;
                try
                {
                    dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                    {
                        if (version == queueVersion) ScheduleCheck();
                    }));
                }
                catch (InvalidOperationException) { checkQueued = false; }
            }
        }

        private static void ScheduleCheck()
        {
            if (references == 0 || dispatcher == null || coalescingTimer == null) return;
            double elapsed = (Stopwatch.GetTimestamp() - lastCheckTimestamp) * 1000.0 / Stopwatch.Frequency;
            if (!initialized || elapsed >= CheckIntervalMilliseconds)
            {
                CheckNow();
                return;
            }
            coalescingTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, CheckIntervalMilliseconds - elapsed));
            coalescingVersion = queueVersion;
            coalescingTimer.Start();
        }

        private static void OnCoalescingTick(object sender, EventArgs e)
        {
            if (coalescingTimer == null) return;
            coalescingTimer.Stop();
            if (!checkQueued || coalescingVersion != queueVersion) return;
            CheckNow();
        }

        private static void OnFullscreenRecoveryTick(object sender, EventArgs e)
        {
            // This fallback exists only while fullscreen. At the desktop there
            // is no periodic foreground/window scan or steady-state timer.
            RequestCheck();
        }

        private static void CheckNow()
        {
            if (references == 0 || dispatcher == null) return;
            bool shellReordered;
            lock (gate)
            {
                checkQueued = false;
                foregroundCheckQueued = false;
                queueVersion++;
                shellReordered = pendingShellReordered;
                pendingShellReordered = false;
            }
            lastCheckTimestamp = Stopwatch.GetTimestamp();
            UpdateShellHook();
            IntPtr foreground = GetForegroundWindow();
            UpdateLocationHook(foreground);
            bool nextDesktop = IsDesktopForegroundWindow(foreground);
            bool nextFullscreen = IsFullscreenWindow(foreground, nextDesktop);
            bool foregroundChanged = foreground != observedForeground;
            bool stateChanged = !initialized || fullscreen != nextFullscreen || desktopForeground != nextDesktop;
            bool desktopTransition = (!initialized && nextDesktop) ||
                (initialized && (desktopForeground != nextDesktop ||
                (foregroundChanged && (desktopForeground || nextDesktop))));
            observedForeground = foreground;
            fullscreen = nextFullscreen;
            desktopForeground = nextDesktop;
            initialized = true;

            if (fullscreen && !fullscreenRecoveryTimer.IsEnabled) fullscreenRecoveryTimer.Start();
            else if (!fullscreen && fullscreenRecoveryTimer.IsEnabled) fullscreenRecoveryTimer.Stop();

            if (!stateChanged && !foregroundChanged && !shellReordered) return;
            EventHandler<DesktopEnvironmentChangedEventArgs> handler = Changed;
            if (handler != null)
                handler(null, new DesktopEnvironmentChangedEventArgs(foreground, fullscreen,
                    desktopTransition, shellReordered));
        }

        private static void UpdateShellHook()
        {
            uint process;
            GetWindowThreadProcessId(GetShellWindow(), out process);
            if (process == shellProcess && (process == 0 || shellReorderHook != IntPtr.Zero)) return;
            RemoveHook(ref shellReorderHook);
            shellProcess = process;
            if (process != 0)
                shellReorderHook = SetWinEventHook(EventObjectReorder, EventObjectReorder,
                    IntPtr.Zero, callback, process, 0, WinEventOutOfContextSkipOwnProcess);
        }

        private static void UpdateLocationHook(IntPtr foreground)
        {
            uint process;
            GetWindowThreadProcessId(foreground, out process);
            if (process == ownProcess) process = 0;
            if (process == locationProcess && (process == 0 || foregroundLocationHook != IntPtr.Zero)) return;
            RemoveHook(ref foregroundLocationHook);
            locationProcess = process;
            if (process != 0)
                foregroundLocationHook = SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange,
                    IntPtr.Zero, callback, process, 0, WinEventOutOfContextSkipOwnProcess);
        }

        private static bool IsDesktopForegroundWindow(IntPtr window)
        {
            if (window == IntPtr.Zero) return false;
            if (GetProp(window, DesktopComponentProperty) != IntPtr.Zero) return true;
            string name = WindowClass(window);
            return window == GetDesktopWindow() || window == GetShellWindow() ||
                name == "Progman" || name == "WorkerW" || name == "Shell_Desktop" ||
                name == "ShellDesktop" || name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd";
        }

        private static bool IsDesktopShellWindow(IntPtr window)
        {
            if (window == IntPtr.Zero) return false;
            if (window == GetDesktopWindow() || window == GetShellWindow()) return true;
            string name = WindowClass(window);
            return name == "Progman" || name == "WorkerW" || name == "Shell_Desktop" || name == "ShellDesktop";
        }

        private static bool IsFullscreenWindow(IntPtr window, bool desktop)
        {
            if (desktop) return false;
            if (window == IntPtr.Zero || !IsWindow(window)) return NotificationStateIndicatesFullscreen();
            uint process;
            GetWindowThreadProcessId(window, out process);
            if (process == ownProcess || !IsWindowVisible(window) || IsIconic(window) || IsCloaked(window)) return false;

            NativeRect bounds;
            bool haveBounds = TryGetFrameBounds(window, out bounds);
            IntPtr monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
            MonitorInfo info = new MonitorInfo();
            info.Size = Marshal.SizeOf(typeof(MonitorInfo));
            if (haveBounds && monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            {
                // A valid ordinary foreground window decides this directly.
                // Ignore possibly stale notification-state fullscreen flags
                // when Alt+Tab has already moved to a smaller normal window.
                return CoversMonitor(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom,
                    info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
            }
            return NotificationStateIndicatesFullscreen();
        }

        // Native bounds and monitor rectangles share desktop pixel coordinates,
        // including negative coordinates on monitors left/above the primary.
        // Compare the entire monitor, rather than its taskbar-excluding work area.
        internal static bool CoversMonitor(int left, int top, int right, int bottom,
            int monitorLeft, int monitorTop, int monitorRight, int monitorBottom)
        {
            if (right <= left || bottom <= top || monitorRight <= monitorLeft || monitorBottom <= monitorTop)
                return false;
            return (long)left <= (long)monitorLeft + 1 && (long)top <= (long)monitorTop + 1 &&
                (long)right >= (long)monitorRight - 1 && (long)bottom >= (long)monitorBottom - 1;
        }

        private static bool TryGetFrameBounds(IntPtr window, out NativeRect bounds)
        {
            try
            {
                if (DwmGetWindowAttribute(window, DwmExtendedFrameBounds, out bounds,
                    Marshal.SizeOf(typeof(NativeRect))) == 0 && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top)
                    return true;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return GetWindowRect(window, out bounds);
        }

        private static bool IsCloaked(IntPtr window)
        {
            try
            {
                int cloaked;
                return DwmGetWindowAttribute(window, DwmCloaked, out cloaked, sizeof(int)) == 0 && cloaked != 0;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        private static bool NotificationStateIndicatesFullscreen()
        {
            try
            {
                int state;
                return SHQueryUserNotificationState(out state) == 0 && state == RunningD3DFullscreen;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        private static string WindowClass(IntPtr window)
        {
            StringBuilder name = new StringBuilder(64);
            GetClassName(window, name, name.Capacity);
            return name.ToString();
        }

        private static void RemoveHook(ref IntPtr hook)
        {
            if (hook == IntPtr.Zero) return;
            UnhookWinEvent(hook);
            hook = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { internal int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            internal int Size;
            internal NativeRect Monitor;
            internal NativeRect WorkArea;
            internal uint Flags;
        }

        private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr window,
            int objectId, int childId, uint eventThread, uint eventTime);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetProp(IntPtr window, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWinEventHook(uint eventMinimum, uint eventMaximum,
            IntPtr module, WinEventCallback eventCallback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect value, int size);
        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int state);
    }
}

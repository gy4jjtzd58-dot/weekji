using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ChineseWeeklyPlanner
{
    // One desktop window contains the weekly plan and quick notes; the session
    // also owns modeless slot editors, settings, and a single shared tray.
    internal sealed class PlannerApplication : Application
    {
        private const int ShowWeekCommand = 1;
        private const int ShowNotesCommand = 2;
        private const int HideAllCommand = 3;
        private const int QuitAllCommand = 4;
        private const int QuitWeekCommand = 5;
        private const int QuitNotesCommand = 6;
        private const int DiagnoseWeekCommand = 7;
        private const int DiagnoseNotesCommand = 8;
        private const int DiagnoseSessionCommand = 9;
        private const int ShowAppearanceCommand = 10;
        private const int ToggleVisibilityCommand = 11;
        private const int SleepCommand = 12;
        private const int WakeCommand = 13;
        private const int ToggleHotkeyId = 0x4D01;
        private const int WmHotkey = 0x0312;
        private const uint CtrlDModifiers = 0x4002; // CONTROL | NOREPEAT.
        private const uint DKey = 0x44;
        private static PlannerApplication activeSession;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string text);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        private static readonly uint CommandMessage = RegisterWindowMessage("ChineseWeeklyPlanner.SessionCommand.v1");
        internal static bool HasSharedTray { get; private set; }

        private readonly string dataRoot;
        private readonly string endpointTitle;
        private readonly bool initiallyShowWeek;
        private readonly bool initiallyShowAppearance;
        private HwndSource endpoint;
        private System.Windows.Forms.NotifyIcon trayIcon;
        private System.Windows.Forms.ContextMenuStrip trayMenu;
        private System.Drawing.Icon trayDrawingIcon;
        private GlassWeekWindow weekWindow;
        private AppearanceSettings appearance;
        private AppearanceSettingsWindow appearanceWindow;
        private GlassWeekWindow shortcutWeek;
        private AppearanceSettingsWindow shortcutAppearance;
        private bool hotkeyRegistered;
        private int hotkeyError;
        private bool exiting;
        private bool disposed;
        private bool autoGameMode = true;
        private bool manualSleep;
        private bool backgroundSuspended;
        private bool resumeWeek;
        private bool resumeAppearance;
        private bool releasingSleepingWindows;
        private bool monitorAcquired;
        private bool dormantShortcutWeek;
        private bool shortcutEnabled = true;
        private bool editorInteropEnabled;
        private int shortcutVariant;
        private DispatcherTimer releaseTimer;
        private readonly Dictionary<string, EditorForm> editors = new Dictionary<string, EditorForm>(StringComparer.OrdinalIgnoreCase);
        private readonly List<EditorForm> sleepingEditors = new List<EditorForm>();
        private readonly List<EditorForm> shortcutEditors = new List<EditorForm>();
        private System.Windows.Forms.ToolStripMenuItem gameModeItem;
        private System.Windows.Forms.ToolStripMenuItem manualSleepItem;
        private System.Windows.Forms.ToolStripMenuItem runtimeStatus;
        private System.Windows.Forms.ToolStripMenuItem[] shortcutItems;
        private System.Windows.Forms.ToolStripMenuItem shortcutToggleItem;
        private string deferredError;

        private PlannerApplication(string dataRoot, string endpointTitle, bool initiallyShowWeek, bool initiallyShowAppearance)
        {
            this.dataRoot = dataRoot;
            this.endpointTitle = endpointTitle;
            this.initiallyShowWeek = initiallyShowWeek;
            this.initiallyShowAppearance = initiallyShowAppearance;
            appearance = AppearanceSettings.Load(dataRoot);
            LoadRuntimeSettings();
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        internal static int RunSession(string dataRoot, bool showWeek, bool showAppearance = false)
        {
            string key = SessionKey(dataRoot);
            string title = "ChineseWeeklyPlanner.Session." + key;
            using (Mutex sessionMutex = new Mutex(false, "Local\\ChineseWeeklyPlanner.Session." + key))
            {
                bool ownsMutex = TryAcquire(sessionMutex);
                // The first process may still be constructing its native IPC
                // endpoint. Wait briefly instead of opening a second session.
                for (int attempt = 0; !ownsMutex && attempt < 60; attempt++)
                {
                    IntPtr existing = Program.FindOwnWindow(title);
                    if (existing != IntPtr.Zero && PostMessage(existing, CommandMessage,
                        new IntPtr(showAppearance ? ShowAppearanceCommand : showWeek ? ShowWeekCommand : ShowNotesCommand), IntPtr.Zero)) return 0;
                    ownsMutex = TryAcquire(sessionMutex);
                    if (!ownsMutex) Thread.Sleep(100);
                }
                if (!ownsMutex) throw new IOException("程序正在启动或退出，请稍后再打开。");

                PlannerApplication application = null;
                try
                {
                    HasSharedTray = true;
                    application = new PlannerApplication(dataRoot, title, showWeek, showAppearance);
                    return application.Run();
                }
                finally
                {
                    if (application != null) application.DisposeSession();
                    HasSharedTray = false;
                    sessionMutex.ReleaseMutex();
                }
            }
        }

        private static bool TryAcquire(Mutex mutex)
        {
            try { return mutex.WaitOne(0, false); }
            catch (AbandonedMutexException) { return true; }
        }

        private static string SessionKey(string root)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(fullRoot.ToUpperInvariant()))).Replace("-", "");
        }

        internal static bool IsControlArgument(string argument)
        {
            return argument == "--quit-all" || argument == "--quit-week" || argument == "--quit-notes" || argument == "--hide-all" || argument == "--toggle-all" || argument == "--sleep" || argument == "--wake" ||
                argument == "--diagnose-week" || argument == "--diagnose-notes" || argument == "--diagnose-session";
        }

        internal static bool TrySendControl(string dataRoot, string argument)
        {
            IntPtr target = Program.FindOwnWindow("ChineseWeeklyPlanner.Session." + SessionKey(dataRoot));
            if (target == IntPtr.Zero) return false;
            int command = argument == "--hide-all" ? HideAllCommand : argument == "--quit-all" ? QuitAllCommand :
                argument == "--quit-week" ? QuitWeekCommand : argument == "--quit-notes" ? QuitNotesCommand :
                argument == "--toggle-all" ? ToggleVisibilityCommand : argument == "--sleep" ? SleepCommand : argument == "--wake" ? WakeCommand : argument == "--diagnose-week" ? DiagnoseWeekCommand : argument == "--diagnose-notes" ? DiagnoseNotesCommand :
                DiagnoseSessionCommand;
            if (!PostMessage(target, CommandMessage, new IntPtr(command), IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法联系正在运行的程序。");
            return true;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (CommandMessage == 0) throw new Win32Exception("无法创建程序会话消息。");
            HwndSourceParameters parameters = new HwndSourceParameters(endpointTitle);
            // A hidden top-level endpoint remains discoverable while both
            // desktop windows are hidden or parented to Explorer's desktop.
            parameters.WindowStyle = unchecked((int)0x80000000); // WS_POPUP, no WS_VISIBLE.
            parameters.ExtendedWindowStyle = 0x00000080; // WS_EX_TOOLWINDOW.
            parameters.Width = 0;
            parameters.Height = 0;
            endpoint = new HwndSource(parameters);
            endpoint.AddHook(EndpointMessage);
            activeSession = this;
            CreateTrayIcon();
            DesktopEnvironmentMonitor.Changed += OnEnvironmentChanged;
            DesktopEnvironmentMonitor.Acquire(Dispatcher);
            monitorAcquired = true;
            UpdateBackgroundMode();
            RegisterVisibilityHotkey();
            if (initiallyShowAppearance) ShowAppearance();
            else ShowWeek();
        }

        private IntPtr EndpointMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == WmHotkey && wParam.ToInt32() == ToggleHotkeyId && hotkeyRegistered &&
                (lParam.ToInt64() & 0xF) == (CurrentHotkeyModifiers & 0xF) && ((lParam.ToInt64() >> 16) & 0xFFFF) == DKey)
            {
                handled = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(delegate
                {
                    if (!disposed) RunSafely(ToggleVisibilityFromShortcut);
                }));
                return IntPtr.Zero;
            }
            if ((uint)message != CommandMessage) return IntPtr.Zero;
            handled = true;
            int command = wParam.ToInt32();
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(delegate
            {
                if (!disposed) RunSafely(delegate { ExecuteCommand(command); });
            }));
            return IntPtr.Zero;
        }

        private void ExecuteCommand(int command)
        {
            switch (command)
            {
                case ShowWeekCommand: ShowWeek(); break;
                case ShowNotesCommand: ShowNotes(); break;
                case HideAllCommand: HideAll(); break;
                case QuitAllCommand: TryExit(); break;
                case QuitWeekCommand: resumeWeek = false; shortcutWeek = null; dormantShortcutWeek = false; if (weekWindow != null) weekWindow.Close(); break;
                case QuitNotesCommand: resumeWeek = false; shortcutWeek = null; dormantShortcutWeek = false; if (weekWindow != null) weekWindow.Close(); break;
                case DiagnoseWeekCommand: RequestWindowDiagnostics(weekWindow); break;
                case DiagnoseNotesCommand: RequestWindowDiagnostics(weekWindow); break;
                case DiagnoseSessionCommand: SaveRuntimeDiagnostics(); break;
                case ShowAppearanceCommand: ShowAppearance(); break;
                case ToggleVisibilityCommand: ToggleVisibilityFromShortcut(); break;
                case SleepCommand: manualSleep = true; UpdateBackgroundMode(); break;
                case WakeCommand: manualSleep = false; UpdateBackgroundMode(); break;
            }
        }

        private void CreateTrayIcon()
        {
            trayMenu = new System.Windows.Forms.ContextMenuStrip();
            AddTrayCommand("显示 " + AppBrand.Name, ShowWeek);
            AddTrayCommand("外观设置", ShowAppearance);
            shortcutToggleItem = AddTrayCommand("隐藏 / 恢复（Ctrl+D）", ToggleVisibilityFromShortcut);
            gameModeItem = AddTrayCommand("全屏时自动休眠", delegate
            {
                autoGameMode = !autoGameMode;
                SaveRuntimeSettings();
                UpdateBackgroundMode();
            });
            manualSleepItem = AddTrayCommand("手动休眠", delegate
            {
                manualSleep = !manualSleep;
                UpdateBackgroundMode();
            });
            System.Windows.Forms.ToolStripMenuItem shortcutMenu = new System.Windows.Forms.ToolStripMenuItem("显隐快捷键");
            shortcutItems = new System.Windows.Forms.ToolStripMenuItem[4];
            string[] shortcutLabels = { "Ctrl+D", "Ctrl+Alt+D", "Ctrl+Shift+D", "关闭快捷键" };
            for (int index = 0; index < shortcutItems.Length; index++)
            {
                int selected = index;
                shortcutItems[index] = new System.Windows.Forms.ToolStripMenuItem(shortcutLabels[index]);
                shortcutItems[index].Click += delegate { RunSafely(delegate { SetShortcut(selected); }); };
                shortcutMenu.DropDownItems.Add(shortcutItems[index]);
            }
            trayMenu.Items.Add(shortcutMenu);
            runtimeStatus = new System.Windows.Forms.ToolStripMenuItem { Enabled = false, Visible = false };
            trayMenu.Items.Add(runtimeStatus);
            trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            AddTrayCommand("全部收起", HideAll);
            AddTrayCommand("退出", TryExit);
            trayIcon = new System.Windows.Forms.NotifyIcon();
            trayDrawingIcon = AppBrand.CreateTrayIcon();
            trayIcon.Icon = trayDrawingIcon;
            trayIcon.Text = AppBrand.Name;
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.DoubleClick += delegate { RunSafely(ShowWeek); };
            trayIcon.Visible = true;
            UpdateRuntimeStatus();
        }

        private void RegisterVisibilityHotkey()
        {
            if (hotkeyRegistered || endpoint == null || backgroundSuspended || !shortcutEnabled) { UpdateRuntimeStatus(); return; }
            hotkeyRegistered = RegisterHotKey(endpoint.Handle, ToggleHotkeyId, CurrentHotkeyModifiers, DKey);
            hotkeyError = hotkeyRegistered ? 0 : Marshal.GetLastWin32Error();
            UpdateRuntimeStatus();
        }

        private uint CurrentHotkeyModifiers { get { return CtrlDModifiers | (shortcutVariant == 1 ? 1u : shortcutVariant == 2 ? 4u : 0u); } }
        private string ShortcutLabel { get { return shortcutVariant == 1 ? "Ctrl+Alt+D" : shortcutVariant == 2 ? "Ctrl+Shift+D" : "Ctrl+D"; } }

        private void ReleaseVisibilityHotkey()
        {
            if (hotkeyRegistered && endpoint != null) UnregisterHotKey(endpoint.Handle, ToggleHotkeyId);
            hotkeyRegistered = false;
        }

        private void SetShortcut(int selected)
        {
            ReleaseVisibilityHotkey();
            shortcutEnabled = selected != 3;
            if (shortcutEnabled) shortcutVariant = selected;
            hotkeyError = 0;
            SaveRuntimeSettings();
            RegisterVisibilityHotkey();
        }

        private void UpdateRuntimeStatus()
        {
            if (runtimeStatus == null) return;
            gameModeItem.Checked = autoGameMode;
            manualSleepItem.Checked = manualSleep;
            for (int index = 0; index < shortcutItems.Length; index++) shortcutItems[index].Checked = index == (shortcutEnabled ? shortcutVariant : 3);
            shortcutToggleItem.Text = "隐藏 / 恢复" + (shortcutEnabled ? "（" + ShortcutLabel + "）" : String.Empty);
            string state = backgroundSuspended ? (manualSleep ? "手动休眠中 · 快捷键已让出" : "全屏休眠中 · 快捷键已让出") :
                hotkeyError != 0 && shortcutEnabled ? ShortcutLabel + " 未启用，请用托盘收起" : deferredError;
            runtimeStatus.Text = state ?? String.Empty;
            runtimeStatus.Visible = !String.IsNullOrEmpty(state);
        }

        private void LoadRuntimeSettings()
        {
            string path = Path.Combine(dataRoot, "runtime.ini");
            try
            {
                if (!File.Exists(path)) return;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string[] parts = line.Split(new[] { '=' }, 2);
                    if (parts.Length != 2) continue;
                    string key = parts[0].Trim(), value = parts[1].Trim();
                    bool flag; int variant;
                    if (key == "AutoGameMode" && Boolean.TryParse(value, out flag)) autoGameMode = flag;
                    else if (key == "ShortcutEnabled" && Boolean.TryParse(value, out flag)) shortcutEnabled = flag;
                    else if (key == "ShortcutVariant" && Int32.TryParse(value, out variant) && variant >= 0 && variant <= 2) shortcutVariant = variant;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void SaveRuntimeSettings()
        {
            Program.WriteAtomic(Path.Combine(dataRoot, "runtime.ini"), "AutoGameMode=" + autoGameMode +
                "\r\nShortcutEnabled=" + shortcutEnabled + "\r\nShortcutVariant=" + shortcutVariant.ToString(CultureInfo.InvariantCulture) + "\r\n");
        }

        private void ClearShortcutSnapshot()
        {
            shortcutWeek = null;
            shortcutAppearance = null;
            dormantShortcutWeek = false;
            shortcutEditors.Clear();
        }

        private void ToggleVisibilityFromShortcut()
        {
            if (exiting || disposed || backgroundSuspended) return;
            bool weekVisible = weekWindow != null && weekWindow.IsVisible;
            bool appearanceVisible = appearanceWindow != null && appearanceWindow.IsVisible;
            bool editorVisible = false;
            foreach (EditorForm editor in editors.Values) if (editor.Visible) editorVisible = true;
            if (weekVisible || appearanceVisible || editorVisible)
            {
                // Save first: a failure must not leave part of the interface hidden.
                if (weekVisible && !weekWindow.PrepareForShutdown()) return;
                ClearShortcutSnapshot();
                shortcutWeek = weekVisible ? weekWindow : null;
                shortcutAppearance = appearanceVisible ? appearanceWindow : null;
                if (weekVisible) weekWindow.DesktopHost.HideToTray();
                // A second save may fail between preflight and HideToTray.
                if (weekVisible && weekWindow.IsVisible)
                {
                    RestoreShortcutSnapshot();
                    return;
                }
                if (appearanceVisible) appearanceWindow.Hide();
                foreach (EditorForm editor in editors.Values)
                    if (editor.Visible) { shortcutEditors.Add(editor); editor.Hide(); }
            }
            else RestoreShortcutSnapshot();
        }

        private void RestoreShortcutSnapshot()
        {
            GlassWeekWindow oldWeek = shortcutWeek;
            AppearanceSettingsWindow oldAppearance = shortcutAppearance;
            EditorForm[] oldEditors = shortcutEditors.ToArray();
            bool rebuildWeek = dormantShortcutWeek;
            ClearShortcutSnapshot();
            if (oldWeek != null && Object.ReferenceEquals(oldWeek, weekWindow)) oldWeek.DesktopHost.ShowFromTray();
            else if (rebuildWeek && weekWindow == null) ShowWeek();
            if (oldAppearance != null && Object.ReferenceEquals(oldAppearance, appearanceWindow))
            {
                bool previouslyActivated = oldAppearance.ShowActivated;
                try
                {
                    oldAppearance.ShowActivated = false;
                    oldAppearance.WindowState = WindowState.Normal;
                    oldAppearance.Show();
                }
                finally { oldAppearance.ShowActivated = previouslyActivated; }
            }
            foreach (EditorForm editor in oldEditors) if (!editor.IsDisposed) editor.ShowWithoutFocus();
        }

        private System.Windows.Forms.ToolStripMenuItem AddTrayCommand(string label, Action action)
        {
            System.Windows.Forms.ToolStripMenuItem item = new System.Windows.Forms.ToolStripMenuItem(label);
            item.Click += delegate { RunSafely(action); };
            trayMenu.Items.Add(item);
            return item;
        }

        private void RunSafely(Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                if (backgroundSuspended)
                {
                    deferredError = "待处理：" + ex.Message;
                    UpdateRuntimeStatus();
                    return;
                }
                MessageBox.Show("暂时无法完成操作。\r\n" + ex.Message, AppBrand.Name,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowWeek()
        {
            if (exiting || disposed) return;
            if (backgroundSuspended) { resumeWeek = true; return; }
            ClearShortcutSnapshot();
            if (weekWindow == null)
            {
                GlassWeekWindow created = GlassWeekWindow.Create(dataRoot, AppBrand.Name);
                created.Icon = AppBrand.WindowIcon;
                created.Closed += delegate
                {
                    if (Object.ReferenceEquals(weekWindow, created)) weekWindow = null;
                    if (Object.ReferenceEquals(MainWindow, created)) MainWindow = null;
                    ShutdownIfEmpty();
                };
                weekWindow = created;
            }
            weekWindow.ApplyAppearance(appearance.Week);
            weekWindow.ApplyNotesAppearance(appearance.Notes);
            weekWindow.DesktopHost.ShowFromTray();
        }

        private void ShowNotes()
        {
            ShowWeek();
            if (!backgroundSuspended && weekWindow != null) weekWindow.FocusQuickNotes();
        }

        private void ApplyAppearance(AppearanceSettings settings)
        {
            if (disposed) return;
            AppearanceSettings value = settings.Clone();
            value.Week.Normalize();
            value.Notes.Normalize();
            appearance = value;
            if (weekWindow != null) { weekWindow.ApplyAppearance(value.Week); weekWindow.ApplyNotesAppearance(value.Notes); }
        }

        private void SaveAppearance(AppearanceSettings settings)
        {
            settings.Save(dataRoot);
            ApplyAppearance(settings);
        }

        private void ShowAppearance()
        {
            if (exiting || disposed) return;
            if (backgroundSuspended) { resumeAppearance = true; return; }
            ClearShortcutSnapshot();
            if (appearanceWindow == null)
            {
                AppearanceSettingsWindow created = new AppearanceSettingsWindow(appearance.Clone(), ApplyAppearance, SaveAppearance);
                created.Icon = AppBrand.WindowIcon;
                created.Closed += delegate
                {
                    if (Object.ReferenceEquals(appearanceWindow, created)) appearanceWindow = null;
                    if (Object.ReferenceEquals(MainWindow, created)) MainWindow = null;
                    ShutdownIfEmpty();
                };
                appearanceWindow = created;
            }
            appearanceWindow.WindowState = WindowState.Normal;
            appearanceWindow.Show();
            appearanceWindow.Activate();
        }

        private void HideAll()
        {
            ClearShortcutSnapshot();
            resumeWeek = false;
            resumeAppearance = false;
            if (weekWindow != null) weekWindow.DesktopHost.HideToTray();
            if (appearanceWindow != null) appearanceWindow.Hide();
            foreach (EditorForm editor in editors.Values) editor.Hide();
            sleepingEditors.Clear();
        }

        internal static bool TryOpenSlot(DateTime monday, int day, int period)
        {
            PlannerApplication session = activeSession;
            if (session == null || session.disposed) return false;
            session.RunSafely(delegate { session.OpenSlot(monday, day, period); });
            return true;
        }

        private void OpenSlot(DateTime monday, int day, int period)
        {
            if (backgroundSuspended) return;
            if (!editorInteropEnabled)
            {
                System.Windows.Forms.Integration.WindowsFormsHost.EnableWindowsFormsInterop();
                editorInteropEnabled = true;
            }
            string path = GlassWeekWindow.GetCellPath(dataRoot, monday, day, period);
            EditorForm editor;
            if (editors.TryGetValue(path, out editor) && !editor.IsDisposed)
            {
                editor.Show(); editor.Activate(); return;
            }
            string[] days = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
            string[] periods = { "上午", "下午", "晚上", "备注" };
            editor = new EditorForm(path, days[day] + " · " + periods[period] + " | " + monday.AddDays(day).ToString("M月d日", CultureInfo.GetCultureInfo("zh-CN")), false);
            EditorForm created = editor;
            editors[path] = editor;
            editor.FormClosed += delegate
            {
                editors.Remove(path);
                shortcutEditors.Remove(created);
                sleepingEditors.Remove(created);
                created.Dispose();
                ShutdownIfEmpty();
            };
            editor.Show();
            editor.Activate();
        }

        private void OnEnvironmentChanged(object sender, DesktopEnvironmentChangedEventArgs args)
        {
            if (!disposed) RunSafely(UpdateBackgroundMode);
        }

        private void UpdateBackgroundMode()
        {
            bool shouldSuspend = manualSleep || (autoGameMode && DesktopEnvironmentMonitor.IsFullscreen);
            if (shouldSuspend == backgroundSuspended) { UpdateRuntimeStatus(); return; }
            backgroundSuspended = shouldSuspend;
            if (shouldSuspend)
            {
                resumeWeek = weekWindow != null && weekWindow.IsVisible;
                resumeAppearance = appearanceWindow != null && appearanceWindow.IsVisible;
                ReleaseVisibilityHotkey();
                if (weekWindow != null)
                {
                    weekWindow.TrySaveNotes(false);
                    weekWindow.SetBackgroundSuspended(true);
                    weekWindow.DesktopHost.SetBackgroundSuspended(true);
                }
                if (resumeAppearance) appearanceWindow.Hide();
                sleepingEditors.Clear();
                foreach (EditorForm editor in editors.Values)
                    if (editor.Visible) { sleepingEditors.Add(editor); editor.Hide(); }
                if (releaseTimer == null)
                {
                    releaseTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher);
                    releaseTimer.Interval = TimeSpan.FromSeconds(30);
                    releaseTimer.Tick += delegate { ReleaseSleepingWindows(); };
                }
                releaseTimer.Start();
                try { using (Process process = Process.GetCurrentProcess()) process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Win32Exception) { }
            }
            else
            {
                if (releaseTimer != null) releaseTimer.Stop();
                try { using (Process process = Process.GetCurrentProcess()) process.PriorityClass = ProcessPriorityClass.Normal; } catch (Win32Exception) { }
                bool showWeek = resumeWeek, showAppearance = resumeAppearance;
                resumeWeek = resumeAppearance = false;
                if (weekWindow != null)
                {
                    weekWindow.SetBackgroundSuspended(false);
                    weekWindow.DesktopHost.SetBackgroundSuspended(false);
                    if (showWeek && !weekWindow.IsVisible) weekWindow.DesktopHost.ShowFromTray();
                }
                else if (showWeek) ShowWeek();
                if (showAppearance)
                {
                    // Explicit settings can be deferred when launched during a game.
                    if (appearanceWindow == null)
                    {
                        appearanceWindow = new AppearanceSettingsWindow(appearance.Clone(), ApplyAppearance, SaveAppearance);
                        appearanceWindow.Icon = AppBrand.WindowIcon;
                        AppearanceSettingsWindow created = appearanceWindow;
                        created.Closed += delegate { if (Object.ReferenceEquals(appearanceWindow, created)) appearanceWindow = null; if (Object.ReferenceEquals(MainWindow, created)) MainWindow = null; ShutdownIfEmpty(); };
                    }
                    bool activation = appearanceWindow.ShowActivated;
                    try { appearanceWindow.ShowActivated = false; appearanceWindow.Show(); }
                    finally { appearanceWindow.ShowActivated = activation; }
                }
                foreach (EditorForm editor in sleepingEditors.ToArray()) if (!editor.IsDisposed) editor.ShowWithoutFocus();
                sleepingEditors.Clear();
                RegisterVisibilityHotkey();
            }
            UpdateRuntimeStatus();
        }

        private void ReleaseSleepingWindows()
        {
            releaseTimer.Stop();
            if (!backgroundSuspended || disposed) return;
            // Preserve any in-memory input and the Ctrl+D restoration snapshot.
            releasingSleepingWindows = true;
            try
            {
                if (weekWindow != null)
                {
                    if (weekWindow.TrySaveNotes(false))
                    {
                        if (Object.ReferenceEquals(shortcutWeek, weekWindow)) { dormantShortcutWeek = true; shortcutWeek = null; }
                        weekWindow.Close();
                    }
                }
            }
            finally { releasingSleepingWindows = false; }
        }

        private bool PrepareForShutdown()
        {
            // Retain the combined window if notes cannot be saved.
            if (weekWindow != null && !weekWindow.TrySaveNotes(!backgroundSuspended)) return false;
            if (weekWindow != null && !weekWindow.PrepareForShutdown()) return false;
            return true;
        }

        private void TryExit()
        {
            if (exiting || disposed || !PrepareForShutdown()) return;
            exiting = true;
            try
            {
                if (appearanceWindow != null) appearanceWindow.Close();
                if (weekWindow != null) weekWindow.Close();
                if (weekWindow != null) return;
                foreach (EditorForm editor in new List<EditorForm>(editors.Values)) editor.Close();
                Shutdown(0);
            }
            finally { exiting = false; }
        }

        private void ShutdownIfEmpty()
        {
            if (!exiting && !backgroundSuspended && !releasingSleepingWindows && weekWindow == null && appearanceWindow == null && editors.Count == 0) Shutdown(0);
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            try { if (!PrepareForShutdown()) e.Cancel = true; }
            catch (Exception ex)
            {
                e.Cancel = true;
                if (!backgroundSuspended) MessageBox.Show("退出前保存失败，请先检查并保存随心记。\r\n" + ex.Message,
                    AppBrand.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            base.OnSessionEnding(e);
        }

        private static void RequestWindowDiagnostics(Window window)
        {
            if (window == null) return;
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero && !PostMessage(handle, DesktopGlassHost.DiagnosticMessage, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private void SaveRuntimeDiagnostics()
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("IsSession=True");
            report.AppendLine("AppName=" + AppBrand.Name);
            report.AppendLine("UnifiedWindow=True");
            report.AppendLine("OwnPid=" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
            report.AppendLine("WeekWindowExists=" + (weekWindow != null));
            report.AppendLine("NotesWindowExists=False");
            report.AppendLine("NotesPanelVisible=" + (weekWindow != null && weekWindow.IsQuickNotesVisible));
            report.AppendLine("WeekWindowVisible=" + (weekWindow != null && weekWindow.IsVisible));
            report.AppendLine("NotesWindowVisible=False");
            report.AppendLine("TrayIconVisible=" + (trayIcon != null && trayIcon.Visible));
            report.AppendLine("TrayIconCount=" + (trayIcon != null && trayIcon.Visible ? "1" : "0"));
            report.AppendLine("AppearanceWindowVisible=" + (appearanceWindow != null && appearanceWindow.IsVisible));
            report.AppendLine("BackgroundSuspended=" + backgroundSuspended);
            report.AppendLine("AutoGameMode=" + autoGameMode);
            report.AppendLine("ManualSleep=" + manualSleep);
            report.AppendLine("FullscreenDetected=" + DesktopEnvironmentMonitor.IsFullscreen);
            report.AppendLine("EnvironmentHookCount=" + DesktopEnvironmentMonitor.SubscriptionHookCount);
            report.AppendLine("WeekContentReadCount=" + (weekWindow == null ? "0" : weekWindow.ContentReadCount.ToString(CultureInfo.InvariantCulture)));
            report.AppendLine("WeekContentWatching=" + (weekWindow != null && weekWindow.IsContentWatching));
            report.AppendLine("SlotEditorCount=" + editors.Count.ToString(CultureInfo.InvariantCulture));
            report.AppendLine("HotkeyLabel=" + (shortcutEnabled ? ShortcutLabel : "Disabled"));
            report.AppendLine("CtrlDHotkeyRegistered=" + hotkeyRegistered);
            report.AppendLine("CtrlDHotkeyError=" + hotkeyError.ToString(CultureInfo.InvariantCulture));
            report.AppendLine("ShortcutHiddenWindowCount=" + ((shortcutWeek != null || dormantShortcutWeek ? 1 : 0) +
                (shortcutAppearance != null ? 1 : 0)).ToString(CultureInfo.InvariantCulture));
            report.AppendLine("WeekFontSize=" + appearance.Week.FontSize.ToString(CultureInfo.InvariantCulture));
            report.AppendLine("NotesFontSize=" + appearance.Notes.FontSize.ToString(CultureInfo.InvariantCulture));
            Program.WriteAtomic(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "session-host-qa.txt"), report.ToString());
        }

        protected override void OnExit(ExitEventArgs e)
        {
            DisposeSession();
            base.OnExit(e);
        }

        private void DisposeSession()
        {
            if (disposed) return;
            disposed = true;
            if (releaseTimer != null) releaseTimer.Stop();
            if (Object.ReferenceEquals(activeSession, this)) activeSession = null;
            DesktopEnvironmentMonitor.Changed -= OnEnvironmentChanged;
            if (monitorAcquired) { DesktopEnvironmentMonitor.Release(); monitorAcquired = false; }
            ClearShortcutSnapshot();
            if (hotkeyRegistered && endpoint != null)
            {
                UnregisterHotKey(endpoint.Handle, ToggleHotkeyId);
                hotkeyRegistered = false;
            }
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }
            if (trayMenu != null) { trayMenu.Dispose(); trayMenu = null; }
            if (trayDrawingIcon != null) { trayDrawingIcon.Dispose(); trayDrawingIcon = null; }
            if (endpoint != null)
            {
                endpoint.RemoveHook(EndpointMessage);
                endpoint.Dispose();
                endpoint = null;
            }
        }
    }
}

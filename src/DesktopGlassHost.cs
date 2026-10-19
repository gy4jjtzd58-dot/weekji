using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ChineseWeeklyPlanner
{
    // Window hosting only. This class never opens, reads, or writes plan/notes data.
    // SaveRuntimeDiagnostics is an explicit, optional output for isolated UI QA.
    internal sealed class DesktopGlassHost : IDisposable
    {
        private const int GwlExStyle = -20;
        private const int GwlStyle = -16;
        private const long WsMinimizeBox = 0x00020000L;
        private const long WsExToolWindow = 0x00000080L;
        private const long WsExAppWindow = 0x00040000L;
        private const long WsExLayered = 0x00080000L;
        private const long WsExTopmost = 0x00000008L;
        private const int WmWindowPosChanging = 0x0046;
        private const int WmMouseActivate = 0x0021;
        private const int WmActivate = 0x0006;
        private const int WmShowWindow = 0x0018;
        private const int WmSettingChange = 0x001A;
        private const int WmDisplayChange = 0x007E;
        private const int WmSysCommand = 0x0112;
        private const int ScMinimize = 0xF020;
        private const int SwShowNoActivate = 4;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpFrameChanged = 0x0020;
        private const uint SwpNoOwnerZOrder = 0x0200;
        private const uint SwpNoSendChanging = 0x0400;
        private const uint GwHwndFirst = 0;
        private const uint GwHwndNext = 2;
        private const uint GwHwndPrevious = 3;
        private static readonly IntPtr HwndNotTopmost = new IntPtr(-2);
        private static readonly IntPtr HwndTopmost = new IntPtr(-1);
        private static readonly IntPtr HwndBottom = new IntPtr(1);
        private const string DesktopComponentProperty = "ChineseWeeklyPlanner.DesktopComponent.v1";

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint RegisterWindowMessage(string name);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLong64(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr window, int index, int value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLong64(IntPtr window, int index, IntPtr value);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SetProp(IntPtr window, string name, IntPtr value);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetProp(IntPtr window, string name);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr RemoveProp(IntPtr window, string name);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string title);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeWindowPos
        {
            public IntPtr Window, InsertAfter;
            public int X, Y, Width, Height;
            public uint Flags;
        }

        internal static readonly uint RestoreMessage = RegisterWindowMessage("ChineseWeeklyPlanner.RestoreTransparent.v1");
        internal static readonly uint DiagnosticMessage = RegisterWindowMessage("ChineseWeeklyPlanner.DiagnoseDesktop.v1");
        private static readonly uint TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        private readonly Window window;
        private readonly string label;
        private readonly HwndSourceHook mainHook;
        private readonly DependencyPropertyDescriptor topmostDescriptor;
        private HwndSource source;
        private HwndSource desktopOrderMarker;
        private Grid wrapper;
        private System.Windows.Forms.NotifyIcon trayIcon;
        private System.Windows.Forms.ContextMenuStrip trayMenu;
        private IntPtr desktopHost;
        private DispatcherTimer shellRetryTimer;
        private DispatcherTimer shellTransitionTimer;
        private int shellRetryCount;
        private int shellTransitionTicks;
        private int suppressedMinimizeCount;
        private int nativeRestoreCount;
        private int shellEventCount;
        private int placementAttemptCount;
        private int shellTransitionTickCount;
        private bool environmentSubscribed;
        private bool attached;
        private bool loaded;
        private bool handlingState;
        private bool placementQueued;
        private bool resettingTopmost;
        private bool manuallyHidden;
        private bool backgroundSuspended;
        private bool restoreAfterSuspension;
        private bool restoreQueued;
        private bool followsRaisedDesktop;
        private bool lastPlacementSucceeded;
        private bool disposed;

        // A notes host can reject hiding when a final save fails. No data access
        // is performed here; the owning window provides its own save callback.
        internal Func<bool> BeforeHide { get; set; }

        internal DesktopGlassHost(Window window, string label)
        {
            if (window == null) throw new ArgumentNullException("window");
            this.window = window;
            this.label = String.IsNullOrWhiteSpace(label) ? "中文周计划" : label;
            // Showing/restoring the desktop component must never interrupt the
            // foreground application. A real mouse click can still focus notes.
            window.ShowActivated = false;
            window.Topmost = false;
            mainHook = MainWindowHook;
            topmostDescriptor = DependencyPropertyDescriptor.FromProperty(Window.TopmostProperty, typeof(Window));
            Attach();
        }

        // Call only after Window.Content has been assigned. Construction calls
        // Attach once; the explicit method is idempotent for callers that use it.
        internal void Attach()
        {
            if (disposed) throw new ObjectDisposedException("DesktopGlassHost");
            if (attached) return;
            UIElement content = window.Content as UIElement;
            if (content == null) throw new InvalidOperationException("请先设置透明窗口内容，再附加桌面宿主。");

            window.Content = null;
            wrapper = new Grid
            {
                // Alpha 1 keeps empty areas interactive without a visible plate.
                Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)),
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            wrapper.Children.Add(content);
            AddResizeHandles();
            window.Content = wrapper;
#if HOST_VISUAL_PREVIEW
            // Isolated visual QA only; release builds remain desktop tool windows.
            window.ShowInTaskbar = true;
#endif

            window.SourceInitialized += OnSourceInitialized;
            window.Loaded += OnLoaded;
            window.LocationChanged += OnBoundsChanged;
            window.SizeChanged += OnSizeChanged;
            window.StateChanged += OnStateChanged;
            window.IsVisibleChanged += OnVisibilityChanged;
            window.Closed += OnClosed;
            if (topmostDescriptor != null) topmostDescriptor.AddValueChanged(window, OnTopmostChanged);
            attached = true;

            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero) InstallMainHook(handle);
            if (window.IsLoaded) OnLoaded(window, new RoutedEventArgs());
        }

        private void AddResizeHandles()
        {
            Grid handles = new Grid { Background = null };
            handles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            handles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            handles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            handles.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            handles.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            handles.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            Panel.SetZIndex(handles, 1000);
            wrapper.Children.Add(handles);
            AddHandle(handles, 0, 0, 1 | 2, Cursors.SizeNWSE);
            AddHandle(handles, 1, 0, 2, Cursors.SizeNS);
            AddHandle(handles, 2, 0, 4 | 2, Cursors.SizeNESW);
            AddHandle(handles, 0, 1, 1, Cursors.SizeWE);
            AddHandle(handles, 2, 1, 4, Cursors.SizeWE);
            AddHandle(handles, 0, 2, 1 | 8, Cursors.SizeNESW);
            AddHandle(handles, 1, 2, 8, Cursors.SizeNS);
            AddHandle(handles, 2, 2, 4 | 8, Cursors.SizeNWSE);
        }

        private void AddHandle(Grid parent, int column, int row, int sides, Cursor cursor)
        {
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(2, 255, 255, 255)));
            ControlTemplate template = new ControlTemplate(typeof(Thumb));
            template.VisualTree = border;
            Thumb thumb = new Thumb
            {
                Cursor = cursor,
                Focusable = false,
                Template = template,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            thumb.DragDelta += delegate(object sender, DragDeltaEventArgs e)
            {
                if (disposed || window.WindowState != WindowState.Normal) return;
                ResizeFromEdge(sides, e.HorizontalChange, e.VerticalChange);
                e.Handled = true;
            };
            Grid.SetColumn(thumb, column);
            Grid.SetRow(thumb, row);
            parent.Children.Add(thumb);
        }

        private void ResizeFromEdge(int sides, double horizontalChange, double verticalChange)
        {
            if (!IsFinite(horizontalChange) || !IsFinite(verticalChange)) return;
            double width = IsFinite(window.Width) ? window.Width : window.ActualWidth;
            double height = IsFinite(window.Height) ? window.Height : window.ActualHeight;
            double left = window.Left;
            double top = window.Top;
            if (!IsFinite(width) || !IsFinite(height) || !IsFinite(left) || !IsFinite(top)) return;

            if ((sides & 1) != 0)
            {
                double next = Clamp(width - horizontalChange, Math.Max(12, window.MinWidth), window.MaxWidth);
                window.Left = left + width - next;
                window.Width = next;
            }
            else if ((sides & 4) != 0)
                window.Width = Clamp(width + horizontalChange, Math.Max(12, window.MinWidth), window.MaxWidth);
            if ((sides & 2) != 0)
            {
                double next = Clamp(height - verticalChange, Math.Max(12, window.MinHeight), window.MaxHeight);
                window.Top = top + height - next;
                window.Height = next;
            }
            else if ((sides & 8) != 0)
                window.Height = Clamp(height + verticalChange, Math.Max(12, window.MinHeight), window.MaxHeight);
            QueueDesktopPlacement();
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            if (Double.IsNaN(maximum) || maximum < minimum) maximum = minimum;
            return Math.Max(minimum, Math.Min(value, maximum));
        }

        private static bool IsFinite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            InstallMainHook(new WindowInteropHelper(window).Handle);
        }

        private void InstallMainHook(IntPtr handle)
        {
            if (disposed || handle == IntPtr.Zero || source != null) return;
            source = HwndSource.FromHwnd(handle);
            if (source != null) source.AddHook(mainHook);
            SetProp(handle, DesktopComponentProperty, new IntPtr(1));
            long style = GetExtendedStyle(handle);
#if !HOST_VISUAL_PREVIEW
            SetExtendedStyle(handle, (style | WsExToolWindow) & ~WsExAppWindow);
#endif
            // The custom minus/close buttons explicitly hide to the tray. This
            // desktop component has no native minimize operation for Explorer's
            // Show Desktop / Minimize All command to apply to it.
            SetWindowStyle(handle, GetWindowStyle(handle) & ~WsMinimizeBox);
            if (!backgroundSuspended)
            {
                SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
                    SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
                PlaceAtDesktopBoundary();
            }
        }

        private IntPtr MainWindowHook(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (disposed) return IntPtr.Zero;
            if (RestoreMessage != 0 && (uint)message == RestoreMessage)
            {
                handled = true;
                window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ShowFromTray));
                return IntPtr.Zero;
            }
            if (DiagnosticMessage != 0 && (uint)message == DiagnosticMessage)
            {
                handled = true;
                window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate
                {
                    if (disposed) return;
                    try { SaveRuntimeDiagnostics(DiagnosticPath()); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }));
                return IntPtr.Zero;
            }
            if (message == WmWindowPosChanging && lParam != IntPtr.Zero)
            {
                NativeWindowPos position = (NativeWindowPos)Marshal.PtrToStructure(lParam, typeof(NativeWindowPos));
                // Freeze ONLY this HWND's order before activation/DragMove/WPF
                // resize can raise it. Keep the default processing and focus
                // messages intact; no NOACTIVATE style is placed on the editor.
                position.Flags |= SwpNoZOrder;
                Marshal.StructureToPtr(position, lParam, false);
                if (!IsDesktopHost(desktopHost)) QueueDesktopPlacement();
            }
            if (message == WmMouseActivate)
            {
                handled = true;
                // A suspended component never participates in activation.
                return new IntPtr(backgroundSuspended ? 3 : 1); // MA_NOACTIVATE / MA_ACTIVATE.
            }
            if (message == WmActivate)
            {
                // The shared native hook skips our process. Explicitly refresh
                // its cache when this component gains or loses activation.
                DesktopEnvironmentMonitor.RequestCheck();
                QueueDesktopPlacement();
            }
            if (message == WmShowWindow && wParam == IntPtr.Zero && !manuallyHidden && !backgroundSuspended && loaded)
                QueueNativeRestore();
            if (message == WmSettingChange || message == WmDisplayChange ||
                (TaskbarCreatedMessage != 0 && (uint)message == TaskbarCreatedMessage))
            {
                desktopHost = IntPtr.Zero;
                QueueDesktopPlacement();
                StartShellRecovery();
            }
            if (message == WmSysCommand && (wParam.ToInt64() & 0xFFF0L) == ScMinimize)
            {
                handled = true;
                suppressedMinimizeCount++;
                // A system minimize is not the user's explicit tray action.
                // Do not turn Show Desktop into a persistent WPF Hide().
                if (!manuallyHidden && !backgroundSuspended) StartShellTransitionRecovery();
                return IntPtr.Zero;
            }
            return IntPtr.Zero;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (disposed || loaded) return;
            loaded = true;
            CreateTrayIcon();
            CreateDesktopOrderMarker();
            SubscribeToDesktopEnvironment();
            // No companion rim windows: the outer frame was removed entirely.
            QueueDesktopPlacement();
            if (!IsDesktopHost(desktopHost)) StartShellRecovery();
#if HOST_DIAGNOSTICS
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate
            {
                SaveRuntimeDiagnostics(DiagnosticPath());
            }));
#endif
        }

        private void CreateTrayIcon()
        {
            // The combined session owns one tray icon for both desktop windows.
            // Independent window/host tests keep their existing local tray.
            if (trayIcon != null || disposed || PlannerApplication.HasSharedTray) return;
            trayMenu = new System.Windows.Forms.ContextMenuStrip();
            System.Windows.Forms.ToolStripMenuItem show = new System.Windows.Forms.ToolStripMenuItem("显示 " + label);
            show.Click += delegate { ShowFromTray(); };
            trayMenu.Items.Add(show);
            System.Windows.Forms.ToolStripMenuItem hide = new System.Windows.Forms.ToolStripMenuItem("隐藏 " + label);
            hide.Click += delegate { HideToTray(); };
            trayMenu.Items.Add(hide);
            trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            System.Windows.Forms.ToolStripMenuItem exit = new System.Windows.Forms.ToolStripMenuItem("退出 " + label);
            // Do not dispose here: notes Closing may reject exit after a save failure.
            exit.Click += delegate { if (!disposed) window.Close(); };
            trayMenu.Items.Add(exit);
            trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Text = label.Length > 63 ? label.Substring(0, 63) : label,
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            trayIcon.DoubleClick += delegate { ShowFromTray(); };
        }

        internal void HideToTray()
        {
            if (disposed) return;
            if (!window.Dispatcher.CheckAccess())
            {
                window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(HideToTray));
                return;
            }
            if (handlingState) return;
            if (backgroundSuspended)
            {
                // A manual choice during suspension overrides its restore intent.
                // Suspension has already hidden the window and must not trigger
                // notes saves or modal errors in front of a full-screen app.
                restoreAfterSuspension = false;
                manuallyHidden = true;
                return;
            }
            handlingState = true;
            try
            {
                bool mayHide = true;
                try { if (BeforeHide != null) mayHide = BeforeHide(); }
                catch (Exception ex)
                {
                    mayHide = false;
                    MessageBox.Show(window, "暂时无法隐藏窗口。\n" + ex.Message, label,
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                if (!mayHide)
                {
                    if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                    return;
                }
                manuallyHidden = true;
                window.Hide();
                // Reset an already iconic native state only while explicitly
                // hidden; system minimization never enters this method.
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            }
            finally { handlingState = false; }
        }

        internal void ShowFromTray()
        {
            if (disposed) return;
            if (!window.Dispatcher.CheckAccess())
            {
                window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ShowFromTray));
                return;
            }
            if (backgroundSuspended)
            {
                manuallyHidden = false;
                restoreAfterSuspension = true;
                DesktopEnvironmentMonitor.RequestCheck();
                return;
            }
            handlingState = true;
            try
            {
                manuallyHidden = false;
                window.WindowState = WindowState.Normal;
                window.Show();
                // Restore means make the component visible on the desktop,
                // never activate it above the user's current application.
                PlaceAtDesktopBoundary();
            }
            finally { handlingState = false; }
            DesktopEnvironmentMonitor.RequestCheck();
            QueueDesktopPlacement();
        }

        internal void SetBackgroundSuspended(bool suspended)
        {
            if (disposed) return;
            if (!window.Dispatcher.CheckAccess())
            {
                window.Dispatcher.BeginInvoke(DispatcherPriority.Send,
                    new Action(delegate { SetBackgroundSuspended(suspended); }));
                return;
            }
            if (backgroundSuspended == suspended) return;
            if (suspended)
            {
                // Set the guard before Hide delivers visibility/native messages.
                // The owning window performs any best-effort data save separately.
                restoreAfterSuspension = window.IsVisible && !manuallyHidden;
                backgroundSuspended = true;
                shellTransitionTicks = 0;
                if (shellTransitionTimer != null) shellTransitionTimer.Stop();
                if (shellRetryTimer != null) shellRetryTimer.Stop();
                handlingState = true;
                try { if (window.IsVisible) window.Hide(); }
                finally { handlingState = false; }
                return;
            }

            bool restore = restoreAfterSuspension && !manuallyHidden;
            restoreAfterSuspension = false;
            backgroundSuspended = false;
            if (!restore) return;
            if (loaded && desktopOrderMarker == null) CreateDesktopOrderMarker();
            ShowFromTray();
            if (!IsDesktopHost(desktopHost)) StartShellRecovery();
        }

        private void OnStateChanged(object sender, EventArgs e)
        {
            if (disposed || handlingState || backgroundSuspended) return;
            if (window.WindowState == WindowState.Minimized && !manuallyHidden) QueueNativeRestore();
            else QueueDesktopPlacement();
        }

        private void OnBoundsChanged(object sender, EventArgs e) { QueueDesktopPlacement(); }
        private void OnSizeChanged(object sender, SizeChangedEventArgs e) { QueueDesktopPlacement(); }
        private void OnTopmostChanged(object sender, EventArgs e)
        {
            if (disposed || resettingTopmost || backgroundSuspended) return;
            if (window.Topmost)
            {
                resettingTopmost = true;
                try { window.Topmost = false; }
                finally { resettingTopmost = false; }
            }
            QueueDesktopPlacement();
        }

        private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (disposed || backgroundSuspended) return;
            if (window.IsVisible) QueueDesktopPlacement();
            else if (loaded && !manuallyHidden) QueueNativeRestore();
        }

        private void QueueNativeRestore()
        {
            if (disposed || manuallyHidden || backgroundSuspended || restoreQueued) return;
            restoreQueued = true;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
            {
                restoreQueued = false;
                if (disposed || manuallyHidden || backgroundSuspended) return;
                IntPtr handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero || !IsWindow(handle)) return;
                handlingState = true;
                try
                {
                    // SW_SHOWNOACTIVATE normalizes a shell-minimized HWND
                    // without taking focus from the game/application. WPF
                    // observes WM_SIZE and synchronizes WindowState itself.
                    if (!window.IsVisible) window.Show();
                    if (IsIconic(handle) || !IsWindowVisible(handle))
                    {
                        ShowWindow(handle, SwShowNoActivate);
                        nativeRestoreCount++;
                    }
                }
                finally { handlingState = false; }
                PlaceAtDesktopBoundary();
                StartShellTransitionRecovery();
            }));
        }

        private void QueueDesktopPlacement()
        {
            if (disposed || manuallyHidden || backgroundSuspended || placementQueued) return;
            placementQueued = true;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
            {
                placementQueued = false;
                if (!disposed && !backgroundSuspended) PlaceAtDesktopBoundary();
            }));
        }

        private void CreateDesktopOrderMarker()
        {
            if (disposed || backgroundSuspended || desktopOrderMarker != null) return;
            // A hidden, disabled window in our own process is a stable desktop
            // reference. It has no visual, taskbar entry, owner, input or size.
            // Comparing desktop against it detects the Shell's Show Desktop
            // reordering even when Explorer does not change WS_EX_TOPMOST.
            HwndSourceParameters parameters = new HwndSourceParameters("ChineseWeeklyPlanner.DesktopOrderMarker");
            parameters.WindowStyle = unchecked((int)0x88000000); // WS_POPUP | WS_DISABLED
            parameters.ExtendedWindowStyle = 0x08000080; // NOACTIVATE | TOOLWINDOW
            parameters.Width = 0;
            parameters.Height = 0;
            desktopOrderMarker = new HwndSource(parameters);
            SetWindowPos(desktopOrderMarker.Handle, HwndBottom, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder | SwpNoSendChanging);
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (!IsDesktopHost(desktopHost)) desktopHost = FindDesktopHost();
            if (desktopHost != IntPtr.Zero && !ForegroundAllowsDesktopBand(handle))
            {
                // When an ordinary application is in front at startup, place
                // the marker explicitly just ABOVE the current desktop. This
                // establishes a false/normal baseline without depending on
                // the Shell's special treatment of HWND_BOTTOM. If we start
                // while Show Desktop is already active, retain the bottom
                // reference so that existing raised state can be detected.
                IntPtr predecessor = GetWindow(desktopHost, GwHwndPrevious);
                while (predecessor == desktopOrderMarker.Handle)
                    predecessor = GetWindow(predecessor, GwHwndPrevious);
                if (predecessor != IntPtr.Zero && (GetExtendedStyle(predecessor) & WsExTopmost) != 0)
                    predecessor = IntPtr.Zero;
                SetWindowPos(desktopOrderMarker.Handle, predecessor, 0, 0, 0, 0,
                    SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder | SwpNoSendChanging);
            }
        }

        private bool IsDesktopRaised()
        {
            if (desktopHost == IntPtr.Zero || desktopOrderMarker == null) return false;
            IntPtr marker = desktopOrderMarker.Handle;
            IntPtr item = GetWindow(desktopHost, GwHwndNext);
            int remaining = 10000;
            while (item != IntPtr.Zero && remaining-- > 0)
            {
                if (item == marker) return true;
                item = GetWindow(item, GwHwndNext);
            }
            return false;
        }

        private static bool ForegroundAllowsDesktopBand(IntPtr handle)
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == handle || GetProp(foreground, DesktopComponentProperty) != IntPtr.Zero)
                return true; // A real click may edit either desktop component.
            string className = NativeClass(foreground);
            return className == "Progman" || className == "WorkerW" ||
                className == "Shell_TrayWnd" || className == "Shell_SecondaryTrayWnd";
        }

        private void SubscribeToDesktopEnvironment()
        {
            if (disposed || environmentSubscribed) return;
            environmentSubscribed = true;
            DesktopEnvironmentMonitor.Changed += OnDesktopEnvironmentChanged;
            DesktopEnvironmentMonitor.Acquire(window.Dispatcher);
        }

        private void OnDesktopEnvironmentChanged(object sender, DesktopEnvironmentChangedEventArgs e)
        {
            if (disposed) return;
            shellEventCount++;
            if (manuallyHidden || backgroundSuspended || e.IsFullscreen) return;
            if (e.DesktopTransition || e.ShellReordered)
            {
                // Only Shell desktop transitions need bounded settling. The
                // monitor coalesces bursts and both hosts reuse the same hooks.
                QueueDesktopPlacement();
                StartShellTransitionRecovery();
            }
            else if (followsRaisedDesktop)
            {
                // Ordinary application switches require at most one demotion
                // when leaving the temporary Show Desktop band, never polling.
                QueueDesktopPlacement();
            }
        }

        private void StartShellTransitionRecovery()
        {
            if (disposed || manuallyHidden || backgroundSuspended || !loaded) return;
            // Explorer can reorder the desktop after delivering the foreground
            // event. Check this one transition for 1.2 seconds, then stop.
            // There is no steady-state window scan, input or keyboard hook.
            if (shellTransitionTimer == null)
            {
                shellTransitionTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher);
                shellTransitionTimer.Interval = TimeSpan.FromMilliseconds(150);
                shellTransitionTimer.Tick += OnShellTransitionTick;
            }
            if (shellTransitionTimer.IsEnabled) return;
            shellTransitionTicks = 8;
            shellTransitionTimer.Start();
        }

        private void OnShellTransitionTick(object sender, EventArgs e)
        {
            if (disposed || manuallyHidden || backgroundSuspended)
            {
                shellTransitionTimer.Stop();
                return;
            }
            shellTransitionTickCount++;
            PlaceAtDesktopBoundary();
            if (--shellTransitionTicks <= 0) shellTransitionTimer.Stop();
        }

        private static bool IsDesktopHost(IntPtr handle)
        {
            if (handle == IntPtr.Zero || !IsWindow(handle) || !IsWindowVisible(handle)) return false;
            string className = NativeClass(handle);
            return (className == "Progman" || className == "WorkerW") &&
                FindWindowEx(handle, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero;
        }

        private static string NativeClass(IntPtr handle)
        {
            StringBuilder name = new StringBuilder(128);
            GetClassName(handle, name, name.Capacity);
            return name.ToString();
        }

        private static IntPtr FindDesktopHost()
        {
            IntPtr shell = GetShellWindow();
            if (IsDesktopHost(shell)) return shell;
            IntPtr result = IntPtr.Zero;
            EnumWindows(delegate(IntPtr candidate, IntPtr parameter)
            {
                if (!IsDesktopHost(candidate)) return true;
                result = candidate;
                return false;
            }, IntPtr.Zero);
            return result;
        }

        private bool PlaceAtDesktopBoundary()
        {
            if (disposed || manuallyHidden || backgroundSuspended) return false;
            placementAttemptCount++;
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return false;
            if (!IsDesktopHost(desktopHost)) desktopHost = FindDesktopHost();
            if (desktopHost == IntPtr.Zero)
            {
                // Do not use HWND_BOTTOM as a substitute: that could place the
                // component behind Explorer's wallpaper. Retain the last order
                // until a real desktop host is available again.
                lastPlacementSucceeded = false;
                return false;
            }

            bool desktopRaised = IsDesktopRaised();
            bool allowRaisedBand = desktopRaised && ForegroundAllowsDesktopBand(handle);
            followsRaisedDesktop = allowRaisedBand;
            uint flags = SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder | SwpNoSendChanging;
            bool topmost = (GetExtendedStyle(handle) & WsExTopmost) != 0;
            if (allowRaisedBand)
            {
                // Show Desktop temporarily promotes the desktop over normal
                // windows. Follow only that state in the back of the topmost
                // band; an ordinary foreground app immediately ends this path.
                // The WPF Topmost preference remains false throughout.
                IntPtr anchor = GetWindow(desktopHost, GwHwndPrevious);
                int remaining = 10000;
                while (anchor != IntPtr.Zero && remaining-- > 0)
                {
                    if (anchor != handle && (GetExtendedStyle(anchor) & WsExTopmost) != 0) break;
                    anchor = GetWindow(anchor, GwHwndPrevious);
                }
                if (!topmost && !SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, flags))
                {
                    lastPlacementSucceeded = false;
                    return false;
                }
                lastPlacementSucceeded = SetWindowPos(handle,
                    anchor == IntPtr.Zero ? HwndTopmost : anchor, 0, 0, 0, 0, flags);
                return lastPlacementSucceeded;
            }

            // Before the next app can be covered, leave the Shell's temporary
            // desktop band. Never promote a widget while an ordinary app has
            // focus, even if Explorer has not completed its z-order transition.
            if (topmost && !SetWindowPos(handle, HwndNotTopmost, 0, 0, 0, 0, flags))
            {
                lastPlacementSucceeded = false;
                return false;
            }
            IntPtr predecessor = GetWindow(desktopHost, GwHwndPrevious);
            if (predecessor == handle && !desktopRaised)
            {
                lastPlacementSucceeded = true;
                return true;
            }
            while (predecessor == handle)
                predecessor = GetWindow(predecessor, GwHwndPrevious);

            if (desktopRaised)
            {
                // During the short Shell transition, prioritize staying behind
                // every visible ordinary app. Explorer will settle back below
                // these windows; the bounded recovery then restores the normal
                // boundary above wallpaper without moving any external HWND.
                IntPtr item = GetWindow(handle, GwHwndFirst);
                IntPtr lowestApp = IntPtr.Zero;
                int remaining = 10000;
                while (item != IntPtr.Zero && remaining-- > 0)
                {
                    if (item != handle && IsOrdinaryVisibleWindow(item) &&
                        (GetExtendedStyle(item) & WsExTopmost) == 0) lowestApp = item;
                    item = GetWindow(item, GwHwndNext);
                }
                if (lowestApp != IntPtr.Zero) predecessor = lowestApp;
            }

            // Insert AFTER the window immediately ABOVE the icon host. Inserting
            // after the host itself would hide us behind its opaque wallpaper.
            // If the host is the highest normal HWND, first leave any topmost
            // band, then explicitly move to normal-band TOP. NOTOPMOST alone
            // does not move an already non-topmost HWND out from under wallpaper.
            IntPtr after = predecessor;
            if (after == IntPtr.Zero || (GetExtendedStyle(after) & WsExTopmost) != 0)
            {
                after = IntPtr.Zero; // HWND_TOP: the desktop is the next normal HWND.
            }
            lastPlacementSucceeded = SetWindowPos(handle, after, 0, 0, 0, 0, flags);
            return lastPlacementSucceeded;
        }

        private void StartShellRecovery()
        {
            if (disposed || manuallyHidden || backgroundSuspended) return;
            // A bounded retry belongs only to a missing/restarting Explorer.
            // There is no steady-state polling, z-order timer, or focus stealing.
            if (shellRetryTimer == null)
            {
                shellRetryTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher);
                shellRetryTimer.Interval = TimeSpan.FromMilliseconds(750);
                shellRetryTimer.Tick += OnShellRecoveryTick;
            }
            shellRetryCount = 0;
            shellRetryTimer.Start();
        }

        private void OnShellRecoveryTick(object sender, EventArgs e)
        {
            bool unavailable = disposed || manuallyHidden || backgroundSuspended;
            bool placed = !unavailable && PlaceAtDesktopBoundary();
            if (unavailable || placed || ++shellRetryCount >= 8)
                shellRetryTimer.Stop();
        }

        private string DiagnosticPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                label == "随手记" ? "notes-host-qa.txt" : "week-host-qa.txt");
        }

        private static bool IsOrdinaryVisibleWindow(IntPtr handle)
        {
            if (!IsWindowVisible(handle) || IsIconic(handle)) return false;
            string className = NativeClass(handle);
            if (className == "Progman" || className == "WorkerW" ||
                className == "Shell_TrayWnd" || className == "Shell_SecondaryTrayWnd") return false;
            return (GetExtendedStyle(handle) & WsExToolWindow) == 0;
        }

        internal void SaveRuntimeDiagnostics(string path)
        {
            if (!window.Dispatcher.CheckAccess())
            {
                window.Dispatcher.Invoke(new Action(delegate { SaveRuntimeDiagnostics(path); }));
                return;
            }
            IntPtr handle = new WindowInteropHelper(window).Handle;
            long style = handle == IntPtr.Zero ? 0 : GetExtendedStyle(handle);
            IntPtr desktop = FindDesktopHost();
            bool desktopBelow = false;
            int ordinaryAbove = 0, ordinaryBelow = 0;
            if (handle != IntPtr.Zero)
            {
                IntPtr item = GetWindow(handle, GwHwndFirst);
                bool passedHost = false;
                int remaining = 10000;
                while (item != IntPtr.Zero && remaining-- > 0)
                {
                    if (item == handle) passedHost = true;
                    else if (item == desktop)
                    {
                        desktopBelow = passedHost;
                        break;
                    }
                    else if (IsOrdinaryVisibleWindow(item))
                    {
                        if (passedHost) ordinaryBelow++;
                        else ordinaryAbove++;
                    }
                    item = GetWindow(item, GwHwndNext);
                }
            }
            StringBuilder result = new StringBuilder();
            result.AppendLine("Label=" + label);
            result.AppendLine("LocalTrayIconVisible=" + (trayIcon != null && trayIcon.Visible));
            result.AppendLine("SharedTray=" + PlannerApplication.HasSharedTray);
            result.AppendLine("ShowInTaskbar=" + window.ShowInTaskbar);
            result.AppendLine("AllowsTransparency=" + window.AllowsTransparency);
            result.AppendLine("Opacity=" + window.Opacity.ToString(CultureInfo.InvariantCulture));
            result.AppendLine("ExtendedStyle=0x" + style.ToString("X", CultureInfo.InvariantCulture));
            result.AppendLine("APPWINDOW=" + ((style & WsExAppWindow) != 0));
            result.AppendLine("TOOLWINDOW=" + ((style & WsExToolWindow) != 0));
            result.AppendLine("LAYERED=" + ((style & WsExLayered) != 0));
            result.AppendLine("TOPMOST=" + ((style & WsExTopmost) != 0));
            result.AppendLine("WpfTopmost=" + window.Topmost);
            result.AppendLine("NativeMinimizeBox=" + ((GetWindowStyle(handle) & WsMinimizeBox) != 0));
            result.AppendLine("NativeVisible=" + (handle != IntPtr.Zero && IsWindowVisible(handle)));
            result.AppendLine("NativeMinimized=" + (handle != IntPtr.Zero && IsIconic(handle)));
            result.AppendLine("ManuallyHidden=" + manuallyHidden);
            result.AppendLine("BackgroundSuspended=" + backgroundSuspended);
            result.AppendLine("RestoreAfterSuspension=" + restoreAfterSuspension);
            result.AppendLine("DesktopRaised=" + IsDesktopRaised());
            result.AppendLine("FollowsRaisedDesktop=" + followsRaisedDesktop);
            result.AppendLine("LastPlacementSucceeded=" + lastPlacementSucceeded);
            result.AppendLine("MinimizeCommandsSuppressed=" + suppressedMinimizeCount.ToString(CultureInfo.InvariantCulture));
            result.AppendLine("NativeVisibilityRestorations=" + nativeRestoreCount.ToString(CultureInfo.InvariantCulture));
            result.AppendLine("ShellEventsReceived=" + shellEventCount.ToString(CultureInfo.InvariantCulture));
            result.AppendLine("ShellEventCount=" + shellEventCount.ToString(CultureInfo.InvariantCulture));
            result.AppendLine("PlacementAttemptCount=" + placementAttemptCount.ToString(CultureInfo.InvariantCulture));
            result.AppendLine("ShellTransitionTickCount=" + shellTransitionTickCount.ToString(CultureInfo.InvariantCulture));
            result.AppendLine("ShellTransitionTimerEnabled=" + (shellTransitionTimer != null && shellTransitionTimer.IsEnabled));
            result.AppendLine("ShellRecoveryTimerEnabled=" + (shellRetryTimer != null && shellRetryTimer.IsEnabled));
            result.AppendLine("SharedEnvironmentSubscription=" + environmentSubscribed);
            result.AppendLine("SharedEnvironmentHookCount=" + DesktopEnvironmentMonitor.SubscriptionHookCount.ToString(CultureInfo.InvariantCulture));
            result.AppendLine("ForegroundEventHook=" + (environmentSubscribed && DesktopEnvironmentMonitor.ForegroundHookActive));
            result.AppendLine("DesktopReorderEventHook=" + (environmentSubscribed && DesktopEnvironmentMonitor.ShellReorderHookActive));
            result.AppendLine("DesktopOrderMarker=" + (desktopOrderMarker != null));
            IntPtr nativeOwner = handle == IntPtr.Zero ? IntPtr.Zero : GetWindow(handle, 4); // GW_OWNER
            result.AppendLine("NativeOwnerPresent=" + (nativeOwner != IntPtr.Zero));
            result.AppendLine("NativeOwnerVisible=" + (nativeOwner != IntPtr.Zero && IsWindowVisible(nativeOwner)));
            result.AppendLine("OwnWindowForeground=" + (handle != IntPtr.Zero && GetForegroundWindow() == handle));
            result.AppendLine("DesktopHostFound=" + (desktop != IntPtr.Zero));
            result.AppendLine("DesktopHostClass=" + (desktop == IntPtr.Zero ? String.Empty : NativeClass(desktop)));
            result.AppendLine("ZOrderAboveDesktop=" + desktopBelow);
            result.AppendLine("VisibleOrdinaryAppsAbove=" + ordinaryAbove.ToString(CultureInfo.InvariantCulture));
            result.AppendLine("VisibleOrdinaryAppsBelow=" + ordinaryBelow.ToString(CultureInfo.InvariantCulture));
            result.AppendLine("Visibility=" + window.Visibility);
            result.AppendLine("WindowState=" + window.WindowState);
            result.AppendLine("Width=" + window.Width.ToString(CultureInfo.InvariantCulture));
            result.AppendLine("Height=" + window.Height.ToString(CultureInfo.InvariantCulture));
            result.AppendLine("EdgeEnabled=False");
            result.AppendLine("EdgeVisible=False");
            result.AppendLine("EdgeRingPhysicalPixels=0");
            // Only explicitly requested isolated-QA diagnostics are written.
            File.WriteAllText(path, result.ToString(), new UTF8Encoding(false));
        }

        private void OnClosed(object sender, EventArgs e) { Dispose(); }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            window.SourceInitialized -= OnSourceInitialized;
            window.Loaded -= OnLoaded;
            window.LocationChanged -= OnBoundsChanged;
            window.SizeChanged -= OnSizeChanged;
            window.StateChanged -= OnStateChanged;
            window.IsVisibleChanged -= OnVisibilityChanged;
            window.Closed -= OnClosed;
            if (topmostDescriptor != null) topmostDescriptor.RemoveValueChanged(window, OnTopmostChanged);
            if (source != null)
            {
                if (source.Handle != IntPtr.Zero) RemoveProp(source.Handle, DesktopComponentProperty);
                try { source.RemoveHook(mainHook); } catch (InvalidOperationException) { }
                source = null;
            }
            if (environmentSubscribed)
            {
                DesktopEnvironmentMonitor.Changed -= OnDesktopEnvironmentChanged;
                DesktopEnvironmentMonitor.Release();
                environmentSubscribed = false;
            }
            if (desktopOrderMarker != null)
            {
                desktopOrderMarker.Dispose();
                desktopOrderMarker = null;
            }
            if (shellTransitionTimer != null)
            {
                shellTransitionTimer.Stop();
                shellTransitionTimer.Tick -= OnShellTransitionTick;
                shellTransitionTimer = null;
            }
            if (shellRetryTimer != null)
            {
                shellRetryTimer.Stop();
                shellRetryTimer.Tick -= OnShellRecoveryTick;
                shellRetryTimer = null;
            }
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }
            if (trayMenu != null) { trayMenu.Dispose(); trayMenu = null; }
        }

        private static long GetExtendedStyle(IntPtr handle)
        {
            return IntPtr.Size == 8 ? GetWindowLong64(handle, GwlExStyle).ToInt64() : (long)GetWindowLong32(handle, GwlExStyle);
        }

        private static long GetWindowStyle(IntPtr handle)
        {
            return IntPtr.Size == 8 ? GetWindowLong64(handle, GwlStyle).ToInt64() : (long)GetWindowLong32(handle, GwlStyle);
        }

        private static void SetWindowStyle(IntPtr handle, long style)
        {
            if (IntPtr.Size == 8) SetWindowLong64(handle, GwlStyle, new IntPtr(style));
            else SetWindowLong32(handle, GwlStyle, unchecked((int)style));
        }

        private static void SetExtendedStyle(IntPtr handle, long style)
        {
            if (IntPtr.Size == 8) SetWindowLong64(handle, GwlExStyle, new IntPtr(style));
            else SetWindowLong32(handle, GwlExStyle, unchecked((int)style));
        }

    }
}

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace ChineseWeeklyPlanner
{
    // Plan editing is delegated to --slot. The embedded quick-notes panel
    // owns notes.txt; this window also remembers its bounds and split width.
    internal sealed class GlassWeekWindow : Window
    {
        private readonly string dataRoot;
        private readonly string settingsFile;
        private readonly Border surface;
        private readonly TextBlock dateRange;
        private readonly QuickNotesPanel quickNotes;
        private readonly Button notesToggle;
        private readonly GridSplitter notesSplitter;
        private readonly ColumnDefinition calendarColumn;
        private readonly ColumnDefinition splitterColumn;
        private readonly ColumnDefinition notesColumn;
        private bool quickNotesVisible = true;
        private double rememberedNotesWidth = 300;
        private readonly TextBlock[,] cellText = new TextBlock[7, 4];
        private readonly Border[,] cellBorders = new Border[7, 4];
        private readonly string[,] cellValues = new string[7, 4];
        private readonly Border[] dayBorders = new Border[7];
        private readonly TextBlock[] dayNames = new TextBlock[7];
        private readonly TextBlock[] dayDates = new TextBlock[7];
        private readonly DispatcherTimer refreshTimer;
        private readonly DispatcherTimer contentChangeTimer;
        private readonly DispatcherTimer settingsTimer;
        private FileSystemWatcher contentWatcher;
        private FileSystemWatcher directoryWatcher;
        private readonly bool[] dirtyCells = new bool[28];
        private readonly byte[] readRetryCounts = new byte[28];
        private bool fullRefreshPending;
        private bool watcherRebuildPending;
        private bool backgroundSuspended;
        private long contentReadCount;
        private string lastSavedSettings;
        private readonly string[] days = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
        private readonly string[] periods = { "上午", "下午", "晚上", "备注" };
        private DateTime displayedMonday;
        private DateTime displayedToday;
        private int weekOffset;
        private bool ready;
        private bool closing;
        private bool positionRestored;
        private readonly DesktopGlassHost desktopHost;
        private AppearanceProfile appearance = AppearanceSettings.CreateDefault().Week;

        internal DesktopGlassHost DesktopHost { get { return desktopHost; } }
        internal long ContentReadCount { get { return contentReadCount; } }
        internal bool IsContentWatching { get { return contentWatcher != null || directoryWatcher != null; } }
        internal string QuickNotesText { get { return quickNotes.NotesText; } }
        internal bool HasUnsavedNotes { get { return quickNotes.HasUnsavedChanges; } }
        internal bool IsQuickNotesVisible { get { return quickNotesVisible; } }

        internal static GlassWeekWindow Create(string root, string title)
        {
            return new GlassWeekWindow(root, title);
        }

        internal bool PrepareForShutdown()
        {
            if (!quickNotes.TrySave(true)) return false;
            SaveSettings(true);
            return true;
        }

        internal bool TrySaveNotes(bool showError)
        {
            return quickNotes.TrySave(showError);
        }

        internal void ApplyNotesAppearance(AppearanceProfile profile)
        {
            quickNotes.ApplyAppearance(profile);
        }

        internal void FocusQuickNotes()
        {
            SetQuickNotesVisible(true);
            quickNotes.FocusNotes();
        }

        internal void ToggleQuickNotes()
        {
            SetQuickNotesVisible(!quickNotesVisible);
        }

        private bool SetQuickNotesVisible(bool visible)
        {
            if (visible == quickNotesVisible) return true;
            if (!visible)
            {
                if (!quickNotes.TrySave(true)) return false;
                RememberNotesWidth();
                if (quickNotes.IsKeyboardFocusWithin) notesToggle.Focus();
            }
            quickNotesVisible = visible;
            // Expanding a planner the user has narrowed must not enlarge or
            // move it. In that case the seven columns share the available room.
            MinWidth = visible ? Math.Min(1120, Math.Max(802, Width)) : 802;
            UpdateQuickNotesLayout();
            quickNotes.SetOwnerVisible(visible && IsVisible);
            QueueSettingsSave();
            return true;
        }

        private void RememberNotesWidth()
        {
            if (quickNotesVisible && IsFinite(quickNotes.ActualWidth) && quickNotes.ActualWidth >= 240)
                rememberedNotesWidth = quickNotes.ActualWidth;
        }

        private void UpdateQuickNotesLayout()
        {
            quickNotes.Visibility = quickNotesVisible ? Visibility.Visible : Visibility.Collapsed;
            notesSplitter.Visibility = quickNotesVisible ? Visibility.Visible : Visibility.Collapsed;
            string action = quickNotesVisible ? "收起右侧随心记" : "展开右侧随心记";
            notesToggle.ToolTip = action;
            AutomationProperties.SetName(notesToggle, action);
            if (!quickNotesVisible)
            {
                calendarColumn.MinWidth = 760;
                splitterColumn.MinWidth = 0;
                splitterColumn.Width = new GridLength(0);
                notesColumn.MinWidth = 0;
                notesColumn.Width = new GridLength(0);
                return;
            }
            double contentWidth = Math.Max(760, Width - 42);
            double weekMinimum = Math.Min(760, Math.Max(0, contentWidth - 258));
            calendarColumn.MinWidth = weekMinimum;
            splitterColumn.Width = new GridLength(18);
            notesColumn.MinWidth = 240;
            notesColumn.MaxWidth = Math.Max(240, contentWidth - weekMinimum - 18);
            notesColumn.Width = new GridLength(Math.Min(Math.Max(240, rememberedNotesWidth), notesColumn.MaxWidth));
        }

        internal void SetBackgroundSuspended(bool suspended)
        {
            if (backgroundSuspended == suspended || closing) return;
            if (suspended) quickNotes.TrySave(false);
            backgroundSuspended = suspended;
            quickNotes.SetBackgroundSuspended(suspended);
            if (suspended) StopContentMonitoring();
            else if (ready && IsVisible) ResumeContentMonitoring();
        }

        internal static void Run(string dataRoot, string title)
        {
            Application application = new Application();
            application.ShutdownMode = ShutdownMode.OnMainWindowClose;
            GlassWeekWindow window = new GlassWeekWindow(dataRoot, title);
            application.SessionEnding += delegate(object sender, SessionEndingCancelEventArgs e)
            {
                if (!window.PrepareForShutdown()) e.Cancel = true;
            };
            application.Run(window);
        }

        internal static DateTime GetMonday(DateTime date, int offset)
        {
            int daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
            return date.Date.AddDays(-daysSinceMonday).AddDays((double)offset * 7.0);
        }

        internal static string GetCellPath(string root, DateTime monday, int day, int period)
        {
            if (monday.DayOfWeek != DayOfWeek.Monday)
                throw new ArgumentException("周计划日期应为周一。", "monday");
            if (day < 0 || day > 6)
                throw new ArgumentOutOfRangeException("day");
            if (period < 0 || period > 3)
                throw new ArgumentOutOfRangeException("period");
            return System.IO.Path.Combine(root, monday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                day.ToString(CultureInfo.InvariantCulture) + "-" + period.ToString(CultureInfo.InvariantCulture) + ".txt");
        }

        private GlassWeekWindow(string root, string title)
        {
            dataRoot = System.IO.Path.GetFullPath(root);
            settingsFile = System.IO.Path.Combine(dataRoot, "week-window.ini");
            Title = title;
            Width = 1440;
            Height = 570;
            MinWidth = 1120;
            MinHeight = 430;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            FontSize = 14;
            FontWeight = FontWeights.Normal;
            Foreground = Brushes.Black;
            Background = Brushes.Transparent;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            AllowsTransparency = true;
            ShowInTaskbar = false;
            Opacity = 1;
            Topmost = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);

            surface = new Border
            {
                Margin = new Thickness(1),
                CornerRadius = new CornerRadius(21),
                BorderThickness = new Thickness(0),
                Background = GlassGradient(),
                Padding = new Thickness(20, 17, 20, 11)
            };
            Content = surface;
            Grid layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(69) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            surface.Child = layout;

            Grid header = new Grid { Margin = new Thickness(3, 0, 0, 0) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.Children.Add(header);

            StackPanel titleArea = new StackPanel
            {
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeAll,
                Margin = new Thickness(0, 0, 12, 0)
            };
            dateRange = new TextBlock
            {
                Margin = new Thickness(0, 12, 0, 0),
                FontSize = 13,
                FontWeight = FontWeights.Normal,
                Foreground = Brushes.Black,
                Opacity = 0.72,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            titleArea.Children.Add(dateRange);
            titleArea.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.ChangedButton != MouseButton.Left) return;
                try { DragMove(); } catch (InvalidOperationException) { }
            };
            header.Children.Add(titleArea);

            Style buttonStyle = CreateButtonStyle();
            StackPanel navigation = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 5, 12, 0)
            };
            Grid.SetColumn(navigation, 1);
            header.Children.Add(navigation);
            Button previous = MakeButton("上周", "查看上一周", 59, buttonStyle);
            previous.Click += delegate { Navigate(-1); };
            navigation.Children.Add(previous);
            Button current = MakeButton("本周", "回到本周", 59, buttonStyle);
            current.Click += delegate { weekOffset = 0; RefreshPlans(true); };
            navigation.Children.Add(current);
            Button next = MakeButton("下周", "查看下一周", 59, buttonStyle);
            next.Click += delegate { Navigate(1); };
            navigation.Children.Add(next);
            notesToggle = MakeButton("随心记", "收起右侧随心记", 76, buttonStyle);
            notesToggle.Margin = new Thickness(14, 0, 0, 0);
            notesToggle.Click += delegate { ToggleQuickNotes(); };
            navigation.Children.Add(notesToggle);

            StackPanel windowControls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 5, 1, 0)
            };
            Grid.SetColumn(windowControls, 2);
            header.Children.Add(windowControls);
            Button minimize = MakeButton("−", "隐藏 week记", 31, buttonStyle);
            minimize.FontFamily = new FontFamily("Segoe UI");
            minimize.FontSize = 18;
            minimize.Click += delegate { desktopHost.HideToTray(); };
            windowControls.Children.Add(minimize);
            Button close = MakeButton("×", "收起到托盘", 31, buttonStyle);
            close.FontFamily = new FontFamily("Segoe UI");
            close.FontSize = 19;
            close.Click += delegate { desktopHost.HideToTray(); };
            windowControls.Children.Add(close);

            Grid combined = new Grid();
            calendarColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 760 };
            splitterColumn = new ColumnDefinition { Width = new GridLength(18) };
            combined.ColumnDefinitions.Add(calendarColumn);
            combined.ColumnDefinitions.Add(splitterColumn);
            notesColumn = new ColumnDefinition { Width = new GridLength(300), MinWidth = 240 };
            combined.ColumnDefinitions.Add(notesColumn);
            Grid.SetRow(combined, 1);
            layout.Children.Add(combined);

            notesSplitter = new GridSplitter
            {
                Width = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
                ResizeDirection = GridResizeDirection.Columns,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeWE,
                Focusable = true,
                ToolTip = "拖动调整周计划和随心记的宽度"
            };
            notesSplitter.Template = (ControlTemplate)XamlReader.Parse(@"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='GridSplitter'>
                <Grid Background='Transparent'><Border Width='2' Height='36' CornerRadius='1' Background='#3C718096' HorizontalAlignment='Center' VerticalAlignment='Center'/></Grid>
            </ControlTemplate>");
            AutomationProperties.SetName(notesSplitter, "调整周计划和随心记宽度");
            Grid.SetColumn(notesSplitter, 1);
            combined.Children.Add(notesSplitter);
            notesSplitter.DragCompleted += delegate { RememberNotesWidth(); QueueSettingsSave(); };
            quickNotes = new QuickNotesPanel(System.IO.Path.Combine(dataRoot, "notes.txt"), buttonStyle);
            Grid.SetColumn(quickNotes, 2);
            combined.Children.Add(quickNotes);
            quickNotes.SizeChanged += delegate { QueueSettingsSave(); };

            Grid calendar = new Grid();
            calendar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            for (int day = 0; day < 7; day++)
                calendar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            calendar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(58) });
            for (int period = 0; period < periods.Length; period++)
                calendar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            combined.Children.Add(calendar);

            for (int day = 0; day < 7; day++)
            {
                Border dayBorder = new Border
                {
                    Margin = new Thickness(3, 0, 3, 5),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(4, 5, 4, 4),
                    BorderThickness = new Thickness(1),
                    BorderBrush = Brush(80, 255, 255, 255),
                    Background = Brush(2, 255, 255, 255)
                };
                StackPanel dayHeader = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                TextBlock name = new TextBlock
                {
                    FontSize = 16,
                    FontWeight = FontWeights.Normal,
                    Foreground = Brushes.Black,
                    TextAlignment = TextAlignment.Center
                };
                TextBlock date = new TextBlock
                {
                    FontSize = 11.5,
                    FontWeight = FontWeights.Normal,
                    Foreground = Brushes.Black,
                    Opacity = 0.7,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 1, 0, 0)
                };
                dayHeader.Children.Add(name);
                dayHeader.Children.Add(date);
                dayBorder.Child = dayHeader;
                dayBorders[day] = dayBorder;
                dayNames[day] = name;
                dayDates[day] = date;
                Grid.SetColumn(dayBorder, day + 1);
                calendar.Children.Add(dayBorder);
            }

            for (int period = 0; period < periods.Length; period++)
            {
                TextBlock rowLabel = new TextBlock
                {
                    Text = periods[period],
                    FontSize = 14,
                    FontWeight = FontWeights.Normal,
                    Foreground = Brushes.Black,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(3, 0, 0, 0)
                };
                Grid.SetRow(rowLabel, period + 1);
                calendar.Children.Add(rowLabel);
                for (int day = 0; day < 7; day++)
                {
                    int editDay = day;
                    int editPeriod = period;
                    Border cell = new Border
                    {
                        CornerRadius = new CornerRadius(12),
                        Margin = new Thickness(3),
                        Padding = new Thickness(11, 9, 8, 8),
                        BorderBrush = Brush(85, 255, 255, 255),
                        BorderThickness = new Thickness(1),
                        Background = CellBackground(false),
                        Cursor = Cursors.Arrow
                    };
                    TextBlock text = new TextBlock
                    {
                        Text = String.Empty,
                        FontSize = 14,
                        FontWeight = FontWeights.Normal,
                        Foreground = Brush(255, 48, 48, 48),
                        Opacity = 0.4,
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 26
                    };
                    ScrollViewer scroll = new ScrollViewer
                    {
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        CanContentScroll = false,
                        Focusable = false,
                        Content = text,
                        Background = Brushes.Transparent
                    };
                    cell.Child = scroll;
                    cell.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
                    {
                        if (e.ClickCount != 2) return;
                        e.Handled = true;
                        OpenSlot(editDay, editPeriod);
                    };
                    cellBorders[day, period] = cell;
                    cellText[day, period] = text;
                    Grid.SetColumn(cell, day + 1);
                    Grid.SetRow(cell, period + 1);
                    calendar.Children.Add(cell);
                }
            }

            // Only the date needs a clock. Plan text is refreshed by file events.
            refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            refreshTimer.Tick += delegate { refreshTimer.Stop(); RefreshPlans(false); };
            contentChangeTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                { Interval = TimeSpan.FromMilliseconds(200) };
            contentChangeTimer.Tick += OnContentChangeTick;
            settingsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            settingsTimer.Tick += delegate { settingsTimer.Stop(); SaveSettings(); };

            LoadSettings();
            UpdateQuickNotesLayout();
            SourceInitialized += delegate { PlaceOnScreen(); };
            Loaded += delegate
            {
                ready = true;
                quickNotes.SetOwnerVisible(quickNotesVisible && IsVisible);
                if (IsVisible && !backgroundSuspended) ResumeContentMonitoring();
            };
            IsVisibleChanged += delegate
            {
                if (!ready || closing) return;
                quickNotes.SetOwnerVisible(quickNotesVisible && IsVisible);
                if (IsVisible && !backgroundSuspended) ResumeContentMonitoring();
                else StopContentMonitoring();
            };
            LocationChanged += delegate { QueueSettingsSave(); };
            SizeChanged += delegate { UpdateQuickNotesLayout(); QueueSettingsSave(); };
            Activated += delegate
            {
                if (CanMonitorContent) RefreshPlans(false);
            };
            StateChanged += delegate
            {
                if (CanMonitorContent && WindowState != WindowState.Minimized) RefreshPlans(false);
            };
            Closing += delegate(object sender, CancelEventArgs e)
            {
                if (!quickNotes.TrySave(true)) { e.Cancel = true; return; }
                closing = true;
                StopContentMonitoring();
                SaveSettings();
            };
            Closed += delegate { StopContentMonitoring(); settingsTimer.Stop(); quickNotes.Dispose(); };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key != Key.Escape || !quickNotes.IsKeyboardFocusWithin) return;
                e.Handled = true;
                desktopHost.HideToTray();
            };
            desktopHost = new DesktopGlassHost(this, title);
            desktopHost.BeforeHide = delegate
            {
                if (!quickNotes.TrySave(true)) return false;
                SaveSettings();
                return true;
            };
            ApplyAppearance(AppearanceSettings.CreateDefault().Week);
        }

        internal void ApplyAppearance(AppearanceProfile profile)
        {
            AppearanceProfile updated = profile == null ? AppearanceSettings.CreateDefault().Week : profile.Clone();
            updated.Normalize();
            appearance = updated;
            surface.Background = GlassGradient();
            SolidColorBrush textBrush = new SolidColorBrush(AppearanceSettings.TextColor(appearance, false));
            textBrush.Freeze();
            DateTime monday = displayedMonday == DateTime.MinValue ? GetMonday(DateTime.Today, weekOffset) : displayedMonday;
            for (int day = 0; day < 7; day++)
            {
                bool today = monday.AddDays(day) == DateTime.Today;
                dayBorders[day].Background = DayBackground(today);
                for (int period = 0; period < periods.Length; period++)
                {
                    cellBorders[day, period].Background = CellBackground(today);
                    TextBlock text = cellText[day, period];
                    text.Foreground = textBrush;
                    text.FontSize = cellValues[day, period] == null && text.Text == "暂时无法读取" ? 13 : appearance.FontSize;
                    text.LineHeight = appearance.LineHeight;
                }
            }
        }

        private void Navigate(int delta)
        {
            try
            {
                int nextOffset = checked(weekOffset + delta);
                DateTime candidate = GetMonday(DateTime.Today, nextOffset);
                candidate.AddDays(6);
                weekOffset = nextOffset;
                RefreshPlans(true);
            }
            catch (ArgumentOutOfRangeException)
            {
                MessageBox.Show(this, "已到可显示的日期范围。", "周计划", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OverflowException)
            {
                MessageBox.Show(this, "已到可显示的日期范围。", "周计划", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void RefreshPlans(bool force)
        {
            if (!CanMonitorContent) return;
            DateTime today = DateTime.Today;
            DateTime monday = GetMonday(today, weekOffset);
            bool newWeek = displayedMonday != monday;
            if (newWeek) Array.Clear(readRetryCounts, 0, readRetryCounts.Length);
            if (force || newWeek || displayedToday != today)
            {
                displayedMonday = monday;
                displayedToday = today;
                DateTime last = monday.AddDays(6);
                dateRange.Text = monday.ToString("yyyy年M月d日", CultureInfo.GetCultureInfo("zh-CN")) + " — " +
                    last.ToString(monday.Year == last.Year ? "M月d日" : "yyyy年M月d日", CultureInfo.GetCultureInfo("zh-CN"));
                for (int day = 0; day < 7; day++)
                {
                    DateTime date = monday.AddDays(day);
                    bool isToday = date == today;
                    dayNames[day].Text = days[day];
                    dayDates[day].Text = date.ToString("M/d", CultureInfo.InvariantCulture) + (isToday ? " · 今天" : String.Empty);
                    dayBorders[day].Background = DayBackground(isToday);
                    dayBorders[day].BorderBrush = isToday ? Brush(170, 170, 225, 255) : Brush(80, 255, 255, 255);
                    for (int period = 0; period < periods.Length; period++)
                    {
                        cellBorders[day, period].Background = CellBackground(isToday);
                        string name = date.ToString("yyyy年M月d日", CultureInfo.GetCultureInfo("zh-CN")) + " " + days[day] + " " + periods[period];
                        AutomationProperties.SetName(cellBorders[day, period], name + "，双击编辑");
                        cellBorders[day, period].ToolTip = name + " · 双击编辑";
                        if (newWeek)
                        {
                            cellValues[day, period] = null;
                            SetCellText(day, period, String.Empty);
                        }
                    }
                }
            }

            if (newWeek || !IsContentWatching) StartContentWatchers();
            Array.Clear(dirtyCells, 0, dirtyCells.Length);
            for (int day = 0; day < 7; day++)
            {
                for (int period = 0; period < periods.Length; period++)
                    ReadCell(day, period);
            }
            ScheduleDateRefresh();
        }

        private bool CanMonitorContent
        {
            get { return ready && !closing && !backgroundSuspended && IsVisible; }
        }

        private void ResumeContentMonitoring()
        {
            if (!CanMonitorContent) return;
            RefreshPlans(true);
            QueueSettingsSave();
        }

        private void StopContentMonitoring()
        {
            refreshTimer.Stop();
            contentChangeTimer.Stop();
            settingsTimer.Stop();
            DisposeContentWatchers();
            fullRefreshPending = false;
            watcherRebuildPending = false;
            Array.Clear(dirtyCells, 0, dirtyCells.Length);
            Array.Clear(readRetryCounts, 0, readRetryCounts.Length);
        }

        private void ScheduleDateRefresh()
        {
            refreshTimer.Stop();
            if (!CanMonitorContent) return;
            DateTime now = DateTime.Now;
            TimeSpan remaining = now.Date.AddDays(1).AddMilliseconds(250) - now;
            refreshTimer.Interval = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(250);
            refreshTimer.Start();
        }

        private void ReadCell(int day, int period)
        {
            int index = day * periods.Length + period;
            try
            {
                // Missing slots stay empty; a read never creates the data directory.
                contentReadCount++;
                string value = Program.ReadText(GetCellPath(dataRoot, displayedMonday, day, period));
                readRetryCounts[index] = 0;
                if (!String.Equals(cellValues[day, period], value, StringComparison.Ordinal))
                {
                    cellValues[day, period] = value;
                    SetCellText(day, period, value);
                }
            }
            catch (IOException)
            {
                ShowReadErrorIfEmpty(day, period);
                // A direct external write can briefly lock the file. Retry only
                // this cell, with a fixed bound instead of restarting polling.
                if (readRetryCounts[index] < 3)
                {
                    readRetryCounts[index]++;
                    dirtyCells[index] = true;
                    contentChangeTimer.Start();
                }
            }
            catch (UnauthorizedAccessException)
            {
                ShowReadErrorIfEmpty(day, period);
            }
        }

        private void StartContentWatchers()
        {
            DisposeContentWatchers();
            if (!CanMonitorContent || displayedMonday == DateTime.MinValue) return;
            string weekFolder = System.IO.Path.GetDirectoryName(GetCellPath(dataRoot, displayedMonday, 0, 0));
            string existingParent = dataRoot;
            while (!Directory.Exists(existingParent))
            {
                string parent = System.IO.Path.GetDirectoryName(existingParent);
                if (String.IsNullOrEmpty(parent) || String.Equals(parent, existingParent, StringComparison.OrdinalIgnoreCase)) return;
                existingParent = parent;
            }
            // Watch directory creation even when this week's folder (or Data)
            // does not exist yet. No empty task files are created for watching.
            directoryWatcher = CreateContentWatcher(existingParent,
                !String.Equals(existingParent, dataRoot, StringComparison.OrdinalIgnoreCase), true);
            if (Directory.Exists(weekFolder)) contentWatcher = CreateContentWatcher(weekFolder, false, false);
        }

        private FileSystemWatcher CreateContentWatcher(string folder, bool includeSubdirectories, bool directoriesOnly)
        {
            FileSystemWatcher watcher = null;
            try
            {
                watcher = new FileSystemWatcher(folder);
                watcher.IncludeSubdirectories = includeSubdirectories;
                watcher.NotifyFilter = directoriesOnly ? NotifyFilters.DirectoryName :
                    NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
                if (directoriesOnly)
                {
                    watcher.Changed += OnDirectoryChanged;
                    watcher.Created += OnDirectoryChanged;
                    watcher.Deleted += OnDirectoryChanged;
                    watcher.Renamed += OnDirectoryRenamed;
                }
                else
                {
                    watcher.Changed += OnPlanFileChanged;
                    watcher.Created += OnPlanFileChanged;
                    watcher.Deleted += OnPlanFileChanged;
                    watcher.Renamed += OnPlanFileRenamed;
                }
                watcher.Error += OnContentWatcherError;
                watcher.EnableRaisingEvents = true;
                return watcher;
            }
            catch (IOException) { if (watcher != null) watcher.Dispose(); return null; }
            catch (UnauthorizedAccessException) { if (watcher != null) watcher.Dispose(); return null; }
            catch (ArgumentException) { if (watcher != null) watcher.Dispose(); return null; }
        }

        private void DisposeContentWatchers()
        {
            FileSystemWatcher oldContent = contentWatcher;
            FileSystemWatcher oldDirectory = directoryWatcher;
            contentWatcher = null;
            directoryWatcher = null;
            if (oldContent != null) oldContent.Dispose();
            if (oldDirectory != null) oldDirectory.Dispose();
        }

        private void OnPlanFileChanged(object sender, FileSystemEventArgs e)
        {
            QueueContentChange((FileSystemWatcher)sender, e.FullPath, null, false, false);
        }

        private void OnPlanFileRenamed(object sender, RenamedEventArgs e)
        {
            QueueContentChange((FileSystemWatcher)sender, e.FullPath, e.OldFullPath, false, false);
        }

        private void OnDirectoryChanged(object sender, FileSystemEventArgs e)
        {
            QueueContentChange((FileSystemWatcher)sender, e.FullPath, null, true, false);
        }

        private void OnDirectoryRenamed(object sender, RenamedEventArgs e)
        {
            QueueContentChange((FileSystemWatcher)sender, e.FullPath, e.OldFullPath, true, false);
        }

        private void OnContentWatcherError(object sender, ErrorEventArgs e)
        {
            QueueContentChange((FileSystemWatcher)sender, null, null, false, true);
        }

        private void QueueContentChange(FileSystemWatcher sender, string path, string oldPath, bool directoryChange, bool reconcile)
        {
            // FileSystemWatcher callbacks only enqueue immutable paths. All
            // window state, pending flags and brushes stay on the UI thread.
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    if (!CanMonitorContent || (sender != contentWatcher && sender != directoryWatcher)) return;
                    bool affected = false;
                    if (reconcile || (directoryChange && (IsRelevantDirectory(path) || IsRelevantDirectory(oldPath))))
                    {
                        watcherRebuildPending = true;
                        fullRefreshPending = true;
                        affected = true;
                    }
                    else if (!directoryChange)
                        affected = MarkCellDirty(path) | MarkCellDirty(oldPath);
                    if (!affected) return;
                    contentChangeTimer.Stop();
                    contentChangeTimer.Start();
                }));
            }
            catch (InvalidOperationException) { /* Dispatcher is shutting down. */ }
        }

        private bool IsRelevantDirectory(string path)
        {
            if (String.IsNullOrEmpty(path)) return false;
            string full = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            string weekFolder = System.IO.Path.GetDirectoryName(GetCellPath(dataRoot, displayedMonday, 0, 0));
            return String.Equals(full, weekFolder, StringComparison.OrdinalIgnoreCase) ||
                String.Equals(full, dataRoot, StringComparison.OrdinalIgnoreCase) ||
                dataRoot.StartsWith(full + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private bool MarkCellDirty(string path)
        {
            if (String.IsNullOrEmpty(path)) return false;
            string weekFolder = System.IO.Path.GetDirectoryName(GetCellPath(dataRoot, displayedMonday, 0, 0));
            if (!String.Equals(System.IO.Path.GetDirectoryName(path), weekFolder, StringComparison.OrdinalIgnoreCase)) return false;
            string name = System.IO.Path.GetFileName(path);
            if (name.Length != 7 || name[1] != '-' || !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                name[0] < '0' || name[0] > '6' || name[2] < '0' || name[2] > '3') return false;
            int index = (name[0] - '0') * periods.Length + (name[2] - '0');
            dirtyCells[index] = true;
            readRetryCounts[index] = 0;
            return true;
        }

        private void OnContentChangeTick(object sender, EventArgs e)
        {
            contentChangeTimer.Stop();
            if (!CanMonitorContent) return;
            bool reconcile = fullRefreshPending || displayedToday != DateTime.Today;
            fullRefreshPending = false;
            if (watcherRebuildPending)
            {
                watcherRebuildPending = false;
                StartContentWatchers();
            }
            if (reconcile) { RefreshPlans(false); return; }
            for (int index = 0; index < dirtyCells.Length; index++)
            {
                if (!dirtyCells[index]) continue;
                dirtyCells[index] = false;
                ReadCell(index / periods.Length, index % periods.Length);
            }
        }

        private void SetCellText(int day, int period, string value)
        {
            bool empty = String.IsNullOrWhiteSpace(value);
            cellText[day, period].Text = empty ? String.Empty : value;
            cellText[day, period].FontSize = appearance.FontSize;
            cellText[day, period].Opacity = empty ? 0.4 : 1;
        }

        private void ShowReadErrorIfEmpty(int day, int period)
        {
            // Preserve the last successful rendering during a transient read failure.
            if (cellValues[day, period] != null) return;
            cellText[day, period].Text = "暂时无法读取";
            cellText[day, period].FontSize = 13;
            cellText[day, period].Opacity = 0.55;
        }

        private void OpenSlot(int day, int period)
        {
            if (PlannerApplication.TryOpenSlot(displayedMonday, day, period)) return;
            string arguments = "--slot " + displayedMonday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " +
                day.ToString(CultureInfo.InvariantCulture) + " " + period.ToString(CultureInfo.InvariantCulture);
            OpenEditor(arguments);
        }

        private void OpenEditor(string arguments)
        {
            try
            {
                string executable = System.Reflection.Assembly.GetExecutingAssembly().Location;
                ProcessStartInfo start = new ProcessStartInfo(executable, arguments);
                start.UseShellExecute = false;
                start.WorkingDirectory = System.IO.Path.GetDirectoryName(executable);
                Process process = Process.Start(start);
                if (process != null) process.Dispose();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开编辑窗口。\n" + ex.Message, "周计划", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static SolidColorBrush Brush(byte alpha, byte red, byte green, byte blue)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
            brush.Freeze();
            return brush;
        }

        private static LinearGradientBrush Gradient(Color top, Color bottom)
        {
            LinearGradientBrush brush = new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(0.8, 1));
            brush.Freeze();
            return brush;
        }

        private LinearGradientBrush GlassGradient()
        {
            LinearGradientBrush brush = new LinearGradientBrush();
            brush.StartPoint = new Point(0, 0);
            brush.EndPoint = new Point(0.85, 1);
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(AppearanceSettings.BackgroundAlpha(appearance, 5.0 / 3.0), 255, 255, 255), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(AppearanceSettings.BackgroundAlpha(appearance, 2.0 / 3.0), 246, 252, 255), 0.46));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(AppearanceSettings.BackgroundAlpha(appearance, 1.0), 229, 241, 254), 1));
            brush.Freeze();
            return brush;
        }

        private SolidColorBrush DayBackground(bool today)
        {
            return today ? Brush(AppearanceSettings.BackgroundAlpha(appearance, 14.0 / 3.0), 185, 221, 255) :
                Brush(AppearanceSettings.BackgroundAlpha(appearance, 2.0 / 3.0), 255, 255, 255);
        }

        private LinearGradientBrush CellBackground(bool today)
        {
            return today ? Gradient(
                Color.FromArgb(AppearanceSettings.BackgroundAlpha(appearance, 3.0), 220, 241, 255),
                Color.FromArgb(AppearanceSettings.BackgroundAlpha(appearance, 4.0 / 3.0), 218, 237, 255)) :
                Gradient(Color.FromArgb(AppearanceSettings.BackgroundAlpha(appearance, 2.0 / 3.0), 255, 255, 255),
                    Color.FromArgb(AppearanceSettings.BackgroundAlpha(appearance, 1.0 / 3.0), 255, 255, 255));
        }

        private static Style CreateButtonStyle()
        {
            return (Style)XamlReader.Parse(@"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>
              <Setter Property='Background' Value='#10FFFFFF'/>
              <Setter Property='BorderBrush' Value='#70FFFFFF'/>
              <Setter Property='BorderThickness' Value='1'/>
              <Setter Property='Foreground' Value='Black'/>
              <Setter Property='Cursor' Value='Hand'/>
              <Setter Property='FontSize' Value='12'/>
              <Setter Property='FontWeight' Value='Normal'/>
              <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
              <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
                <Border x:Name='Pill' CornerRadius='15' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>
                  <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' RecognizesAccessKey='True'/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Pill' Property='Background' Value='#25FFFFFF'/></Trigger>
                  <Trigger Property='IsPressed' Value='True'><Setter TargetName='Pill' Property='Background' Value='#354A99C8'/></Trigger>
                  <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Pill' Property='BorderBrush' Value='#A5479ED9'/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate></Setter.Value></Setter>
            </Style>");
        }

        private Button MakeButton(string content, string accessibleName, double width, Style style)
        {
            Button button = new Button
            {
                Content = content,
                Width = width,
                Height = 31,
                Margin = new Thickness(5, 0, 0, 0),
                Style = style,
                ToolTip = accessibleName,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(button, accessibleName);
            return button;
        }

        private void QueueSettingsSave()
        {
            if (!ready || closing || backgroundSuspended || !IsVisible || WindowState != WindowState.Normal) return;
            settingsTimer.Stop();
            settingsTimer.Start();
        }

        private void SaveSettings(bool force = false)
        {
            if (!ready || (backgroundSuspended && !force && !closing)) return;
            try
            {
                RememberNotesWidth();
                Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
                if (bounds.IsEmpty || !IsFinite(bounds.Left) || !IsFinite(bounds.Top) ||
                    !IsFinite(bounds.Width) || !IsFinite(bounds.Height) || bounds.Width < MinWidth || bounds.Height < MinHeight) return;
                string value = "Width=" + bounds.Width.ToString("0.##", CultureInfo.InvariantCulture) +
                    "\nHeight=" + bounds.Height.ToString("0.##", CultureInfo.InvariantCulture) +
                    "\nLeft=" + bounds.Left.ToString("0.##", CultureInfo.InvariantCulture) +
                    "\nTop=" + bounds.Top.ToString("0.##", CultureInfo.InvariantCulture) +
                    "\nNotesWidth=" + rememberedNotesWidth.ToString("0.##", CultureInfo.InvariantCulture) +
                    "\nNotesVisible=" + (quickNotesVisible ? "True" : "False") + "\n";
                if (String.Equals(lastSavedSettings, value, StringComparison.Ordinal)) return;
                // Layout writes never modify any of the 28 plan files.
                Program.WriteAtomic(settingsFile, value);
                lastSavedSettings = value;
            }
            catch (Exception) { /* Appearance settings must never prevent closing. */ }
        }

        private void LoadSettings()
        {
            if (!File.Exists(settingsFile)) return;
            try
            {
                bool hasLeft = false, hasTop = false;
                bool hasNotesVisibility = false;
                double savedWidth = Width;
                string[] lines = File.ReadAllLines(settingsFile);
                lastSavedSettings = String.Join("\n", lines) + "\n";
                foreach (string line in lines)
                {
                    string[] pair = line.Split(new char[] { '=' }, 2);
                    if (pair.Length != 2) continue;
                    bool visible;
                    if (pair[0] == "NotesVisible" && Boolean.TryParse(pair[1], out visible))
                    {
                        quickNotesVisible = visible;
                        hasNotesVisibility = true;
                        continue;
                    }
                    double value;
                    if (!Double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !IsFinite(value)) continue;
                    if (pair[0] == "Width" && value > 0 && value <= 6000) savedWidth = value;
                    else if (pair[0] == "Height" && value >= MinHeight && value <= 4000) Height = value;
                    else if (pair[0] == "Left" && Math.Abs(value) < 100000) { Left = value; hasLeft = true; }
                    else if (pair[0] == "Top" && Math.Abs(value) < 100000) { Top = value; hasTop = true; }
                    else if (pair[0] == "NotesWidth" && value >= 240 && value <= 4500)
                        rememberedNotesWidth = value;
                }
                // Parse visibility before clamping dimensions so a collapsed
                // layout can retain its smaller saved width, in any INI order.
                MinWidth = quickNotesVisible ? (hasNotesVisibility ? Math.Min(1120, Math.Max(802, savedWidth)) : 1120) : 802;
                Width = Math.Max(MinWidth, savedWidth);
                positionRestored = hasLeft && hasTop;
            }
            catch (Exception) { /* Ignore invalid or inaccessible appearance settings. */ }
        }

        private static bool IsFinite(double value)
        {
            return !Double.IsInfinity(value) && !Double.IsNaN(value);
        }

        private void PlaceOnScreen()
        {
            PresentationSource presentation = PresentationSource.FromVisual(this);
            Matrix fromPixels = presentation != null && presentation.CompositionTarget != null ? presentation.CompositionTarget.TransformFromDevice : Matrix.Identity;
            Matrix toPixels = fromPixels;
            if (toPixels.HasInverse) toPixels.Invert();
            else { fromPixels = Matrix.Identity; toPixels = Matrix.Identity; }
            System.Drawing.Point cursor = System.Windows.Forms.Cursor.Position;
            if (positionRestored)
            {
                Point saved = toPixels.Transform(new Point(Left + Width / 2, Top + Height / 2));
                cursor = new System.Drawing.Point((int)saved.X, (int)saved.Y);
            }
            System.Drawing.Rectangle pixels = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
            Point first = fromPixels.Transform(new Point(pixels.Left, pixels.Top));
            Point last = fromPixels.Transform(new Point(pixels.Right, pixels.Bottom));
            double availableWidth = last.X - first.X;
            double availableHeight = last.Y - first.Y;
            Width = Math.Min(Width, Math.Max(MinWidth, availableWidth - 24));
            Height = Math.Min(Height, Math.Max(MinHeight, availableHeight - 24));
            double startLeft = positionRestored ? Left : first.X + (availableWidth - Width) / 2;
            double startTop = positionRestored ? Top : first.Y + Math.Min(88, Math.Max(12, (availableHeight - Height) / 3));
            Left = Math.Max(first.X + 8, Math.Min(startLeft, last.X - Width - 8));
            Top = Math.Max(first.Y + 8, Math.Min(startTop, last.Y - Height - 8));
        }
    }
}

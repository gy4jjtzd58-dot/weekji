using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ChineseWeeklyPlanner
{
    // A child control of the planner, with no window, tray icon or background
    // polling of its own. The existing notes.txt remains the source of truth.
    internal sealed class QuickNotesPanel : Grid, IDisposable
    {
        private readonly string file;
        private readonly TextBox body;
        private readonly TextBlock status;
        private readonly TextBlock placeholder;
        private readonly Ellipse statusDot;
        private readonly Border writingSurface;
        private readonly DispatcherTimer saveTimer;
        private bool dirty;
        private bool suspended;
        private bool ownerVisible;
        private bool disposed;
        private bool readFailed;
        private bool loading;

        internal string NotesText { get { return body.Text; } }
        internal bool HasUnsavedChanges { get { return dirty; } }

        internal QuickNotesPanel(string notesFile, Style buttonStyle)
        {
            file = notesFile;
            MinWidth = 240;
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(58) });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });

            TextBlock heading = new TextBlock
            {
                Text = "随心记",
                FontSize = 16,
                Foreground = Brush(255, 25, 34, 47),
                FontWeight = FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 0, 5)
            };
            Children.Add(heading);

            writingSurface = new Border
            {
                CornerRadius = new CornerRadius(14),
                BorderBrush = Brush(80, 255, 255, 255),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 12, 12, 12)
            };
            Grid.SetRow(writingSurface, 1);
            Children.Add(writingSurface);
            Grid writingLayout = new Grid();
            writingSurface.Child = writingLayout;
            body = new TextBox
            {
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                CaretBrush = Brush(255, 20, 112, 187),
                SelectionBrush = Brush(115, 118, 188, 244),
                SelectionOpacity = 0.65,
                FontWeight = FontWeights.Normal,
                Padding = new Thickness(0),
                FocusVisualStyle = null
            };
            AutomationProperties.SetName(body, "随心记内容");
            writingLayout.Children.Add(body);
            placeholder = new TextBlock
            {
                Text = "写下此刻想到的事…",
                Foreground = Brush(255, 115, 130, 146),
                FontSize = 14,
                Margin = new Thickness(1, 3, 0, 0),
                IsHitTestVisible = false
            };
            writingLayout.Children.Add(placeholder);

            Grid footer = new Grid { Margin = new Thickness(3, 4, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetRow(footer, 2);
            Children.Add(footer);
            statusDot = new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = Brush(255, 110, 145, 159),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0)
            };
            footer.Children.Add(statusDot);
            status = new TextBlock
            {
                Text = "自动保存到本地",
                Foreground = Brush(255, 86, 101, 119),
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            Grid.SetColumn(status, 1);
            footer.Children.Add(status);
            Button save = new Button
            {
                Content = "保存",
                Width = 52,
                Height = 28,
                Style = buttonStyle,
                ToolTip = "立即保存（Ctrl+S）",
                Margin = new Thickness(5, 0, 0, 0)
            };
            AutomationProperties.SetName(save, "保存随心记");
            save.Click += delegate { if (readFailed && !dirty) LoadNotes(); else TrySave(true); };
            Grid.SetColumn(save, 2);
            footer.Children.Add(save);

            LoadNotes();
            saveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                { Interval = TimeSpan.FromMilliseconds(700) };
            saveTimer.Tick += delegate
            {
                saveTimer.Stop();
                if (CanAutosave) TrySave(false);
            };
            body.TextChanged += delegate
            {
                UpdatePlaceholder();
                if (loading) return;
                dirty = true;
                SetStatus("正在输入…", 139, 162, 184, null);
                saveTimer.Stop();
                if (CanAutosave) saveTimer.Start();
            };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || (e.Key != Key.S && e.Key != Key.Enter)) return;
                e.Handled = true;
                if (readFailed && !dirty) LoadNotes(); else TrySave(true);
            };
            ApplyAppearance(AppearanceSettings.CreateDefault().Notes);
        }

        private bool CanAutosave { get { return !disposed && !suspended && ownerVisible; } }

        internal void FocusNotes()
        {
            if (disposed || !ownerVisible) return;
            body.Focus();
            Keyboard.Focus(body);
            body.CaretIndex = body.Text.Length;
        }

        internal void SetOwnerVisible(bool visible)
        {
            ownerVisible = visible;
            saveTimer.Stop();
            if (dirty && CanAutosave) saveTimer.Start();
        }

        internal void SetBackgroundSuspended(bool value)
        {
            if (disposed || suspended == value) return;
            saveTimer.Stop();
            suspended = value;
            if (dirty && CanAutosave) saveTimer.Start();
        }

        internal void ApplyAppearance(AppearanceProfile profile)
        {
            AppearanceProfile appearance = profile == null ? AppearanceSettings.CreateDefault().Notes : profile.Clone();
            appearance.Normalize();
            body.FontSize = appearance.FontSize;
            SolidColorBrush textBrush = new SolidColorBrush(AppearanceSettings.TextColor(appearance, true));
            textBrush.Freeze();
            body.Foreground = textBrush;
            body.SetValue(Block.LineHeightProperty, appearance.LineHeight);
            body.SetValue(Block.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
            LinearGradientBrush background = new LinearGradientBrush(
                Color.FromArgb(AppearanceSettings.BackgroundAlpha(appearance, 2.0 / 3.0), 255, 255, 255),
                Color.FromArgb(AppearanceSettings.BackgroundAlpha(appearance, 1.0 / 3.0), 255, 255, 255),
                new Point(0, 0), new Point(0.8, 1));
            background.Freeze();
            writingSurface.Background = background;
        }

        internal bool TrySave(bool showError)
        {
            if (disposed) return !dirty;
            saveTimer.Stop();
            // A read error must never turn an untouched empty editor into an
            // overwrite of the user's existing file. The save button retries it.
            if (readFailed)
            {
                if (dirty)
                {
                    SetStatus("原内容未读出 · 暂不能覆盖", 190, 90, 90,
                        "请先检查 notes.txt 的读取权限，原文件尚未修改。\n" + file);
                    return false;
                }
                // No edit is at risk, so a read-only failure need not trap the
                // user in the app. An explicit Save button retries the read.
                return true;
            }
            if (!dirty) return true;
            try
            {
                Program.WriteAtomic(file, body.Text);
                dirty = false;
                SetStatus("已保存 · " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture),
                    55, 145, 120, null);
                return true;
            }
            catch (Exception ex)
            {
                SetStatus(showError ? "保存失败 · 请检查权限或空间后重试" : "保存失败 · 请重试",
                    190, 90, 90, ex.Message);
                return false;
            }
        }

        private bool LoadNotes()
        {
            loading = true;
            try
            {
                // ReadText treats an absent file as empty and creates no files.
                body.Text = Program.ReadText(file);
                readFailed = false;
                body.IsReadOnly = false;
                UpdatePlaceholder();
                SetStatus("自动保存到本地", 110, 145, 159, null);
                return true;
            }
            catch (Exception ex)
            {
                readFailed = true;
                body.IsReadOnly = true;
                UpdatePlaceholder();
                placeholder.Visibility = Visibility.Collapsed;
                SetStatus("读取失败 · 点保存重试", 190, 90, 90, ex.Message);
                return false;
            }
            finally { loading = false; }
        }

        private void UpdatePlaceholder()
        {
            placeholder.Visibility = body.Text.Length == 0 && !readFailed ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetStatus(string text, byte red, byte green, byte blue, string detail)
        {
            status.Text = text;
            status.ToolTip = detail;
            statusDot.Fill = Brush(255, red, green, blue);
        }

        private static SolidColorBrush Brush(byte alpha, byte red, byte green, byte blue)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
            brush.Freeze();
            return brush;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            saveTimer.Stop();
        }
    }
}

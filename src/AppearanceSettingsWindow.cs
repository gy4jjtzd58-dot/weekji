using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ChineseWeeklyPlanner
{
    internal sealed class AppearanceSettingsWindow : Window
    {
        private readonly AppearanceSettings initial;
        private AppearanceSettings working;
        private readonly Action<AppearanceSettings> preview;
        private readonly Action<AppearanceSettings> save;
        private readonly ProfileControls[] panels = new ProfileControls[2];
        private readonly TextBlock status;
        private readonly Border statusBorder;
        private bool refreshing;
        private bool committed;

        private sealed class ProfileControls
        {
            internal bool Notes;
            internal Slider FontSize;
            internal Slider Tone;
            internal Slider Transparency;
            internal Slider LineHeight;
            internal TextBlock FontValue;
            internal TextBlock ToneValue;
            internal TextBlock TransparencyValue;
            internal TextBlock LineValue;
            internal TextBlock Sample;
            internal Border SampleSurface;
        }

        internal AppearanceSettingsWindow(AppearanceSettings value, Action<AppearanceSettings> preview,
            Action<AppearanceSettings> save)
        {
            if (preview == null) throw new ArgumentNullException("preview");
            if (save == null) throw new ArgumentNullException("save");
            this.preview = preview;
            this.save = save;
            initial = (value ?? AppearanceSettings.CreateDefault()).Clone();
            initial.Normalize();
            working = initial.Clone();

            Title = "week记 · 外观设置";
            MaxWidth = Math.Max(320, SystemParameters.WorkArea.Width - 32);
            MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 32);
            Width = Math.Min(500, MaxWidth);
            Height = Math.Min(650, MaxHeight);
            MinWidth = Math.Min(460, MaxWidth);
            MinHeight = Math.Min(620, MaxHeight);
            ResizeMode = ResizeMode.CanMinimize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
#if APPEARANCE_VISUAL_PREVIEW
            ShowInTaskbar = true;
#endif
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 251));
            Foreground = new SolidColorBrush(Color.FromRgb(32, 41, 53));
            FontFamily = new FontFamily("Microsoft YaHei UI");
            FontSize = 13;
            FontWeight = FontWeights.Normal;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);

            Grid layout = new Grid { Margin = new Thickness(24, 19, 24, 18) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = layout;

            StackPanel heading = new StackPanel { Margin = new Thickness(0, 0, 0, 17) };
            heading.Children.Add(new TextBlock { Text = "外观设置", FontSize = 23, Margin = new Thickness(0, 0, 0, 6) });
            heading.Children.Add(new TextBlock
            {
                Text = "分别调整周计划和随心记，拖动滑块即可预览。",
                Foreground = MutedBrush(), FontSize = 12, TextWrapping = TextWrapping.Wrap
            });
            layout.Children.Add(heading);

            TabControl tabs = new TabControl
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(222, 228, 235)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(17, 10, 17, 12)
            };
            AutomationProperties.SetName(tabs, "调整对象");
            Grid.SetRow(tabs, 1);
            layout.Children.Add(tabs);
            panels[0] = BuildProfile(false, tabs);
            panels[1] = BuildProfile(true, tabs);

            status = new TextBlock
            {
                Name = "AppearanceStatus", TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(155, 43, 43)), FontSize = 12
            };
            AutomationProperties.SetName(status, "外观设置状态");
            statusBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(253, 238, 238)),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(9),
                Margin = new Thickness(0, 10, 0, 0), MaxHeight = 105,
                Visibility = Visibility.Collapsed,
                Child = new ScrollViewer
                {
                    Content = status, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                }
            };
            Grid.SetRow(statusBorder, 2);
            layout.Children.Add(statusBorder);

            Grid footer = new Grid { Margin = new Thickness(0, 17, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetRow(footer, 3);
            layout.Children.Add(footer);
            Button reset = CreateButton("恢复默认", false);
            reset.HorizontalAlignment = HorizontalAlignment.Left;
            reset.Click += delegate
            {
                working = AppearanceSettings.CreateDefault();
                RefreshPanels();
                PreviewWorking();
            };
            footer.Children.Add(reset);
            Button cancel = CreateButton("取消", false);
            cancel.Margin = new Thickness(8, 0, 0, 0);
            cancel.IsCancel = true;
            cancel.Click += delegate { Close(); };
            Grid.SetColumn(cancel, 1);
            footer.Children.Add(cancel);
            Button accept = CreateButton("保存", true);
            accept.Margin = new Thickness(8, 0, 0, 0);
            accept.Click += OnSave;
            Grid.SetColumn(accept, 2);
            footer.Children.Add(accept);

            Closing += OnClosing;
            RefreshPanels();
        }

        private ProfileControls BuildProfile(bool notes, TabControl tabs)
        {
            ProfileControls controls = new ProfileControls { Notes = notes };
            string target = notes ? "随心记" : "周计划";
            StackPanel panel = new StackPanel();
            ScrollViewer scroller = new ScrollViewer
            {
                Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(0, 0, 5, 0)
            };
            TabItem tab = new TabItem { Header = target, Content = scroller, Padding = new Thickness(19, 7, 19, 7) };
            AutomationProperties.SetName(tab, target);
            tabs.Items.Add(tab);

            controls.FontSize = AddSlider(panel, "字号", target + "字号", 11, 30, 0.5, out controls.FontValue);
            controls.Tone = AddSlider(panel, "文字深浅", target + "文字深浅", 0, 100, 1, out controls.ToneValue);
            controls.Transparency = AddSlider(panel, "背景透明度", target + "背景透明度", 35, 100, 1, out controls.TransparencyValue);
            controls.LineHeight = AddSlider(panel, "行高", target + "行高", 18, 48, 1, out controls.LineValue);
            controls.Tone.ToolTip = "向左更黑，向右更浅";
            controls.Transparency.ToolTip = "数值越大，背景越透明";

            panel.Children.Add(new TextBlock
            {
                Text = "效果预览", Foreground = MutedBrush(), FontSize = 12,
                Margin = new Thickness(0, 3, 0, 7)
            });
            Border sample = new Border
            {
                Height = 121, CornerRadius = new CornerRadius(10), ClipToBounds = true,
                Background = new LinearGradientBrush(Color.FromRgb(178, 209, 215), Color.FromRgb(219, 203, 224), 25)
            };
            controls.SampleSurface = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 7, 16, 7) };
            sample.Child = controls.SampleSurface;
            controls.Sample = new TextBlock
            {
                Text = notes ? "记录一个想法\n留点时间散步" : "阅读 30 分钟\n傍晚运动一下",
                FontWeight = FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight
            };
            controls.SampleSurface.Child = controls.Sample;
            panel.Children.Add(sample);
            panel.Children.Add(new TextBlock
            {
                Text = "保存后保留设置；取消会恢复本次调整前的外观。",
                Foreground = MutedBrush(), FontSize = 11,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0)
            });
            RoutedPropertyChangedEventHandler<double> changed = delegate
            {
                if (refreshing) return;
                AppearanceProfile profile = notes ? working.Notes : working.Week;
                profile.FontSize = controls.FontSize.Value;
                profile.TextTone = controls.Tone.Value;
                profile.BackgroundOpacity = 1.0 - controls.Transparency.Value / 100.0;
                profile.LineHeight = controls.LineHeight.Value;
                profile.Normalize();
                RefreshPanels();
                PreviewWorking();
            };
            controls.FontSize.ValueChanged += changed;
            controls.Tone.ValueChanged += changed;
            controls.Transparency.ValueChanged += changed;
            controls.LineHeight.ValueChanged += changed;
            return controls;
        }

        private static Slider AddSlider(Panel parent, string label, string name, double minimum,
            double maximum, double tick, out TextBlock value)
        {
            Grid row = new Grid { Margin = new Thickness(0, 8, 0, 4) };
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            value = new TextBlock
            {
                MinWidth = 55, TextAlignment = TextAlignment.Right,
                Foreground = MutedBrush(), VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(value, 1);
            row.Children.Add(value);
            Slider slider = new Slider
            {
                Minimum = minimum, Maximum = maximum, TickFrequency = tick,
                IsSnapToTickEnabled = false, SmallChange = tick, LargeChange = tick * 4,
                Margin = new Thickness(0, 5, 0, 0), Height = 25,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(slider, name);
            Grid.SetRow(slider, 1);
            Grid.SetColumnSpan(slider, 2);
            row.Children.Add(slider);
            parent.Children.Add(row);
            return slider;
        }

        private void RefreshPanels()
        {
            refreshing = true;
            try
            {
                working.Normalize();
                foreach (ProfileControls controls in panels)
                {
                    if (controls == null) continue;
                    AppearanceProfile profile = controls.Notes ? working.Notes : working.Week;
                    controls.FontSize.Value = profile.FontSize;
                    controls.Tone.Value = profile.TextTone;
                    controls.Transparency.Value = 100 * (1 - profile.BackgroundOpacity);
                    controls.LineHeight.Minimum = Math.Max(18, profile.FontSize * 1.15);
                    controls.LineHeight.Value = profile.LineHeight;
                    controls.FontValue.Text = profile.FontSize.ToString("0.#", CultureInfo.InvariantCulture);
                    controls.ToneValue.Text = profile.TextTone.ToString("0", CultureInfo.InvariantCulture) + "%";
                    controls.TransparencyValue.Text = (100 * (1 - profile.BackgroundOpacity)).ToString("0.#", CultureInfo.InvariantCulture) + "%";
                    controls.LineValue.Text = profile.LineHeight.ToString("0.#", CultureInfo.InvariantCulture);
                    controls.Sample.FontSize = profile.FontSize;
                    controls.Sample.LineHeight = profile.LineHeight;
                    controls.Sample.Foreground = new SolidColorBrush(AppearanceSettings.TextColor(profile, controls.Notes));
                    controls.SampleSurface.Background = new SolidColorBrush(Color.FromArgb(
                        AppearanceSettings.BackgroundAlpha(profile, 1), 255, 255, 255));
                }
            }
            finally { refreshing = false; }
        }

        private void PreviewWorking()
        {
            try
            {
                preview(working.Clone());
                ClearStatus();
            }
            catch (Exception error)
            {
                ShowStatus("暂时无法预览外观，请重试。\n" + error.Message);
            }
        }

        private void OnSave(object sender, RoutedEventArgs args)
        {
            try
            {
                working.Normalize();
                save(working.Clone());
                committed = true;
                Close();
            }
            catch (Exception error)
            {
                ShowStatus("设置未能保存，当前调整仍保留在窗口中。请重试。\n" + error.Message);
            }
        }

        private void OnClosing(object sender, CancelEventArgs args)
        {
            if (committed) return;
            try
            {
                preview(initial.Clone());
                ClearStatus();
            }
            catch (Exception error)
            {
                args.Cancel = true;
                ShowStatus("暂时无法恢复原来的外观，窗口已保持打开，请重试。\n" + error.Message);
            }
        }

        private void ShowStatus(string message)
        {
            status.Text = message;
            statusBorder.Visibility = Visibility.Visible;
        }

        private void ClearStatus()
        {
            status.Text = String.Empty;
            statusBorder.Visibility = Visibility.Collapsed;
        }

        private static Button CreateButton(string label, bool primary)
        {
            Button button = new Button
            {
                Content = label, MinWidth = 72, Padding = new Thickness(13, 7, 13, 7),
                FontWeight = FontWeights.Normal, Cursor = Cursors.Hand,
                Foreground = primary ? Brushes.White : new SolidColorBrush(Color.FromRgb(47, 58, 71)),
                Background = new SolidColorBrush(primary ? Color.FromRgb(54, 104, 158) : Color.FromRgb(247, 249, 251)),
                BorderBrush = new SolidColorBrush(primary ? Color.FromRgb(54, 104, 158) : Color.FromRgb(212, 220, 230)),
                BorderThickness = new Thickness(1)
            };
            AutomationProperties.SetName(button, label);
            return button;
        }

        private static SolidColorBrush MutedBrush()
        {
            return new SolidColorBrush(Color.FromRgb(104, 116, 131));
        }
    }
}

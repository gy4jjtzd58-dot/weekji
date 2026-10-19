using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ChineseWeeklyPlanner
{
    internal static class Program
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowName);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr window, int command);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 0) args = new[] { "--week" };
            if (args.Length == 1 && args[0] == "--self-test")
                return SelfTest();

            try
            {
                string dataRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "Data"));
                if (args.Length == 1 && PlannerApplication.IsControlArgument(args[0]))
                {
                    if (PlannerApplication.TrySendControl(dataRoot, args[0])) return 0;
                    // Allow an update command to close the previous, separate-window
                    // build through its normal save/cancel path as well.
                    if (args[0] == "--diagnose-session" || args[0] == "--hide-all" || args[0] == "--toggle-all" || args[0] == "--sleep" || args[0] == "--wake") return 0;
                    string[] titles = new[] { AppBrand.Name };
                    foreach (string existingTitle in titles)
                    {
                        IntPtr closing = FindOwnWindow(existingTitle);
                        uint message = args[0].StartsWith("--diagnose-", StringComparison.Ordinal) ? DesktopGlassHost.DiagnosticMessage : 0x0010;
                        if (closing != IntPtr.Zero && !PostMessage(closing, message, IntPtr.Zero, IntPtr.Zero)) return 3;
                    }
                    return 0;
                }
                bool notes = args.Length == 1 && args[0] == "--notes";
                bool week = args.Length == 1 && args[0] == "--week";
                bool appearance = args.Length == 1 && args[0] == "--appearance";
                DateTime monday = DateTime.MinValue;
                int day = 0;
                int period = 0;
                if (!notes && !week && !appearance && (args.Length != 4 || args[0] != "--slot" ||
                    !DateTime.TryParseExact(args[1], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out monday) ||
                    monday.DayOfWeek != DayOfWeek.Monday ||
                    !Int32.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out day) ||
                    !Int32.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out period) ||
                    day < 0 || day > 6 || period < 0 || period > 3))
                {
                    MessageBox.Show("请打开 week记，或双击周计划格子编辑。", AppBrand.Name,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 2;
                }

                if (week || notes || appearance) return PlannerApplication.RunSession(dataRoot, week, appearance);

                string file = Path.Combine(dataRoot, monday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    day.ToString(CultureInfo.InvariantCulture) + "-" + period.ToString(CultureInfo.InvariantCulture) + ".txt");
                string[] days = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
                string[] periods = { "上午", "下午", "晚上", "备注" };
                DateTime date = monday.AddDays(day);
                string title = days[day] + " · " + periods[period] + " | " +
                    date.ToString("M月d日", CultureInfo.GetCultureInfo("zh-CN"));

                using (SHA256 hash = SHA256.Create())
                {
                    string key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(file.ToUpperInvariant()))).Replace("-", "");
                    bool created;
                    using (Mutex singleInstance = new Mutex(true, "Local\\ChineseWeeklyPlanner_" + key, out created))
                    {
                        if (!created)
                        {
                            IntPtr existing = FindOwnWindow(title);
                            if (existing != IntPtr.Zero)
                            {
                                ShowWindowAsync(existing, 9);
                                SetForegroundWindow(existing);
                            }
                            return 0;
                        }
                        try
                        {
                            Application.EnableVisualStyles();
                            Application.SetCompatibleTextRenderingDefault(false);
                            using (EditorForm editor = new EditorForm(file, title, false))
                                Application.Run(editor);
                        }
                        finally
                        {
                            singleInstance.ReleaseMutex();
                        }
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开：\r\n" + ex.Message, AppBrand.Name,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        internal static IntPtr FindOwnWindow(string title)
        {
            string ownFolder = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            string ownName;
            using (Process ownProcess = Process.GetCurrentProcess())
                ownName = Path.GetFileName(ownProcess.MainModule.FileName);
            IntPtr candidate = IntPtr.Zero;
            for (int count = 0; count < 256; count++)
            {
                candidate = FindWindowEx(IntPtr.Zero, candidate, null, title);
                if (candidate == IntPtr.Zero) break;
                uint processId;
                GetWindowThreadProcessId(candidate, out processId);
                try
                {
                    using (Process process = Process.GetProcessById((int)processId))
                    {
                        string processPath = process.MainModule.FileName;
                        string fileName = Path.GetFileName(processPath);
                        if (String.Equals(fileName, ownName, StringComparison.OrdinalIgnoreCase) &&
                            String.Equals(ownFolder, Path.GetDirectoryName(processPath), StringComparison.OrdinalIgnoreCase))
                            return candidate;
                    }
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            return IntPtr.Zero;
        }

        internal static string ReadText(string path)
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : String.Empty;
        }

        internal static void WriteAtomic(string path, string text)
        {
            string fullPath = Path.GetFullPath(path);
            string folder = Path.GetDirectoryName(fullPath);
            Directory.CreateDirectory(folder);
            string temporary = Path.Combine(folder, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                // No shell, INI interpolation, or Rainmeter command parsing is involved.
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(text);
                    writer.Flush();
                    stream.Flush(true);
                }
                if (File.Exists(fullPath))
                    File.Replace(temporary, fullPath, null);
                else
                    File.Move(temporary, fullPath);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        private static int SelfTest()
        {
            string testParent = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
            string testRoot = Path.GetFullPath(Path.Combine(testParent, "ChineseWeeklyPlanner-Test-" + Guid.NewGuid().ToString("N")));
            try
            {
                string file = Path.Combine(testRoot, "2026-10-12", "0-0.txt");
                string first = "中文周计划\r\n引号：\"hello\" 'world'\r\n#tag [brackets] [!Refresh] | % 😀";
                WriteAtomic(file, first);
                if (!String.Equals(ReadText(file), first, StringComparison.Ordinal))
                    throw new InvalidOperationException("首次保存的文本不一致。");
                string second = "第二次保存\n\n早上：读书\r\n下午：开会\r\n晚上：休息\r\n";
                WriteAtomic(file, second);
                if (!String.Equals(ReadText(file), second, StringComparison.Ordinal))
                    throw new InvalidOperationException("覆盖保存的文本不一致。");
                byte[] bytes = File.ReadAllBytes(file);
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                    throw new InvalidOperationException("文本不应带有 UTF-8 BOM。");
                WriteAtomic(file, String.Empty);
                if (ReadText(file).Length != 0)
                    throw new InvalidOperationException("清空保存失败。");
                if (Directory.GetFiles(testRoot, "*.tmp", SearchOption.AllDirectories).Length != 0)
                    throw new InvalidOperationException("出现未清理的临时文件。");
                if (GlassWeekWindow.GetMonday(new DateTime(2026, 10, 18), 0) != new DateTime(2026, 10, 12)
                    || GlassWeekWindow.GetMonday(new DateTime(2026, 10, 18), 1) != new DateTime(2026, 10, 19)
                    || GlassWeekWindow.GetMonday(new DateTime(2027, 1, 3), 0) != new DateTime(2026, 12, 28)
                    || GlassWeekWindow.GetMonday(new DateTime(2024, 3, 3), 0) != new DateTime(2024, 2, 26))
                    throw new InvalidOperationException("跨周、跨年或闰年日期计算失败。");
                string legacyPath = Path.Combine(testRoot, "2026-10-12", "6-2.txt");
                if (!String.Equals(GlassWeekWindow.GetCellPath(testRoot, new DateTime(2026, 10, 12), 6, 2), legacyPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("与现有周计划数据路径不兼容。");
                string remarksPath = Path.Combine(testRoot, "2026-10-12", "0-3.txt");
                if (!String.Equals(GlassWeekWindow.GetCellPath(testRoot, new DateTime(2026, 10, 12), 0, 3), remarksPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("备注数据路径不正确。");
                string remarks = "备注：记得带资料\r\n第二行事项\n中文与符号 # []";
                WriteAtomic(remarksPath, remarks);
                if (!String.Equals(ReadText(remarksPath), remarks, StringComparison.Ordinal))
                    throw new InvalidOperationException("备注多行文本保存失败。");
                Console.WriteLine("WeekEditor self-test: PASS");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("WeekEditor self-test: FAIL: " + ex);
                return 1;
            }
            finally
            {
                // Delete only the uniquely named child created exclusively by this test.
                if (String.Equals(Path.GetDirectoryName(testRoot), testParent.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(testRoot).StartsWith("ChineseWeeklyPlanner-Test-", StringComparison.Ordinal)
                    && Directory.Exists(testRoot))
                    Directory.Delete(testRoot, true);
            }
        }
    }

    internal sealed class EditorForm : Form
    {
        private bool showingWithoutActivation;
        protected override bool ShowWithoutActivation { get { return showingWithoutActivation; } }
        internal void ShowWithoutFocus()
        {
            showingWithoutActivation = true;
            try { Show(); }
            finally { showingWithoutActivation = false; }
        }
        private readonly string file;
        private readonly bool notes;
        private readonly TextBox body;
        private readonly Label status;
        private readonly System.Windows.Forms.Timer saveTimer;
        private readonly Icon ownIcon;
        private bool dirty;
        private bool loading = true;

        internal EditorForm(string file, string title, bool notes)
        {
            SuspendLayout();
            this.file = file;
            this.notes = notes;
            ownIcon = AppBrand.CreateTrayIcon();
            Icon = ownIcon;
            Text = title;
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.FromArgb(249, 248, 244);
            ForeColor = Color.FromArgb(39, 49, 51);
            ClientSize = new Size(520, 410);
            MinimumSize = new Size(400, 340);
            StartPosition = FormStartPosition.Manual;
            ShowIcon = false;
            ShowInTaskbar = false;
            KeyPreview = true;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(22, 18, 22, 16);
            layout.ColumnCount = 1;
            layout.RowCount = 4;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 51F));
            Controls.Add(layout);

            Label heading = new Label();
            heading.Text = title;
            heading.Dock = DockStyle.Fill;
            heading.Margin = new Padding(0);
            heading.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Regular, GraphicsUnit.Point);
            heading.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(heading, 0, 0);

            Panel hintRow = new Panel();
            hintRow.Dock = DockStyle.Fill;
            hintRow.Margin = new Padding(0);
            layout.Controls.Add(hintRow, 0, 1);

            status = new Label();
            status.Text = notes ? "本地自动保存 · 随时记下一件事" : "写下安排，一行一件事";
            status.AutoSize = true;
            status.Location = new Point(0, 3);
            status.ForeColor = Color.FromArgb(113, 121, 119);
            status.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            hintRow.Controls.Add(status);

            if (notes)
            {
                CheckBox pin = new CheckBox();
                pin.Text = "置顶";
                pin.AutoSize = true;
                pin.Dock = DockStyle.Right;
                pin.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
                pin.CheckedChanged += delegate { TopMost = pin.Checked; };
                hintRow.Controls.Add(pin);
            }

            Panel inputBorder = new Panel();
            inputBorder.Dock = DockStyle.Fill;
            inputBorder.Margin = new Padding(0);
            inputBorder.Padding = new Padding(11);
            inputBorder.BackColor = Color.White;
            layout.Controls.Add(inputBorder, 0, 2);

            body = new TextBox();
            body.Multiline = true;
            body.AcceptsReturn = true;
            body.AcceptsTab = true;
            body.ScrollBars = ScrollBars.Vertical;
            body.BorderStyle = BorderStyle.None;
            body.BackColor = Color.White;
            body.ForeColor = ForeColor;
            body.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Regular, GraphicsUnit.Point);
            body.Dock = DockStyle.Fill;
            body.MaxLength = 0;
            body.Text = Program.ReadText(file);
            body.TextChanged += BodyChanged;
            body.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.A)
                {
                    body.SelectAll();
                    e.SuppressKeyPress = true;
                }
            };
            inputBorder.Controls.Add(body);

            FlowLayoutPanel footer = new FlowLayoutPanel();
            footer.Dock = DockStyle.Fill;
            footer.FlowDirection = FlowDirection.RightToLeft;
            footer.Margin = new Padding(0, 13, 0, 0);
            footer.WrapContents = false;
            layout.Controls.Add(footer, 0, 3);

            Button save = MakeButton(notes ? "立即保存" : "保存", true);
            save.Click += delegate
            {
                if (TrySave(true) && !notes)
                    Close();
            };
            footer.Controls.Add(save);

            Button cancel = MakeButton(notes ? "关闭" : "取消", false);
            cancel.Click += delegate { Close(); };
            footer.Controls.Add(cancel);

            saveTimer = new System.Windows.Forms.Timer();
            saveTimer.Interval = 700;
            saveTimer.Tick += delegate
            {
                saveTimer.Stop();
                TrySave(false);
            };

            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    if (TrySave(true) && !notes)
                        Close();
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    Close();
                }
            };

            Shown += delegate
            {
                Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
                Point mouse = Cursor.Position;
                int x = Math.Max(area.Left, Math.Min(mouse.X - 60, area.Right - Width));
                int y = Math.Max(area.Top, Math.Min(mouse.Y - 35, area.Bottom - Height));
                Location = new Point(x, y);
                body.Focus();
                body.SelectionStart = body.TextLength;
                body.SelectionLength = 0;
            };

            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                saveTimer.Stop();
                if (notes && dirty && !TrySave(true))
                    e.Cancel = true;
            };
            FormClosed += delegate { saveTimer.Dispose(); ownIcon.Dispose(); };
            loading = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ResumeLayout(true);
        }

        private Button MakeButton(string text, bool primary)
        {
            Button button = new Button();
            button.Text = text;
            button.Size = new Size(94, 34);
            button.Margin = new Padding(8, 0, 0, 0);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.UseVisualStyleBackColor = false;
            button.BackColor = primary ? Color.FromArgb(56, 102, 86) : Color.FromArgb(236, 237, 231);
            button.ForeColor = primary ? Color.White : Color.FromArgb(67, 77, 73);
            button.Cursor = Cursors.Hand;
            button.TabStop = true;
            return button;
        }

        private void BodyChanged(object sender, EventArgs e)
        {
            if (loading)
                return;
            dirty = true;
            if (notes)
            {
                status.Text = "正在输入 · 本地自动保存";
                saveTimer.Stop();
                saveTimer.Start();
            }
        }

        private bool TrySave(bool showError)
        {
            try
            {
                if (dirty || !File.Exists(file))
                    Program.WriteAtomic(file, body.Text);
                dirty = false;
                if (notes)
                    status.Text = "已保存到本地 · " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex)
            {
                status.Text = "尚未保存 · 请重试";
                if (showError)
                    MessageBox.Show(this, "保存失败，请检查文件夹权限或磁盘空间。\r\n" + ex.Message,
                        "中文周计划", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }
    }
}

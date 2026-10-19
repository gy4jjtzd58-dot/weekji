using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;

namespace ChineseWeeklyPlanner
{
    internal sealed class AppearanceProfile
    {
        private readonly double defaultFontSize;

        internal double FontSize { get; set; }
        internal double TextTone { get; set; }
        internal double BackgroundOpacity { get; set; }
        internal double LineHeight { get; set; }

        internal AppearanceProfile(double fontSize)
        {
            defaultFontSize = fontSize;
            FontSize = fontSize;
            TextTone = 50;
            BackgroundOpacity = 3.0 / 255.0;
            LineHeight = 26;
        }

        internal AppearanceProfile Clone()
        {
            return new AppearanceProfile(defaultFontSize)
            {
                FontSize = FontSize,
                TextTone = TextTone,
                BackgroundOpacity = BackgroundOpacity,
                LineHeight = LineHeight
            };
        }

        internal void Normalize()
        {
            FontSize = Clamp(FiniteOrDefault(FontSize, defaultFontSize), 11, 30);
            TextTone = Clamp(FiniteOrDefault(TextTone, 50), 0, 100);
            BackgroundOpacity = Clamp(FiniteOrDefault(BackgroundOpacity, 3.0 / 255.0), 0, 0.65);
            LineHeight = Clamp(FiniteOrDefault(LineHeight, 26), Math.Max(18, FontSize * 1.15), 48);
        }

        internal static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static double FiniteOrDefault(double value, double fallback)
        {
            return Double.IsNaN(value) || Double.IsInfinity(value) ? fallback : value;
        }
    }

    internal sealed class AppearanceSettings
    {
        internal AppearanceProfile Week { get; set; }
        internal AppearanceProfile Notes { get; set; }

        internal static AppearanceSettings CreateDefault()
        {
            return new AppearanceSettings
            {
                Week = new AppearanceProfile(16.5),
                Notes = new AppearanceProfile(16)
            };
        }

        internal AppearanceSettings Clone()
        {
            return new AppearanceSettings
            {
                Week = (Week ?? new AppearanceProfile(16.5)).Clone(),
                Notes = (Notes ?? new AppearanceProfile(16)).Clone()
            };
        }

        internal void Normalize()
        {
            if (Week == null) Week = new AppearanceProfile(16.5);
            if (Notes == null) Notes = new AppearanceProfile(16);
            Week.Normalize();
            Notes.Normalize();
        }

        internal static AppearanceSettings Load(string dataRoot)
        {
            AppearanceSettings result = CreateDefault();
            string path = Path.Combine(dataRoot, "appearance.ini");
            if (!File.Exists(path)) return result;
            try
            {
                AppearanceProfile selected = null;
                foreach (string source in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = source.Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                    if (line[0] == '[' && line[line.Length - 1] == ']')
                    {
                        string section = line.Substring(1, line.Length - 2).Trim();
                        selected = String.Equals(section, "Week", StringComparison.OrdinalIgnoreCase) ? result.Week :
                            String.Equals(section, "Notes", StringComparison.OrdinalIgnoreCase) ? result.Notes : null;
                        continue;
                    }
                    if (selected == null) continue;
                    int equals = line.IndexOf('=');
                    if (equals <= 0) continue;
                    string key = line.Substring(0, equals).Trim();
                    double value;
                    if (!Double.TryParse(line.Substring(equals + 1).Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out value)) continue;
                    if (String.Equals(key, "FontSize", StringComparison.OrdinalIgnoreCase)) selected.FontSize = value;
                    else if (String.Equals(key, "TextTone", StringComparison.OrdinalIgnoreCase)) selected.TextTone = value;
                    else if (String.Equals(key, "BackgroundOpacity", StringComparison.OrdinalIgnoreCase)) selected.BackgroundOpacity = value;
                    else if (String.Equals(key, "LineHeight", StringComparison.OrdinalIgnoreCase)) selected.LineHeight = value;
                }
            }
            catch (IOException) { return CreateDefault(); }
            catch (UnauthorizedAccessException) { return CreateDefault(); }
            result.Normalize();
            return result;
        }

        internal void Save(string dataRoot)
        {
            AppearanceSettings value = Clone();
            value.Normalize();
            StringBuilder text = new StringBuilder();
            text.AppendLine("; 周计划与随手记的外观；不包含任务内容。");
            AppendProfile(text, "Week", value.Week);
            text.AppendLine();
            AppendProfile(text, "Notes", value.Notes);
            Program.WriteAtomic(Path.Combine(dataRoot, "appearance.ini"), text.ToString());
        }

        private static void AppendProfile(StringBuilder text, string section, AppearanceProfile profile)
        {
            text.Append('[').Append(section).AppendLine("]");
            text.Append("FontSize=").AppendLine(profile.FontSize.ToString("R", CultureInfo.InvariantCulture));
            text.Append("TextTone=").AppendLine(profile.TextTone.ToString("R", CultureInfo.InvariantCulture));
            text.Append("BackgroundOpacity=").AppendLine(profile.BackgroundOpacity.ToString("R", CultureInfo.InvariantCulture));
            text.Append("LineHeight=").AppendLine(profile.LineHeight.ToString("R", CultureInfo.InvariantCulture));
        }

        internal static Color TextColor(AppearanceProfile profile, bool notes)
        {
            AppearanceProfile value = (profile ?? new AppearanceProfile(notes ? 16 : 16.5)).Clone();
            value.Normalize();
            byte red = notes ? (byte)25 : (byte)48;
            byte green = notes ? (byte)34 : (byte)48;
            byte blue = notes ? (byte)47 : (byte)48;
            return Color.FromRgb(ToneChannel(red, value.TextTone), ToneChannel(green, value.TextTone), ToneChannel(blue, value.TextTone));
        }

        private static byte ToneChannel(byte middle, double tone)
        {
            double value = tone <= 50 ? middle * tone / 50.0 : middle + (160 - middle) * (tone - 50) / 50.0;
            return (byte)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        internal static byte BackgroundAlpha(AppearanceProfile profile, double factor)
        {
            AppearanceProfile value = (profile ?? new AppearanceProfile(16.5)).Clone();
            value.Normalize();
            if (Double.IsNaN(factor) || Double.IsInfinity(factor)) factor = 1;
            return (byte)Math.Round(AppearanceProfile.Clamp(value.BackgroundOpacity * 255 * factor, 0, 255), MidpointRounding.AwayFromZero);
        }
    }
}

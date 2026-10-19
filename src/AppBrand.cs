using System;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChineseWeeklyPlanner
{
    internal static class AppBrand
    {
        internal const string Name = "week记";
        internal const string IconResourceName = "ChineseWeeklyPlanner.weekji.ico";
        private static ImageSource windowIcon;

        // Return an independent icon: NotifyIcon and Form each own their icon lifetime.
        internal static System.Drawing.Icon CreateTrayIcon()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(IconResourceName))
            {
                if (stream != null)
                {
                    using (System.Drawing.Icon icon = new System.Drawing.Icon(stream, 32, 32))
                        return (System.Drawing.Icon)icon.Clone();
                }
            }
            return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        }

        internal static ImageSource WindowIcon
        {
            get
            {
                if (windowIcon == null)
                {
                    using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(IconResourceName))
                    {
                        if (stream != null)
                        {
                            // OnLoad detaches the frozen image from the resource stream.
                            IconBitmapDecoder decoder = new IconBitmapDecoder(stream,
                                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                            BitmapFrame largest = decoder.Frames[0];
                            foreach (BitmapFrame frame in decoder.Frames)
                                if (frame.PixelWidth > largest.PixelWidth) largest = frame;
                            largest.Freeze();
                            windowIcon = largest;
                        }
                    }
                    if (windowIcon == null)
                    {
                        using (System.Drawing.Icon fallback = CreateTrayIcon())
                        {
                            BitmapSource source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                                fallback.Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            source.Freeze();
                            windowIcon = source;
                        }
                    }
                }
                return windowIcon;
            }
        }
    }
}

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed' }
    exit 0
}
Add-Type -AssemblyName System.Drawing
$assetRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'assets'
[IO.Directory]::CreateDirectory($assetRoot) | Out-Null

# Render the selected W wave as native vector geometry. The source SVG is the
# editable master; this matching path avoids any crop of the concept preview.
Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class WeekjiIconBuilder
{
    private static GraphicsPath Tile()
    {
        GraphicsPath path = new GraphicsPath();
        path.AddArc(4, 4, 108, 108, 180, 90);
        path.AddArc(144, 4, 108, 108, 270, 90);
        path.AddArc(144, 144, 108, 108, 0, 90);
        path.AddArc(4, 144, 108, 108, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static byte[] Render(int size)
    {
        // Supersampling keeps the round caps readable at tray sizes.
        const int sourceSize = 1024;
        using (Bitmap source = new Bitmap(sourceSize, sourceSize, PixelFormat.Format32bppArgb))
        {
            using (Graphics graphics = Graphics.FromImage(source))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.ScaleTransform(4, 4);
                using (GraphicsPath tile = Tile())
                using (SolidBrush paper = new SolidBrush(Color.FromArgb(252, 252, 253)))
                    graphics.FillPath(paper, tile);
                using (GraphicsPath wave = new GraphicsPath())
                using (Pen ink = new Pen(Color.FromArgb(7, 53, 120), 24))
                {
                    ink.StartCap = LineCap.Round;
                    ink.EndCap = LineCap.Round;
                    ink.LineJoin = LineJoin.Round;
                    wave.AddBezier(36, 101, 54, 111, 56, 153, 81, 153);
                    wave.AddBezier(81, 153, 101, 153, 107, 113, 128, 113);
                    wave.AddBezier(128, 113, 149, 113, 156, 153, 175, 153);
                    wave.AddBezier(175, 153, 198, 153, 202, 112, 220, 101);
                    graphics.DrawPath(ink, wave);
                }
            }
            using (Bitmap target = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(target))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.DrawImage(source, new Rectangle(0, 0, size, size), 0, 0,
                        sourceSize, sourceSize, GraphicsUnit.Pixel);
                }
                using (MemoryStream stream = new MemoryStream())
                {
                    target.Save(stream, ImageFormat.Png);
                    return stream.ToArray();
                }
            }
        }
    }

    public static void Build(string assetRoot)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        byte[][] images = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++) images[i] = Render(sizes[i]);
        File.WriteAllBytes(Path.Combine(assetRoot, "weekji.png"), images[images.Length - 1]);
        using (BinaryWriter writer = new BinaryWriter(File.Create(Path.Combine(assetRoot, "weekji.ico"))))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(images[i].Length);
                writer.Write(offset);
                offset += images[i].Length;
            }
            foreach (byte[] image in images) writer.Write(image);
        }
    }
}
'@ -ReferencedAssemblies System.Drawing
[WeekjiIconBuilder]::Build($assetRoot)
Get-Item -LiteralPath (Join-Path $assetRoot 'weekji.ico'), (Join-Path $assetRoot 'weekji.png') |
    Select-Object Name, Length

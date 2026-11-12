using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Frostbar.Core.Icons;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Icons;

/// <summary>
/// Makes app icons as PNG files, in a helper process of its own (<c>Frostbar.exe --icons &lt;request file&gt; &lt;pid&gt;</c>):
/// the shell's large icons cost a process about 45 MB that it keeps (the image factory's handlers and caches; M5
/// 2026-10-12: 177 icons, at 256 or 80 px alike, 10 MB at 48 px), so the shell itself only reads the PNGs
/// (<see cref="IconService"/>). Each request line is "source TAB px TAB file"; each answer line on stdout is
/// "new|same|fail TAB file". A PNG that came out the same as the file already there is not written again (its time is
/// touched). The helper stops when the shell that asked has gone.
/// </summary>
public static class IconExtractor
{
    const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

    public static int RunHelper(string requestFile, int parentPid)
    {
        Process? parent = null;
        try { parent = Process.GetProcessById(parentPid); } catch (ArgumentException) { }
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        try
        {
            foreach (var line in File.ReadLines(requestFile))
            {
                if (parent is { HasExited: true }) return 3;
                var parts = line.Split('\t');
                if (parts.Length != 3 || !int.TryParse(parts[1], out int px)) continue;
                string file = parts[2];
                try
                {
                    var image = Extract(parts[0], px);
                    if (image == null) { stdout.WriteLine("fail\t" + file); continue; }
                    byte[] png = Png(image);
                    if (File.Exists(file) && File.ReadAllBytes(file).AsSpan().SequenceEqual(png))
                    {
                        File.SetLastWriteTimeUtc(file, DateTime.UtcNow); // still in use: kept by the clean-up
                        stdout.WriteLine("same\t" + file);
                        continue;
                    }
                    string tmp = $"{file}.{Environment.ProcessId}.tmp";
                    File.WriteAllBytes(tmp, png);
                    File.Move(tmp, file, overwrite: true);
                    stdout.WriteLine("new\t" + file);
                }
                catch (Exception) { stdout.WriteLine("fail\t" + file); }
            }
            return 0;
        }
        finally { parent?.Dispose(); }
    }

    /// <summary>
    /// The shell's icon for an AppsFolder parsing name ("shell:AppsFolder\…") or a file path, scaled to
    /// <paramref name="px"/>: its 256 px image (icon only), or for an app with only small icons its 48 px icon (the
    /// shell frames those at larger sizes: <see cref="IconFrame"/>).
    /// </summary>
    public static BitmapSource? Extract(string source, int px)
    {
        if (ShellApi.SHCreateItemFromParsingName(source, IntPtr.Zero, typeof(IShellItem).GUID, out var item) != 0 || item == null) return null;
        try
        {
            var factory = (IShellItemImageFactory)item;
            var image = Image(factory, 256, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out bool framed);
            if (framed && Image(factory, 48, SIIGBF_ICONONLY, out _) is { } small) image = small;
            return Scale(image, px);
        }
        finally { Marshal.ReleaseComObject(item); }
    }

    static BitmapSource? Image(IShellItemImageFactory factory, int size, int flags, out bool framed)
    {
        framed = false;
        if (factory.GetImage(new SIZE(size, size), flags, out IntPtr hbm) != 0 || hbm == IntPtr.Zero) return null;
        try { return FromHBitmap(hbm, out framed); }
        finally { Native.DeleteObject(hbm); }
    }

    /// <summary>
    /// 32 bpp pixels top-down: GDI knows the section's real orientation, which its header does not report reliably
    /// (M0 S6: every icon came out upside down when copied "as stored"). The factory's pixels are premultiplied
    /// (Pbgra32); an image with no alpha at all is made opaque. <paramref name="framed"/>: the shell's frame around a
    /// small icon.
    /// </summary>
    static BitmapSource? FromHBitmap(IntPtr hbm, out bool framed)
    {
        framed = false;
        if (Native.GetObject(hbm, Marshal.SizeOf<BITMAP>(), out BITMAP bm) == 0 || bm.bmBitsPixel != 32) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var px = new byte[w * h * 4];
        var bmi = new BITMAPINFO { bmiHeader = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 } };
        IntPtr dc = Native.GetDC(IntPtr.Zero);
        try { if (Native.GetDIBits(dc, hbm, 0, (uint)h, px, ref bmi, 0) != h) return null; }
        finally { Native.ReleaseDC(IntPtr.Zero, dc); }
        bool anyAlpha = false;
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        framed = anyAlpha && IconFrame.IsFramed(px, w, h);
        var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        bs.Freeze();
        return bs;
    }

    /// <summary>Scaled once (high quality) to <paramref name="px"/>, then frozen.</summary>
    public static BitmapSource? Scale(BitmapSource? src, int px)
    {
        if (src == null) return null;
        if (src.PixelWidth == px) return src;
        double k = (double)px / src.PixelWidth;
        var scaled = new TransformedBitmap(src, new ScaleTransform(k, k));
        var copy = new WriteableBitmap(scaled);
        copy.Freeze();
        return copy;
    }

    static byte[] Png(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>A PNG made by the helper, as premultiplied pixels (null if it cannot be read).</summary>
    public static BitmapSource? ReadPng(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var frame = new PngBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            BitmapSource src = frame.Format == PixelFormats.Pbgra32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
            int w = src.PixelWidth, h = src.PixelHeight;
            var px = new byte[w * h * 4];
            src.CopyPixels(px, w * 4, 0);
            var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
            bs.Freeze();
            return bs;
        }
        catch (Exception) { return null; }
    }
}

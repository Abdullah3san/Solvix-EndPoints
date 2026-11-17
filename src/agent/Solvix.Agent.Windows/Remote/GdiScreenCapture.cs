namespace Solvix.Agent.Windows.Remote;

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Remote;

/// <summary>
/// Captures the primary screen of the user session this process runs in (GDI) and encodes it as JPEG.
/// Only works inside the signed-in user's own session (the screen helper); the lock screen, UAC and
/// other secure desktops cannot be captured from there.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class GdiScreenCapture : IScreenCapture
{
    private const int MaxWidth = 1920;
    private const long Quality = 60;
    private static readonly ImageCodecInfo? JpegCodec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public ScreenFrame? Capture()
    {
        if (JpegCodec is null)
        {
            return null;
        }

        var width = GetSystemMetrics(0);
        var height = GetSystemMetrics(1);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        try
        {
            using var screen = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(screen))
            {
                graphics.CopyFromScreen(0, 0, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            }

            var scale = width > MaxWidth ? MaxWidth / (double)width : 1.0;
            using var output = scale < 1.0 ? Resize(screen, (int)(width * scale), (int)(height * scale)) : null;
            using var encoded = new MemoryStream();
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, Quality);
            (output ?? screen).Save(encoded, JpegCodec, parameters);
            return new ScreenFrame(encoded.ToArray(), output?.Width ?? width, output?.Height ?? height);
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or InvalidOperationException)
        {
            return null; // e.g. the desktop is switching (lock screen, UAC): try again next frame
        }
    }

    /// <summary>Real pixel sizes on scaled displays, and no console window left on the user's screen.</summary>
    public static void PrepareHelperProcess()
    {
        FreeConsole();
        SetProcessDPIAware();
    }

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    private static Bitmap Resize(Bitmap source, int width, int height)
    {
        var target = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(target);
        graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
        graphics.DrawImage(source, 0, 0, width, height);
        return target;
    }

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDPIAware();
}

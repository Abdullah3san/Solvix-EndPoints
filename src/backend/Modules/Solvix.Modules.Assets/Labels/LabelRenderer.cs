namespace Solvix.Modules.Assets.Labels;

using System.Globalization;
using System.Security;
using System.Text;
using Solvix.SharedKernel.Exceptions;
using ZXing;
using ZXing.Common;
using ZXing.QrCode.Internal;

/// <summary>
/// Renders asset labels as scalable SVG (printable at any resolution, no image
/// libraries needed). Encoding is done by ZXing: Code 128 for the asset tag,
/// QR (error correction M) for the QR payload.
/// </summary>
public static class LabelRenderer
{
    public const int MaxBarcodeLength = 80;
    public const int MaxQrLength = 512;

    /// <summary>Code 128 symbol of <paramref name="text"/> with the human-readable text underneath.</summary>
    public static string Code128Svg(string text, int moduleWidth = 2, int barHeight = 60, bool includeText = true)
    {
        var matrix = EncodeCode128(text);
        const int quiet = 10;
        var width = (matrix.Width + (2 * quiet)) * moduleWidth;
        var textHeight = includeText ? 18 : 0;
        var height = barHeight + textHeight;
        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" width="{width}" height="{height}" shape-rendering="crispEdges" role="img" aria-label="Barcode {Escape(text)}">""");
        svg.Append(CultureInfo.InvariantCulture, $"""<rect width="{width}" height="{height}" fill="#fff"/><path fill="#000" d=""").Append('"');
        foreach (var (start, length) in Runs(matrix, 0))
        {
            svg.Append(CultureInfo.InvariantCulture, $"M{(start + quiet) * moduleWidth} 0h{length * moduleWidth}v{barHeight}h-{length * moduleWidth}z");
        }

        svg.Append("\"/>");
        if (includeText)
        {
            svg.Append(CultureInfo.InvariantCulture, $"""<text x="{width / 2}" y="{height - 4}" font-family="monospace" font-size="14" text-anchor="middle" fill="#000">{Escape(text)}</text>""");
        }

        return svg.Append("</svg>").ToString();
    }

    public static string QrSvg(string payload, int moduleSize = 4)
    {
        var matrix = EncodeQr(payload);
        const int quiet = 4;
        var size = (matrix.Width + (2 * quiet)) * moduleSize;
        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {size} {size}" width="{size}" height="{size}" shape-rendering="crispEdges" role="img" aria-label="QR code">""");
        svg.Append(CultureInfo.InvariantCulture, $"""<rect width="{size}" height="{size}" fill="#fff"/><path fill="#000" d=""").Append('"');
        for (var y = 0; y < matrix.Height; y++)
        {
            foreach (var (start, length) in Runs(matrix, y))
            {
                svg.Append(CultureInfo.InvariantCulture, $"M{(start + quiet) * moduleSize} {(y + quiet) * moduleSize}h{length * moduleSize}v{moduleSize}h-{length * moduleSize}z");
            }
        }

        return svg.Append("\"/></svg>").ToString();
    }

    internal static BitMatrix EncodeCode128(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxBarcodeLength || text.Any(c => c is < ' ' or > '~'))
        {
            throw new ValidationException("text", $"Barcodes support 1–{MaxBarcodeLength} printable ASCII characters.");
        }

        return new MultiFormatWriter().encode(text, BarcodeFormat.CODE_128, 0, 1, new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0 });
    }

    internal static BitMatrix EncodeQr(string payload)
    {
        if (string.IsNullOrEmpty(payload) || payload.Length > MaxQrLength)
        {
            throw new ValidationException("payload", $"QR payloads must be 1–{MaxQrLength} characters.");
        }

        return new MultiFormatWriter().encode(payload, BarcodeFormat.QR_CODE, 0, 0, new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.MARGIN] = 0,
            [EncodeHintType.ERROR_CORRECTION] = ErrorCorrectionLevel.M,
            [EncodeHintType.CHARACTER_SET] = "UTF-8",
        });
    }

    private static IEnumerable<(int Start, int Length)> Runs(BitMatrix matrix, int y)
    {
        var x = 0;
        while (x < matrix.Width)
        {
            if (!matrix[x, y])
            {
                x++;
                continue;
            }

            var start = x;
            while (x < matrix.Width && matrix[x, y])
            {
                x++;
            }

            yield return (start, x - start);
        }
    }

    private static string Escape(string text) => SecurityElement.Escape(text) ?? string.Empty;
}

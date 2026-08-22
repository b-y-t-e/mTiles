using Avalonia.Media.Imaging;
using QRCoder;

namespace mTiles.Services.Phone;

/// <summary>Renders a pairing URL as a QR code the panel can show.</summary>
internal static class QrCodeImage
{
    /// <summary>
    /// Black on white, whatever the application's theme is.
    /// </summary>
    /// <remarks>
    /// A QR code drawn in the surrounding dark palette looks better and scans worse: phone cameras
    /// expect dark modules on a light field, and an inverted code is decoded by some readers and not
    /// others. This is the one place in the application that ignores the theme, and it does so because
    /// the thing being drawn is not decoration — it is a machine-readable symbol whose contract predates
    /// this program.
    /// </remarks>
    private static readonly byte[] Dark = [0x00, 0x00, 0x00];
    private static readonly byte[] Light = [0xFF, 0xFF, 0xFF];

    /// <summary>
    /// Error correction level. <c>Q</c> (25%) rather than the usual <c>M</c>, because this code is read
    /// off a screen across a desk, at an angle, often with a reflection on it.
    /// </summary>
    private const QRCodeGenerator.ECCLevel Correction = QRCodeGenerator.ECCLevel.Q;

    public static Bitmap? Render(string url, int pixelsPerModule = 6)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(url, Correction);
            var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule, Dark, Light);

            using var stream = new MemoryStream(png);
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("The QR code could not be drawn: {0}", ex.Message);
            return null;
        }
    }
}

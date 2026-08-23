using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ZXing;
using ZXing.Common;

namespace Stashographer.Services.Lookup;

/// <summary>
/// Decodes barcodes from a still camera image on the server. This is the portable fallback
/// when live browser scanning is unavailable (notably LAN HTTP and Firefox-family browsers).
/// Images stay local and are not persisted.
/// </summary>
public class BarcodeImageDecoder
{
    private const int MaxBytes = 20 * 1024 * 1024;
    private const long MaxPixels = 50_000_000;

    public async Task<string?> DecodeAsync(Stream content, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        var total = 0;
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            total += read;
            if (total > MaxBytes) throw new InvalidDataException("Barcode photo exceeds the 20 MB limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }

        var bytes = buffer.ToArray();
        var info = Image.Identify(bytes) ?? throw new InvalidDataException("The selected file is not a supported image.");
        if ((long)info.Width * info.Height > MaxPixels)
            throw new InvalidDataException("Barcode photo dimensions are too large.");

        using var image = Image.Load<Rgba32>(bytes);
        var pixels = new byte[checked(image.Width * image.Height * 4)];
        image.CopyPixelDataTo(pixels);
        var source = new RGBLuminanceSource(
            pixels, image.Width, image.Height, RGBLuminanceSource.BitmapFormat.RGBA32);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                PossibleFormats = new List<BarcodeFormat>
                {
                    BarcodeFormat.EAN_13,
                    BarcodeFormat.EAN_8,
                    BarcodeFormat.UPC_A,
                    BarcodeFormat.UPC_E,
                    BarcodeFormat.CODE_128,
                    BarcodeFormat.CODE_39,
                    BarcodeFormat.ITF,
                    BarcodeFormat.QR_CODE,
                    BarcodeFormat.DATA_MATRIX
                }
            }
        };

        return reader.Decode(source)?.Text?.Trim() is { Length: > 0 } value ? value : null;
    }
}

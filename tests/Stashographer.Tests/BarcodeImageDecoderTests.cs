using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Stashographer.Services.Inventory;
using Stashographer.Services.Lookup;

namespace Stashographer.Tests;

public class BarcodeImageDecoderTests
{
    [Fact]
    public async Task Decodes_qr_code_from_camera_image_fallback()
    {
        const string payload = "9780262033848";
        var png = ContainerService.GenerateQrPng(payload);

        var decoded = await new BarcodeImageDecoder().DecodeAsync(new MemoryStream(png));

        Assert.Equal(payload, decoded);
    }

    [Fact]
    public async Task Returns_null_when_image_contains_no_barcode()
    {
        using var image = new Image<Rgba32>(240, 160, Color.White);
        using var stream = new MemoryStream();
        await image.SaveAsPngAsync(stream);
        stream.Position = 0;

        var decoded = await new BarcodeImageDecoder().DecodeAsync(stream);

        Assert.Null(decoded);
    }
}

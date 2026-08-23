using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Stashographer.Services.Ai;

namespace Stashographer.Tests;

public class VisionParsingTests
{
    /// <summary>Never invoked — parsing tests only need a constructible service.</summary>
    private sealed class DeadClientProvider : IAiClientProvider
    {
        public bool IsConfigured => false;
        public AiOptions Current => new();
        public IChatClient? GetChatClient() => null;
        public IChatClient? GetVisionClient() => null;
        public void Reconfigure(AiOptions options) { }
    }

    private static OpenAiEnrichmentService Service() =>
        new(new DeadClientProvider(), NullLogger<OpenAiEnrichmentService>.Instance);

    [Fact]
    public void ExtractJson_tolerates_surrounding_prose()
    {
        var json = OpenAiEnrichmentService.ExtractJson("Sure! Here you go: {\"name\":\"X\"} Hope that helps.");
        Assert.Equal("{\"name\":\"X\"}", json);
        Assert.Null(OpenAiEnrichmentService.ExtractJson("no json here"));
        Assert.Null(OpenAiEnrichmentService.ExtractJson(null));
    }

    [Fact]
    public void Identification_parses_fields_normalizes_barcode_and_defaults_count()
    {
        var ident = Service().ParseIdentification("""
            {"name":"Heinz Baked Beans","kind":"Grocery","description":"Tin of beans",
             "attributes":{"Brand":"Heinz"},"price":{"amount":1.25,"currency":"gbp"},
             "barcode":"50-0015 7024671","count":2}
            """);

        Assert.NotNull(ident);
        Assert.Equal("Heinz Baked Beans", ident!.Name);
        Assert.Equal("Grocery", ident.Kind);
        Assert.Equal("5000157024671", ident.Barcode); // digits only
        Assert.Equal(2, ident.Count);
        Assert.Equal("Heinz", ident.Attributes["Brand"]);
        Assert.Equal(1.25m, ident.PriceAmount);
        Assert.Equal("GBP", ident.PriceCurrency);

        var noCount = Service().ParseIdentification("""{"name":"Thing","barcode":"123"}""");
        Assert.Equal(1, noCount!.Count);          // default
        Assert.Null(noCount.Barcode);             // too short → treated as hallucination
    }

    [Fact]
    public void Boxes_parse_and_degenerate_ones_are_discarded()
    {
        var boxes = Service().ParseBoxes("""
            {"items":[
              {"label":"can","box":{"x":0.1,"y":0.2,"w":0.3,"h":0.4}},
              {"label":"degenerate","box":{"x":0.5,"y":0.5,"w":0.0,"h":0.4}},
              {"label":"offscreen","box":{"x":1.5,"y":0.2,"w":0.3,"h":0.4}}
            ]}
            """);

        Assert.Single(boxes);
        Assert.Equal("can", boxes[0].Label);
        Assert.Equal(0.3, boxes[0].W, 3);
    }

    [Fact]
    public void Boxes_accept_array_coordinates_and_common_model_scales()
    {
        var boxes = Service().ParseBoxes("""
            {"items":[
              {"label":"percent","box":[10,20,30,40]},
              {"label":"thousand-scale","box":{"x":500,"y":100,"w":250,"h":300}}
            ]}
            """);

        Assert.Equal(2, boxes.Count);
        Assert.Equal(0.1, boxes[0].X, 3);
        Assert.Equal(0.4, boxes[0].H, 3);
        Assert.Equal(0.5, boxes[1].X, 3);
        Assert.Equal(0.25, boxes[1].W, 3);
    }

    [Fact]
    public void Pick_parses_confidence_and_null_id_means_none()
    {
        var svc = Service();

        var high = svc.ParsePick("""{"matchedItemId":12,"confidence":"high"}""");
        Assert.Equal(12, high!.MatchedItemId);
        Assert.Equal(MatchConfidence.High, high.Confidence);

        var none = svc.ParsePick("""{"matchedItemId":null,"confidence":"high"}""");
        Assert.Null(none!.MatchedItemId);
        Assert.Equal(MatchConfidence.None, none.Confidence); // no id → no confidence

        var medium = svc.ParsePick("""{"matchedItemId":3,"confidence":"medium"}""");
        Assert.Equal(MatchConfidence.Medium, medium!.Confidence);
    }
}

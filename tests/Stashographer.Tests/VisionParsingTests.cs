using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Stashographer.Data.Entities;
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
    public void Identification_preserves_visible_price_without_currency_and_expiry_evidence()
    {
        var ident = Service().ParseIdentification("""
            {"name":"Yoghurt","price":{"amount":1.5,"currency":null},
             "expiry":{"rawText":"BEST BEFORE 03/04/26","date":"2026-04-03",
                       "type":"best_before","confidence":0.91}}
            """);

        Assert.NotNull(ident);
        Assert.Equal(1.5m, ident!.PriceAmount);
        Assert.Null(ident.PriceCurrency);
        Assert.Equal("BEST BEFORE 03/04/26", ident.Expiry!.RawText);
        Assert.Equal(new DateOnly(2026, 4, 3), ident.Expiry.Date);
        Assert.Equal("best_before", ident.Expiry.Type);
        Assert.Equal(0.91m, ident.Expiry.Confidence);
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

    [Fact]
    public void Capture_relationship_parses_exact_instance_verdict_and_image_role()
    {
        var pick = Service().ParseCaptureRelationship("""
            {"queueItemId":42,"relationship":"same_physical","confidence":"high",
             "suggestedRole":"back","reason":"Same corner wear and serial label."}
            """);

        Assert.NotNull(pick);
        Assert.Equal(42, pick!.QueueItemId);
        Assert.Equal(CaptureRelationship.SamePhysicalItem, pick.Relationship);
        Assert.Equal(MatchConfidence.High, pick.Confidence);
        Assert.Equal(ItemImageRole.Back, pick.SuggestedRole);
        Assert.Equal("Same corner wear and serial label.", pick.Reason);
    }

    [Fact]
    public void Receipt_parsing_keeps_visible_values_and_conservative_matches()
    {
        var receipt = Service().ParseReceipt("""
            {
              "merchant":"Example Market",
              "purchaseDate":"2026-08-23",
              "currency":"gbp",
              "total":4.25,
              "lines":[
                {"lineIndex":0,"description":"TOMATOES","quantity":2,
                 "unitPrice":1.25,"lineTotal":2.50,"matchedQueueItemId":12,"confidence":"high"},
                {"lineIndex":0,"description":"BREAD","quantity":0,
                 "lineTotal":1.75,"matchedQueueItemId":null,"confidence":"high"},
                {"description":" ","lineTotal":10}
              ]
            }
            """);

        Assert.NotNull(receipt);
        Assert.Equal("Example Market", receipt!.Merchant);
        Assert.Equal(new DateOnly(2026, 8, 23), receipt.PurchaseDate);
        Assert.Equal("GBP", receipt.Currency);
        Assert.Equal(4.25m, receipt.Total);
        Assert.Equal(2, receipt.Lines.Count);
        Assert.Equal(0, receipt.Lines[0].LineIndex);
        Assert.Equal(1, receipt.Lines[1].LineIndex);
        Assert.Equal(2, receipt.Lines[0].Quantity);
        Assert.Equal(1, receipt.Lines[1].Quantity);
        Assert.Equal(MatchConfidence.None, receipt.Lines[1].Confidence);
    }

    [Fact]
    public void Bom_suggestion_parses_reviewable_requirements_and_safe_defaults()
    {
        var suggestion = Service().ParseBomSuggestion("""
            {
              "name":"Vegetable curry",
              "description":"Serves four",
              "outputQuantity":4,
              "outputUnit":"servings",
              "requirements":[
                {"name":"Chickpeas","quantity":2,"unit":"cans","optional":false,
                 "matchItemKindId":7,"matchText":"chickpeas",
                 "requiredAttributes":{"Form":"canned"}},
                {"name":"Coriander","quantity":-4,"unit":"g","optional":true},
                {"name":" ","quantity":10}
              ]
            }
            """, BomKind.Recipe);

        Assert.NotNull(suggestion);
        Assert.Equal(BomKind.Recipe, suggestion!.Kind);
        Assert.Equal(4, suggestion.OutputQuantity);
        Assert.Equal("servings", suggestion.OutputUnit);
        Assert.Equal(2, suggestion.Requirements.Count);
        Assert.Equal(7, suggestion.Requirements[0].MatchItemKindId);
        Assert.Equal("canned", suggestion.Requirements[0].RequiredAttributes["Form"]);
        Assert.True(suggestion.Requirements[1].IsOptional);
        Assert.Equal(1, suggestion.Requirements[1].Quantity);
    }

    [Fact]
    public void Meal_plan_suggestion_keeps_valid_saved_recipe_references_and_safe_defaults()
    {
        var suggestion = Service().ParseMealPlanSuggestion("""
            {
              "name":"Use the spinach",
              "notes":"Check overdue food before cooking.",
              "entries":[
                {"date":"2026-08-25","mealSlot":"Dinner","bomDefinitionId":12,
                 "outputQuantity":2,"reason":"Uses spinach expiring tomorrow"},
                {"date":"2026-08-26","mealSlot":"Lunch","bomDefinitionId":13,
                 "outputQuantity":-4},
                {"date":"25/08/2026","bomDefinitionId":14},
                {"date":"2026-08-27","bomDefinitionId":0}
              ]
            }
            """);

        Assert.NotNull(suggestion);
        Assert.Equal("Use the spinach", suggestion!.Name);
        Assert.Equal(2, suggestion.Entries.Count);
        Assert.Equal(new DateOnly(2026, 8, 25), suggestion.Entries[0].Date);
        Assert.Equal(12, suggestion.Entries[0].BomDefinitionId);
        Assert.Equal(2, suggestion.Entries[0].OutputQuantity);
        Assert.Equal(1, suggestion.Entries[1].OutputQuantity);
    }
}

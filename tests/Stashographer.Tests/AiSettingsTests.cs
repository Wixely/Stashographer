using Microsoft.Extensions.Logging.Abstractions;
using Stashographer.Services.Ai;
using Stashographer.Services.Config;

namespace Stashographer.Tests;

public class AiSettingsTests
{
    [Fact]
    public async Task Ai_options_roundtrip_through_settings_table()
    {
        await using var db = await TestDb.CreateAsync();
        var svc = new SettingsService(db.Factory);

        // Nothing stored yet → null (config defaults apply).
        Assert.Null(await svc.GetAiOptionsAsync());

        await svc.SaveAiOptionsAsync(new AiOptions
        {
            Enabled = true,
            Endpoint = "http://ollama:11434/v1",
            ApiKey = "ollama",
            Model = "llama3.2-vision",
            VisionModel = null
        });

        var loaded = await svc.GetAiOptionsAsync();
        Assert.NotNull(loaded);
        Assert.True(loaded!.Enabled);
        Assert.Equal("http://ollama:11434/v1", loaded.Endpoint);
        Assert.Equal("ollama", loaded.ApiKey);
        Assert.Equal("llama3.2-vision", loaded.Model);
        Assert.Equal("llama3.2-vision", loaded.EffectiveVisionModel); // falls back to Model
        Assert.True(loaded.IsConfigured);

        // Saving again overwrites (upsert), e.g. disabling.
        await svc.SaveAiOptionsAsync(new AiOptions { Enabled = false, ApiKey = "ollama" });
        Assert.False((await svc.GetAiOptionsAsync())!.Enabled);
    }

    [Fact]
    public void Provider_reconfigures_at_runtime_and_survives_bad_input()
    {
        var provider = new OpenAiClientProvider(NullLogger<OpenAiClientProvider>.Instance);
        Assert.False(provider.IsConfigured);

        // Valid config → clients built (no network needed to construct).
        provider.Reconfigure(new AiOptions { Enabled = true, ApiKey = "sk-test", Model = "gpt-4o-mini" });
        Assert.True(provider.IsConfigured);
        Assert.NotNull(provider.GetChatClient());
        Assert.NotNull(provider.GetVisionClient());

        // Malformed endpoint → falls back to unconfigured instead of throwing.
        provider.Reconfigure(new AiOptions { Enabled = true, ApiKey = "sk-test", Endpoint = "not a url" });
        Assert.False(provider.IsConfigured);
        Assert.Null(provider.GetChatClient());

        // Disabled → unconfigured.
        provider.Reconfigure(new AiOptions { Enabled = false, ApiKey = "sk-test" });
        Assert.False(provider.IsConfigured);
    }

    [Fact]
    public void Enrichment_service_reflects_provider_state()
    {
        var provider = new OpenAiClientProvider(NullLogger<OpenAiClientProvider>.Instance);
        var service = new OpenAiEnrichmentService(provider, NullLogger<OpenAiEnrichmentService>.Instance);

        Assert.False(service.IsEnabled);

        provider.Reconfigure(new AiOptions { Enabled = true, ApiKey = "sk-test" });
        Assert.True(service.IsEnabled); // flips live, no re-registration
    }

    [Fact]
    public async Task Intake_options_default_to_safe_queue_review_and_roundtrip()
    {
        await using var db = await TestDb.CreateAsync();
        var service = new SettingsService(db.Factory);

        var defaults = await service.GetIntakeOptionsAsync();
        Assert.True(defaults.QueueEnabled);
        Assert.True(defaults.AutoProcessBarcodes);
        Assert.True(defaults.AutoProcessPhotos);
        Assert.True(defaults.RequireReview);
        Assert.True(defaults.ContinueLiveScanning);
        Assert.True(defaults.PromptForConsecutiveBarcodeQuantity);
        Assert.Equal(2000, defaults.LiveScanCooldownMilliseconds);

        await service.SaveIntakeOptionsAsync(new IntakeOptions
        {
            QueueEnabled = false,
            AutoProcessBarcodes = false,
            AutoProcessPhotos = false,
            RequireReview = false,
            ContinueLiveScanning = false,
            PromptForConsecutiveBarcodeQuantity = false,
            LiveScanCooldownMilliseconds = 9000,
            ContextItemCount = 99
        });

        var loaded = await service.GetIntakeOptionsAsync();
        Assert.False(loaded.QueueEnabled);
        Assert.False(loaded.AutoProcessBarcodes);
        Assert.False(loaded.AutoProcessPhotos);
        Assert.False(loaded.RequireReview);
        Assert.False(loaded.ContinueLiveScanning);
        Assert.False(loaded.PromptForConsecutiveBarcodeQuantity);
        Assert.Equal(5000, loaded.LiveScanCooldownMilliseconds);
        Assert.Equal(25, loaded.ContextItemCount);
    }

    [Fact]
    public async Task Regional_options_have_local_defaults_and_roundtrip()
    {
        await using var db = await TestDb.CreateAsync();
        var service = new SettingsService(db.Factory);

        var defaults = await service.GetRegionalOptionsAsync();
        Assert.Equal("GBP", defaults.DefaultCurrency);
        Assert.Equal(RegionalDateOrder.DayMonthYear, defaults.DateOrder);
        Assert.Equal("en-GB", defaults.CultureName);

        await service.SaveRegionalOptionsAsync(new InventoryRegionalOptions
        {
            DefaultCurrency = "eur",
            DateOrder = RegionalDateOrder.MonthDayYear,
            CultureName = "en-US",
            TimeZoneId = "UTC"
        });

        var loaded = await service.GetRegionalOptionsAsync();
        Assert.Equal("EUR", loaded.DefaultCurrency);
        Assert.Equal(RegionalDateOrder.MonthDayYear, loaded.DateOrder);
        Assert.Equal("en-US", loaded.CultureName);
        Assert.Equal("UTC", loaded.TimeZoneId);
    }
}

using Dapper;
using System.Globalization;
using Stashographer.Data;
using Stashographer.Data.Entities;
using Stashographer.Services.Ai;

namespace Stashographer.Services.Config;

/// <summary>
/// Key/value app settings persisted in the database so they can be changed from the UI at
/// runtime (no restart, no container rebuild). Once saved here, values take precedence over
/// appsettings/environment configuration.
/// </summary>
public class SettingsService(IDbConnectionFactory db)
{
    private const string AiPrefix = "Ai.";
    private const string IntakePrefix = "Intake.";
    private const string InventoryPrefix = "Inventory.";

    public async Task<Dictionary<string, string>> GetAllAsync(string? prefix = null, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(string Key, string Value)>(
            prefix is null
                ? "SELECT Key, Value FROM Settings"
                : "SELECT Key, Value FROM Settings WHERE Key LIKE @like",
            new { like = $"{prefix}%" });
        return rows.ToDictionary(r => r.Key, r => r.Value);
    }

    public async Task SetManyAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        foreach (var (key, value) in values)
        {
            await conn.ExecuteAsync("""
                INSERT INTO Settings (Key, Value) VALUES (@key, @value)
                ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
                """, new { key, value });
        }
    }

    // --- AI options -----------------------------------------------------------------

    /// <summary>Loads AI options from the DB, or null when nothing has been saved yet.</summary>
    public async Task<AiOptions?> GetAiOptionsAsync(CancellationToken ct = default)
    {
        var stored = await GetAllAsync(AiPrefix, ct);
        if (stored.Count == 0) return null;

        return new AiOptions
        {
            Enabled = stored.TryGetValue("Ai.Enabled", out var e) && bool.TryParse(e, out var b) && b,
            Endpoint = stored.GetValueOrDefault("Ai.Endpoint"),
            ApiKey = stored.GetValueOrDefault("Ai.ApiKey"),
            Model = stored.GetValueOrDefault("Ai.Model") is { Length: > 0 } m ? m : new AiOptions().Model,
            VisionModel = stored.GetValueOrDefault("Ai.VisionModel")
        };
    }

    public Task SaveAiOptionsAsync(AiOptions options, CancellationToken ct = default) =>
        SetManyAsync(new Dictionary<string, string>
        {
            ["Ai.Enabled"] = options.Enabled.ToString(),
            ["Ai.Endpoint"] = options.Endpoint ?? string.Empty,
            ["Ai.ApiKey"] = options.ApiKey ?? string.Empty,
            ["Ai.Model"] = options.Model,
            ["Ai.VisionModel"] = options.VisionModel ?? string.Empty
        }, ct);

    // --- Intake options -------------------------------------------------------------

    public async Task<IntakeOptions> GetIntakeOptionsAsync(CancellationToken ct = default)
    {
        var stored = await GetAllAsync(IntakePrefix, ct);
        var defaults = new IntakeOptions();
        return new IntakeOptions
        {
            QueueEnabled = ReadBool(stored, "Intake.QueueEnabled", defaults.QueueEnabled),
            AutoProcessBarcodes = ReadBool(stored, "Intake.AutoProcessBarcodes", defaults.AutoProcessBarcodes),
            AutoProcessPhotos = ReadBool(stored, "Intake.AutoProcessPhotos", defaults.AutoProcessPhotos),
            RequireReview = ReadBool(stored, "Intake.RequireReview", defaults.RequireReview),
            ContinueLiveScanning = ReadBool(stored, "Intake.ContinueLiveScanning", defaults.ContinueLiveScanning),
            PromptForConsecutiveBarcodeQuantity = ReadBool(
                stored, "Intake.PromptForConsecutiveBarcodeQuantity",
                defaults.PromptForConsecutiveBarcodeQuantity),
            LiveScanCooldownMilliseconds = stored.TryGetValue("Intake.LiveScanCooldownMilliseconds", out var cooldown)
                && int.TryParse(cooldown, out var parsedCooldown)
                    ? Math.Clamp(parsedCooldown, 500, 5000)
                    : defaults.LiveScanCooldownMilliseconds,
            ContextItemCount = stored.TryGetValue("Intake.ContextItemCount", out var count)
                && int.TryParse(count, out var parsed)
                    ? Math.Clamp(parsed, 0, 25)
                    : defaults.ContextItemCount
        };
    }

    public Task SaveIntakeOptionsAsync(IntakeOptions options, CancellationToken ct = default) =>
        SetManyAsync(new Dictionary<string, string>
        {
            ["Intake.QueueEnabled"] = options.QueueEnabled.ToString(),
            ["Intake.AutoProcessBarcodes"] = options.AutoProcessBarcodes.ToString(),
            ["Intake.AutoProcessPhotos"] = options.AutoProcessPhotos.ToString(),
            ["Intake.RequireReview"] = options.RequireReview.ToString(),
            ["Intake.ContinueLiveScanning"] = options.ContinueLiveScanning.ToString(),
            ["Intake.PromptForConsecutiveBarcodeQuantity"] = options.PromptForConsecutiveBarcodeQuantity.ToString(),
            ["Intake.LiveScanCooldownMilliseconds"] = Math.Clamp(
                options.LiveScanCooldownMilliseconds, 500, 5000).ToString(),
            ["Intake.ContextItemCount"] = Math.Clamp(options.ContextItemCount, 0, 25).ToString()
        }, ct);

    // --- Inventory options ----------------------------------------------------------

    public async Task<InventoryRegionalOptions> GetRegionalOptionsAsync(CancellationToken ct = default)
    {
        var stored = await GetAllAsync(InventoryPrefix, ct);
        var defaults = new InventoryRegionalOptions();
        var options = new InventoryRegionalOptions
        {
            DefaultCurrency = stored.GetValueOrDefault("Inventory.DefaultCurrency") ?? defaults.DefaultCurrency,
            DateOrder = stored.TryGetValue("Inventory.DateOrder", out var order)
                        && Enum.TryParse<RegionalDateOrder>(order, out var parsedOrder)
                ? parsedOrder
                : defaults.DateOrder,
            CultureName = stored.GetValueOrDefault("Inventory.CultureName") ?? defaults.CultureName,
            TimeZoneId = stored.GetValueOrDefault("Inventory.TimeZoneId") ?? defaults.TimeZoneId
        };
        try
        {
            ValidateRegionalOptions(options);
            options.DefaultCurrency = SpecialAttributeCatalog.NormalizeCurrencyCode(options.DefaultCurrency);
            return options;
        }
        catch (Exception ex) when (ex is InvalidOperationException or CultureNotFoundException
                                       or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return defaults;
        }
    }

    public async Task<string> GetDefaultCurrencyAsync(CancellationToken ct = default) =>
        (await GetRegionalOptionsAsync(ct)).DefaultCurrency;

    public Task SaveRegionalOptionsAsync(InventoryRegionalOptions options, CancellationToken ct = default)
    {
        ValidateRegionalOptions(options);
        options.DefaultCurrency = SpecialAttributeCatalog.NormalizeCurrencyCode(options.DefaultCurrency);
        return SetManyAsync(new Dictionary<string, string>
        {
            ["Inventory.DefaultCurrency"] = options.DefaultCurrency,
            ["Inventory.DateOrder"] = options.DateOrder.ToString(),
            ["Inventory.CultureName"] = options.CultureName.Trim(),
            ["Inventory.TimeZoneId"] = options.TimeZoneId.Trim()
        }, ct);
    }

    public async Task SaveDefaultCurrencyAsync(string currencyCode, CancellationToken ct = default)
    {
        var options = await GetRegionalOptionsAsync(ct);
        options.DefaultCurrency = currencyCode;
        await SaveRegionalOptionsAsync(options, ct);
    }

    private static void ValidateRegionalOptions(InventoryRegionalOptions options)
    {
        _ = SpecialAttributeCatalog.NormalizeCurrencyCode(options.DefaultCurrency);
        _ = CultureInfo.GetCultureInfo(options.CultureName.Trim());
        _ = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId.Trim());
    }

    private static bool ReadBool(
        IReadOnlyDictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed)
            ? parsed
            : fallback;
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace Stashographer.Data.Entities;

/// <summary>
/// The storage shape for a system-recognized attribute. Unlike ordinary string attributes,
/// these values keep their machine-readable type so they can be sorted and aggregated.
/// </summary>
public sealed class SpecialAttributeValue
{
    public decimal? DecimalValue { get; set; }
    public string? TextValue { get; set; }
    public string? CurrencyCode { get; set; }
    public DateOnly? DateValue { get; set; }
    public string? Qualifier { get; set; }
    public SpecialAttributeEvidence? Evidence { get; set; }
}

public sealed class SpecialAttributeEvidence
{
    public string Source { get; set; } = "user";
    public int? SourceImageId { get; set; }
    public string? RawText { get; set; }
    public decimal? Confidence { get; set; }
    public string? Convention { get; set; }
    public List<string> Assumptions { get; set; } = new();
}

public enum ExpiryDateKind
{
    Unknown,
    UseBy,
    BestBefore
}

public enum SpecialAttributeValueKind
{
    Money,
    Number,
    Text,
    Date
}

/// <summary>Metadata for a special attribute whose stable key is used by application features.</summary>
public sealed record SpecialAttributeDefinition(
    string Key,
    string DisplayName,
    SpecialAttributeValueKind ValueKind,
    string Description);

/// <summary>
/// Registry of attributes with application behavior. Display labels may evolve, but keys are
/// stable persistence/API identifiers. New special attributes should be registered here.
/// </summary>
public static class SpecialAttributeCatalog
{
    public const string PriceKey = "price";
    public const string ExpiryKey = "expiry";

    public static readonly SpecialAttributeDefinition Price = new(
        PriceKey,
        "Unit price",
        SpecialAttributeValueKind.Money,
        "Price for one unit of the item, stored with its ISO currency code.");

    public static readonly SpecialAttributeDefinition Expiry = new(
        ExpiryKey,
        "Expiry",
        SpecialAttributeValueKind.Date,
        "Operational use-by or best-before date, backed by the indexed item expiry column.");

    public static IReadOnlyList<SpecialAttributeDefinition> All { get; } = [Price, Expiry];

    private static readonly HashSet<string> PriceAliases =
        ["price", "unitprice", "cost", "unitcost", "purchaseprice"];
    private static readonly HashSet<string> ExpiryAliases =
        ["expiry", "expirydate", "expiration", "expirationdate", "useby", "bestbefore", "bbe"];

    private static readonly Regex MoneyPattern = new(
        @"^\s*(?:(?<symbol>[£€$¥])|(?<prefix>[A-Za-z]{3})\s*)?" +
        @"(?<amount>[0-9]+(?:,[0-9]{3})*(?:\.[0-9]+)?)" +
        @"(?:\s*(?<suffix>[A-Za-z]{3}))?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static SpecialAttributeValue? GetPrice(Item item) =>
        item.SpecialAttributes.GetValueOrDefault(PriceKey) is { DecimalValue: not null } value
            ? value
            : null;

    public static SpecialAttributeValue? GetExpiry(Item item) =>
        item.SpecialAttributes.GetValueOrDefault(ExpiryKey) is { DateValue: not null } value
            ? value
            : item.ExpiryDate is { } date
                ? new SpecialAttributeValue { DateValue = date, Qualifier = ExpiryDateKind.Unknown.ToString() }
                : null;

    public static void SetPrice(
        Item item, decimal? amount, string? currencyCode, SpecialAttributeEvidence? evidence = null)
    {
        if (amount is null)
        {
            item.SpecialAttributes.Remove(PriceKey);
            return;
        }

        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), "Price cannot be negative.");
        item.SpecialAttributes[PriceKey] = new SpecialAttributeValue
        {
            DecimalValue = amount,
            CurrencyCode = NormalizeCurrencyCode(currencyCode),
            Evidence = evidence
        };
    }

    public static void SetExpiry(
        Item item, DateOnly? date, ExpiryDateKind kind = ExpiryDateKind.Unknown,
        SpecialAttributeEvidence? evidence = null)
    {
        item.ExpiryDate = date;
        if (date is null)
        {
            item.SpecialAttributes.Remove(ExpiryKey);
            return;
        }
        item.SpecialAttributes[ExpiryKey] = new SpecialAttributeValue
        {
            DateValue = date,
            Qualifier = kind.ToString(),
            Evidence = evidence
        };
    }

    /// <summary>
    /// Adds newly observed special values without replacing reviewed data already on the item.
    /// Returns true when the target changed.
    /// </summary>
    public static bool MergeMissing(Item target, Item observed)
    {
        var changed = false;
        if (GetPrice(target) is null && GetPrice(observed) is { } price)
        {
            SetPrice(target, price.DecimalValue, price.CurrencyCode, price.Evidence);
            changed = true;
        }

        if (GetExpiry(target) is null && GetExpiry(observed) is { DateValue: { } date } expiry)
        {
            var kind = Enum.TryParse<ExpiryDateKind>(expiry.Qualifier, out var parsed)
                ? parsed
                : ExpiryDateKind.Unknown;
            SetExpiry(target, date, kind, expiry.Evidence);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Applies an explicit exchange rate. The rate is target-currency units per one source-
    /// currency unit; rate acquisition remains a separate concern so stale rates are never
    /// silently treated as current.
    /// </summary>
    public static SpecialAttributeValue ConvertPrice(
        SpecialAttributeValue source, string targetCurrencyCode, decimal targetUnitsPerSourceUnit)
    {
        if (source.DecimalValue is null) throw new ArgumentException("The source has no price.", nameof(source));
        _ = NormalizeCurrencyCode(source.CurrencyCode);
        if (targetUnitsPerSourceUnit <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetUnitsPerSourceUnit), "Exchange rate must be positive.");
        return new SpecialAttributeValue
        {
            DecimalValue = decimal.Round(source.DecimalValue.Value * targetUnitsPerSourceUnit, 2,
                MidpointRounding.AwayFromZero),
            CurrencyCode = NormalizeCurrencyCode(targetCurrencyCode)
        };
    }

    public static string FormatPrice(SpecialAttributeValue? value)
    {
        if (value?.DecimalValue is not { } amount) return string.Empty;
        var currency = NormalizeCurrencyCode(value.CurrencyCode);
        var symbol = currency switch
        {
            "GBP" => "£",
            "USD" => "$",
            "EUR" => "€",
            "JPY" => "¥",
            _ => currency + " "
        };
        return symbol + amount.ToString("0.00", CultureInfo.InvariantCulture);
    }

    public static void Normalize(Item item)
    {
        if (item.SpecialAttributes.TryGetValue(PriceKey, out var price))
        {
            if (price.DecimalValue is null)
                item.SpecialAttributes.Remove(PriceKey);
            else
            {
                if (price.DecimalValue < 0) throw new InvalidOperationException("Price cannot be negative.");
                price.CurrencyCode = NormalizeCurrencyCode(price.CurrencyCode);
            }
        }

        if (item.ExpiryDate is { } date)
        {
            if (!item.SpecialAttributes.TryGetValue(ExpiryKey, out var expiry))
                item.SpecialAttributes[ExpiryKey] = new SpecialAttributeValue
                {
                    DateValue = date,
                    Qualifier = ExpiryDateKind.Unknown.ToString()
                };
            else
                expiry.DateValue = date;
        }
        else if (item.SpecialAttributes.TryGetValue(ExpiryKey, out var expiry))
        {
            if (expiry.DateValue is { } specialDate) item.ExpiryDate = specialDate;
            else item.SpecialAttributes.Remove(ExpiryKey);
        }
    }

    /// <summary>
    /// Moves recognized legacy/AI price strings out of the ordinary attribute bag when they
    /// can be parsed without guessing. Unparseable values remain untouched for user review.
    /// </summary>
    public static void PromoteFromOrdinaryAttributes(Item item)
    {
        if (GetPrice(item) is not null) return;
        foreach (var (name, text) in item.Attributes.ToList())
        {
            if (!IsReservedName(name) || !TryParsePrice(text, out var amount, out var currency)) continue;
            SetPrice(item, amount, currency);
            item.Attributes.Remove(name);
            return;
        }
    }

    public static bool IsReservedName(string? name)
    {
        var key = new string((name ?? string.Empty).Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant).ToArray());
        return PriceAliases.Contains(key) || ExpiryAliases.Contains(key);
    }

    public static bool TryParsePrice(string? text, out decimal amount, out string currencyCode)
    {
        amount = 0;
        currencyCode = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = MoneyPattern.Match(text);
        if (!match.Success || !decimal.TryParse(match.Groups["amount"].Value,
                NumberStyles.Number, CultureInfo.InvariantCulture, out amount) || amount < 0)
            return false;

        currencyCode = match.Groups["prefix"].Value;
        if (currencyCode.Length == 0) currencyCode = match.Groups["suffix"].Value;
        if (currencyCode.Length == 0)
        {
            currencyCode = match.Groups["symbol"].Value switch
            {
                "£" => "GBP",
                "€" => "EUR",
                "$" => "USD",
                "¥" => "JPY",
                _ => string.Empty
            };
        }
        if (currencyCode.Length != 3) return false;
        currencyCode = currencyCode.ToUpperInvariant();
        return true;
    }

    public static string NormalizeCurrencyCode(string? code)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length != 3 || !normalized.All(c => c is >= 'A' and <= 'Z'))
            throw new InvalidOperationException("Use a three-letter ISO currency code such as GBP, EUR, or USD.");
        return normalized;
    }
}

namespace Stashographer.Services.Lookup;

public enum CodeKind
{
    Unknown,
    Isbn,
    ProductBarcode
}

/// <summary>
/// Classifies a scanned code so the router can pick a provider. Books are EAN-13 codes in
/// the Bookland range (prefix 978/979) or bare ISBN-10; everything else is treated as a
/// product barcode (UPC-A / EAN-13).
/// </summary>
public static class CodeClassifier
{
    /// <summary>Strips spaces/hyphens and uppercases the ISBN-10 check character.</summary>
    public static string Normalize(string code) =>
        new string((code ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray())
            .ToUpperInvariant();

    public static CodeKind Classify(string code)
    {
        var c = Normalize(code);
        if (c.Length == 0) return CodeKind.Unknown;

        if (IsIsbn13(c) || IsIsbn10(c)) return CodeKind.Isbn;

        // UPC-A (12) or other EAN-13 numeric barcodes.
        if ((c.Length == 12 || c.Length == 13) && c.All(char.IsDigit))
            return CodeKind.ProductBarcode;

        return CodeKind.Unknown;
    }

    public static bool IsIsbn13(string code)
    {
        var c = Normalize(code);
        if (c.Length != 13 || !c.All(char.IsDigit)) return false;
        if (!c.StartsWith("978") && !c.StartsWith("979")) return false;

        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (c[i] - '0') * (i % 2 == 0 ? 1 : 3);
        var check = (10 - sum % 10) % 10;
        return check == c[12] - '0';
    }

    public static bool IsIsbn10(string code)
    {
        var c = Normalize(code);
        if (c.Length != 10) return false;

        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            if (!char.IsDigit(c[i])) return false;
            sum += (c[i] - '0') * (10 - i);
        }
        var last = c[9];
        var checkVal = last == 'X' ? 10 : char.IsDigit(last) ? last - '0' : -1;
        if (checkVal < 0) return false;
        sum += checkVal;
        return sum % 11 == 0;
    }
}

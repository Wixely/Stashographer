using Stashographer.Services.Lookup;

namespace Stashographer.Tests;

public class CodeClassifierTests
{
    [Theory]
    [InlineData("9780262033848")]   // ISBN-13 (SICP), 978 prefix
    [InlineData("978-0-262-03384-8")] // hyphenated
    [InlineData("9791234567896")]    // 979 prefix, valid check digit
    public void Classifies_valid_isbn13_as_isbn(string code)
    {
        Assert.True(CodeClassifier.IsIsbn13(code));
        Assert.Equal(CodeKind.Isbn, CodeClassifier.Classify(code));
    }

    [Theory]
    [InlineData("0262033844")]  // ISBN-10 (SICP)
    [InlineData("020161622X")]  // ISBN-10 ending in X check digit
    public void Classifies_valid_isbn10_as_isbn(string code)
    {
        Assert.True(CodeClassifier.IsIsbn10(code));
        Assert.Equal(CodeKind.Isbn, CodeClassifier.Classify(code));
    }

    [Theory]
    [InlineData("5449000000996")] // Coca-Cola EAN-13 (not Bookland)
    [InlineData("036000291452")]  // UPC-A (12 digits)
    public void Classifies_product_barcodes(string code)
    {
        Assert.Equal(CodeKind.ProductBarcode, CodeClassifier.Classify(code));
    }

    [Theory]
    [InlineData("9780262033840")] // ISBN-13 with wrong check digit
    [InlineData("0262033840")]    // ISBN-10 with wrong check digit
    public void Rejects_bad_check_digits(string code)
    {
        Assert.False(CodeClassifier.IsIsbn13(code) && code.Length == 13);
        Assert.False(CodeClassifier.IsIsbn10(code) && code.Length == 10);
    }

    [Fact]
    public void Normalize_strips_spaces_and_hyphens_and_uppercases()
    {
        Assert.Equal("020161622X", CodeClassifier.Normalize("0-201 616 22x"));
    }

    [Fact]
    public void Empty_code_is_unknown()
    {
        Assert.Equal(CodeKind.Unknown, CodeClassifier.Classify(""));
    }
}

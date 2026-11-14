namespace Solvix.UnitTests.SharedKernel;

using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Tenancy;

public sealed class SlugTests
{
    [Theory]
    [InlineData("acme", "acme")]
    [InlineData("  Acme-Corp ", "acme-corp")]
    [InlineData("a1-b2-c3", "a1-b2-c3")]
    public void Normalize_accepts_valid_slugs(string input, string expected) =>
        Assert.Equal(expected, Slug.Normalize(input));

    [Theory]
    [InlineData("ab")]
    [InlineData("-acme")]
    [InlineData("acme-")]
    [InlineData("acme--corp")]
    [InlineData("acme corp")]
    [InlineData("acme_corp")]
    [InlineData("api")]
    [InlineData("admin")]
    public void Normalize_rejects_invalid_or_reserved_slugs(string input) =>
        Assert.Throws<ValidationException>(() => Slug.Normalize(input));

    [Theory]
    [InlineData("Acme Corporation", "acme-corporation")]
    [InlineData("  Über & Söhne GmbH ", "ber-s-hne-gmbh")]
    [InlineData("A", "a-org")]
    [InlineData("API", "api-org")]
    public void FromName_derives_a_valid_slug(string name, string expected)
    {
        var slug = Slug.FromName(name);
        Assert.Equal(expected, slug);
        Assert.True(Slug.IsValid(slug));
    }

    [Fact]
    public void FromName_truncates_to_max_length()
    {
        var slug = Slug.FromName(new string('a', 200) + " " + new string('b', 20));
        Assert.True(slug.Length <= Slug.MaxLength);
        Assert.True(Slug.IsValid(slug));
    }
}

namespace Solvix.UnitTests.Assets;

using Solvix.SharedKernel.Endpoints;

public sealed class HardwareIdentityTests
{
    [Theory]
    [InlineData(" 5cd1234xyz ", "5CD1234XYZ")]
    [InlineData("ABC-123", "ABC-123")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("To be filled by O.E.M.", null)]
    [InlineData("System Serial Number", null)]
    [InlineData("Default string", null)]
    [InlineData("0000000000", null)]
    [InlineData("FFFFFFFF", null)]
    [InlineData("N/A", null)]
    [InlineData("12", null)]
    public void Serial_numbers_are_normalised_and_placeholders_rejected(string? input, string? expected) =>
        Assert.Equal(expected, HardwareIdentity.NormalizeSerial(input));

    [Theory]
    [InlineData("Dell Inc.", "dell")]
    [InlineData("DELL", "dell")]
    [InlineData("Hewlett-Packard", "hp")]
    [InlineData("HP Inc.", "hp")]
    [InlineData("HP", "hp")]
    [InlineData("Hewlett Packard Enterprise", "hpe")]
    [InlineData("LENOVO", "lenovo")]
    [InlineData("Microsoft Corporation", "microsoft")]
    [InlineData("ASUSTeK COMPUTER INC.", "asus")]
    [InlineData("Zebra Technologies", "zebra")]
    [InlineData("Cisco Systems, Inc.", "cisco")]
    [InlineData("System manufacturer", null)]
    [InlineData("  ", null)]
    public void Manufacturers_are_normalised_with_corporate_suffixes_and_aliases(string? input, string? expected) =>
        Assert.Equal(expected, HardwareIdentity.NormalizeManufacturer(input));

    [Theory]
    [InlineData("Latitude 7440", "latitude7440")]
    [InlineData("LATITUDE-7440", "latitude7440")]
    [InlineData("System Product Name", null)]
    public void Models_compare_on_letters_and_digits(string? input, string? expected) =>
        Assert.Equal(expected, HardwareIdentity.NormalizeModel(input));
}

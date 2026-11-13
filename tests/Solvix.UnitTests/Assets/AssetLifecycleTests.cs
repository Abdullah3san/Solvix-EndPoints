namespace Solvix.UnitTests.Assets;

using Solvix.Modules.Assets.Application;
using Solvix.Modules.Assets.Domain;
using Solvix.Modules.Assets.Labels;
using Solvix.SharedKernel.Exceptions;
using ZXing;
using ZXing.Common;

public sealed class AssetLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    private static Asset NewAsset() => Asset.Register(Guid.NewGuid(), "ast-lap-000001", Details(), null, null);

    private static AssetDetails Details(string? serial = "SN-1") => new(
        "Laptop", AssetCategory.Laptop, AssetCondition.New, "Dell Inc.", "Latitude", serial, null, null, null,
        null, null, null, null, null, null, null, null, null, DepreciationMethod.None, null, null);

    [Fact]
    public void Registration_normalises_tag_and_hardware_keys()
    {
        var asset = NewAsset();
        Assert.Equal("AST-LAP-000001", asset.AssetTag);
        Assert.Equal(AssetStatus.InStock, asset.Status);
        Assert.Equal("dell", asset.ManufacturerKey);
        Assert.Equal("SN-1", asset.SerialNumberKey);
        Assert.Throws<ValidationException>(() => Asset.NormalizeTag("has space"));
    }

    [Fact]
    public void Assignment_return_and_maintenance_follow_the_state_machine()
    {
        var asset = NewAsset();
        asset.Assign(Assignee.Create(null, "Ali", "E-1"), Now);
        Assert.Equal(AssetStatus.InUse, asset.Status);

        asset.StartMaintenance();
        Assert.Equal(AssetStatus.InMaintenance, asset.Status);
        Assert.Throws<ConflictException>(() => asset.Assign(Assignee.Create(null, "Other", null), Now));
        Assert.Throws<ConflictException>(() => asset.Retire("x", Now));
        asset.EndMaintenance();
        Assert.Equal(AssetStatus.InUse, asset.Status);

        asset.Return(AssetCondition.Fair);
        Assert.Equal(AssetStatus.InStock, asset.Status);
        Assert.False(asset.IsAssigned);
        Assert.Equal(AssetCondition.Fair, asset.Condition);
        Assert.Throws<ConflictException>(() => asset.Return(null));
    }

    [Fact]
    public void Returning_during_maintenance_lands_in_stock_afterwards()
    {
        var asset = NewAsset();
        asset.Assign(Assignee.Create(null, "Ali", null), Now);
        asset.StartMaintenance();
        asset.Return(null);
        asset.EndMaintenance();
        Assert.Equal(AssetStatus.InStock, asset.Status);
    }

    [Fact]
    public void Retirement_and_disposal_end_the_lifecycle()
    {
        var asset = NewAsset();
        asset.Assign(Assignee.Create(null, "Ali", null), Now);
        Assert.Throws<ConflictException>(() => asset.RecordDisposal(DisposalMethod.Recycled, Now, null, null, null));
        asset.Retire("Broken screen", Now);
        Assert.Equal(AssetStatus.Retired, asset.Status);
        Assert.False(asset.IsAssigned);

        asset.RecordDisposal(DisposalMethod.Recycled, Now.AddDays(1), 0m, "CERT-1", null);
        Assert.Equal(AssetStatus.Disposed, asset.Status);
        Assert.Equal("asset_disposed", Assert.Throws<ConflictException>(() => asset.Update(Details())).Code);
        Assert.Throws<ConflictException>(() => asset.Transfer(Guid.NewGuid(), null, null));
        Assert.Throws<ConflictException>(() => asset.Reinstate());
    }

    [Fact]
    public void Missing_assets_can_be_found_again()
    {
        var asset = NewAsset();
        asset.MarkMissing();
        Assert.Throws<ConflictException>(() => asset.Assign(Assignee.Create(null, "x", null), Now));
        asset.MarkFound(Now);
        Assert.Equal(AssetStatus.InStock, asset.Status);
        Assert.Equal(Now, asset.LastAuditedAtUtc);
    }

    [Fact]
    public void Assignee_requires_a_member_or_a_name()
    {
        Assert.Throws<ValidationException>(() => Assignee.Create(null, " ", null));
        var member = Assignee.Create(Guid.NewGuid(), "ignored", "E-1");
        Assert.Null(member.Name);
        Assert.Equal("E-1", member.EmployeeNumber);
    }

    [Theory]
    [InlineData(null, WarrantyState.None)]
    [InlineData(-1, WarrantyState.Expired)]
    [InlineData(0, WarrantyState.ExpiringSoon)]
    [InlineData(60, WarrantyState.ExpiringSoon)]
    [InlineData(61, WarrantyState.Active)]
    public void Warranty_state_is_derived_from_the_end_date(int? daysFromToday, WarrantyState expected)
    {
        var today = new DateOnly(2026, 11, 12);
        Assert.Equal(expected, Asset.WarrantyStateOn(daysFromToday is { } d ? today.AddDays(d) : null, today));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=1+2", "'=1+2")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("-5", "'-5")]
    [InlineData(null, "")]
    public void Csv_cells_are_escaped_and_formulas_neutralised(string? input, string expected) =>
        Assert.Equal(expected, Csv.Escape(input));

    [Theory]
    [InlineData("AST-LAP-000001")]
    [InlineData("ACME-HHD-123456")]
    public void Code128_round_trips_through_a_decoder(string text)
    {
        var matrix = LabelRenderer.EncodeCode128(text);
        Assert.Equal(text, Decode(matrix, BarcodeFormat.CODE_128, scale: 3, rows: 40));
        var svg = LabelRenderer.Code128Svg(text);
        Assert.Contains($">{text}</text>", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Qr_code_round_trips_through_a_decoder()
    {
        const string payload = "https://endpoints.example.com/assets/01a1d1f5-8877-7e4e-a9a2-1425bf7d251b";
        Assert.Equal(payload, Decode(LabelRenderer.EncodeQr(payload), BarcodeFormat.QR_CODE, scale: 6, rows: null));
        Assert.StartsWith("<svg", LabelRenderer.QrSvg(payload), StringComparison.Ordinal);
    }

    [Fact]
    public void Label_text_is_escaped_and_validated()
    {
        Assert.Throws<ValidationException>(() => LabelRenderer.Code128Svg("tab\there"));
        Assert.Throws<ValidationException>(() => LabelRenderer.QrSvg(new string('x', LabelRenderer.MaxQrLength + 1)));
        Assert.Contains("&lt;b&gt;", LabelRenderer.Code128Svg("<b>"), StringComparison.Ordinal);
    }

    /// <summary>Rasterises the module matrix (with quiet zone) to grey-scale and decodes it with ZXing's reader.</summary>
    private static string? Decode(BitMatrix matrix, BarcodeFormat format, int scale, int? rows)
    {
        const int quiet = 10;
        var height = rows ?? matrix.Height;
        var width = (matrix.Width + (2 * quiet)) * scale;
        var pixelHeight = (height + (2 * quiet)) * scale;
        var luminance = new byte[width * pixelHeight];
        Array.Fill(luminance, (byte)255);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < matrix.Width; x++)
            {
                if (!matrix[x, rows is null ? y : 0])
                {
                    continue;
                }

                for (var dy = 0; dy < scale; dy++)
                {
                    for (var dx = 0; dx < scale; dx++)
                    {
                        luminance[(((y + quiet) * scale) + dy) * width + ((x + quiet) * scale) + dx] = 0;
                    }
                }
            }
        }

        var source = new PlanarYUVLuminanceSource(luminance, width, pixelHeight, 0, 0, width, pixelHeight, false);
        var result = new MultiFormatReader().decode(
            new BinaryBitmap(new HybridBinarizer(source)),
            new Dictionary<DecodeHintType, object> { [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> { format }, [DecodeHintType.TRY_HARDER] = true });
        return result?.Text;
    }
}

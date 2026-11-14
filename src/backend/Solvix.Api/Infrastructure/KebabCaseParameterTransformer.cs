namespace Solvix.Api.Infrastructure;

using System.Text.RegularExpressions;

/// <summary>Turns "CompanyProfile" into "company-profile" for route tokens.</summary>
internal sealed partial class KebabCaseParameterTransformer : IOutboundParameterTransformer
{
    public string? TransformOutbound(object? value) =>
        value is null ? null : Boundary().Replace(value.ToString()!, "$1-$2").ToLowerInvariant();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex Boundary();
}

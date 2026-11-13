namespace Solvix.Modules.Mdm.Providers.Android;

using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

/// <summary>REST client for the Android Management API v1.</summary>
internal sealed class HttpAndroidManagementApi(HttpClient http, IGoogleAccessTokenProvider tokens, IOptions<MdmOptions> options) : IAndroidManagementApi
{
    public async Task<AndroidSignupUrl> CreateSignupUrlAsync(string projectId, string callbackUrl, CancellationToken cancellationToken)
    {
        var query = $"signupUrls?projectId={Uri.EscapeDataString(projectId)}&callbackUrl={Uri.EscapeDataString(callbackUrl)}";
        var json = await SendAsync(HttpMethod.Post, query, null, cancellationToken);
        return new AndroidSignupUrl(Required(json, "name"), Required(json, "url"));
    }

    public async Task<AndroidEnterpriseInfo> CreateEnterpriseAsync(string projectId, string signupUrlName, string enterpriseToken, string displayName, CancellationToken cancellationToken)
    {
        var query = $"enterprises?projectId={Uri.EscapeDataString(projectId)}&signupUrlName={Uri.EscapeDataString(signupUrlName)}&enterpriseToken={Uri.EscapeDataString(enterpriseToken)}";
        var body = new JsonObject { ["enterpriseDisplayName"] = displayName };
        var json = await SendAsync(HttpMethod.Post, query, body, cancellationToken);
        return new AndroidEnterpriseInfo(Required(json, "name"), json.GetPropertyOrNull("enterpriseDisplayName")?.GetString());
    }

    public async Task<AndroidEnrollmentTokenInfo> CreateEnrollmentTokenAsync(string enterpriseName, JsonObject token, CancellationToken cancellationToken)
    {
        var json = await SendAsync(HttpMethod.Post, $"{enterpriseName}/enrollmentTokens", token, cancellationToken);
        DateTimeOffset? expires = DateTimeOffset.TryParse(json.GetPropertyOrNull("expirationTimestamp")?.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var e) ? e : null;
        return new AndroidEnrollmentTokenInfo(Required(json, "name"), Required(json, "value"), json.GetPropertyOrNull("qrCode")?.GetString(), expires);
    }

    public async Task PatchPolicyAsync(string policyName, JsonObject policy, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Patch, policyName, policy, cancellationToken);

    public async Task<IReadOnlyList<AndroidDeviceInfo>> ListDevicesAsync(string enterpriseName, CancellationToken cancellationToken)
    {
        var devices = new List<AndroidDeviceInfo>();
        string? pageToken = null;
        do
        {
            var url = $"{enterpriseName}/devices?pageSize=100" + (pageToken is null ? string.Empty : $"&pageToken={Uri.EscapeDataString(pageToken)}");
            var json = await SendAsync(HttpMethod.Get, url, null, cancellationToken);
            if (json.GetPropertyOrNull("devices") is { ValueKind: JsonValueKind.Array } array)
            {
                devices.AddRange(array.EnumerateArray().Select(ParseDevice));
            }

            pageToken = json.GetPropertyOrNull("nextPageToken")?.GetString();
        }
        while (!string.IsNullOrEmpty(pageToken));
        return devices;
    }

    public async Task PatchDeviceAsync(string deviceName, JsonObject patch, string updateMask, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Patch, $"{deviceName}?updateMask={Uri.EscapeDataString(updateMask)}", patch, cancellationToken);

    public async Task<string> IssueCommandAsync(string deviceName, JsonObject command, CancellationToken cancellationToken)
    {
        var json = await SendAsync(HttpMethod.Post, $"{deviceName}:issueCommand", command, cancellationToken);
        return Required(json, "name");
    }

    public async Task<AndroidOperationInfo> GetOperationAsync(string operationName, CancellationToken cancellationToken)
    {
        var json = await SendAsync(HttpMethod.Get, operationName, null, cancellationToken);
        var done = json.GetPropertyOrNull("done") is { ValueKind: JsonValueKind.True };
        var error = json.GetPropertyOrNull("error")?.GetPropertyOrNull("message")?.GetString();
        return new AndroidOperationInfo(done, error);
    }

    public async Task DeleteDeviceAsync(string deviceName, IReadOnlyList<string> wipeDataFlags, string? wipeReasonMessage, CancellationToken cancellationToken)
    {
        var query = string.Join('&', wipeDataFlags.Select(f => $"wipeDataFlags={Uri.EscapeDataString(f)}")
            .Concat(string.IsNullOrWhiteSpace(wipeReasonMessage) ? [] : [$"wipeReasonMessage={Uri.EscapeDataString(wipeReasonMessage)}"]));
        await SendAsync(HttpMethod.Delete, query.Length == 0 ? deviceName : $"{deviceName}?{query}", null, cancellationToken);
    }

    internal static AndroidDeviceInfo ParseDevice(JsonElement e)
    {
        var hardware = e.GetPropertyOrNull("hardwareInfo");
        var software = e.GetPropertyOrNull("softwareInfo");
        var settings = e.GetPropertyOrNull("deviceSettings");
        var apps = e.GetPropertyOrNull("applicationReports") is { ValueKind: JsonValueKind.Array } reports
            ? reports.EnumerateArray()
                .Where(r => r.GetPropertyOrNull("packageName") is not null)
                .Select(r => new AndroidAppReport(
                    r.GetProperty("packageName").GetString()!,
                    r.GetPropertyOrNull("displayName")?.GetString(),
                    r.GetPropertyOrNull("versionName")?.GetString(),
                    r.GetPropertyOrNull("applicationSource")?.GetString()))
                .ToList()
            : null;
        return new AndroidDeviceInfo
        {
            Name = e.GetProperty("name").GetString()!,
            State = e.GetPropertyOrNull("state")?.GetString(),
            ManagementMode = e.GetPropertyOrNull("managementMode")?.GetString(),
            Ownership = e.GetPropertyOrNull("ownership")?.GetString(),
            EnrollmentTokenData = e.GetPropertyOrNull("enrollmentTokenData")?.GetString(),
            AppliedPolicyName = e.GetPropertyOrNull("appliedPolicyName")?.GetString(),
            AppliedPolicyVersion = int.TryParse(e.GetPropertyOrNull("appliedPolicyVersion")?.ToString(), out var v) ? v : null,
            PolicyCompliant = e.GetPropertyOrNull("policyCompliant") is { ValueKind: JsonValueKind.True or JsonValueKind.False } c ? c.GetBoolean() : null,
            LastStatusReportTime = DateTimeOffset.TryParse(e.GetPropertyOrNull("lastStatusReportTime")?.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null,
            LastPolicySyncTime = DateTimeOffset.TryParse(e.GetPropertyOrNull("lastPolicySyncTime")?.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ps) ? ps : null,
            Manufacturer = hardware?.GetPropertyOrNull("manufacturer")?.GetString(),
            Brand = hardware?.GetPropertyOrNull("brand")?.GetString(),
            Model = hardware?.GetPropertyOrNull("model")?.GetString(),
            SerialNumber = hardware?.GetPropertyOrNull("serialNumber")?.GetString(),
            AndroidVersion = software?.GetPropertyOrNull("androidVersion")?.GetString(),
            SecurityPatchLevel = software?.GetPropertyOrNull("securityPatchLevel")?.GetString(),
            IsEncrypted = settings?.GetPropertyOrNull("isEncrypted") is { ValueKind: JsonValueKind.True or JsonValueKind.False } enc ? enc.GetBoolean() : null,
            IsDeviceSecure = settings?.GetPropertyOrNull("isDeviceSecure") is { ValueKind: JsonValueKind.True or JsonValueKind.False } sec ? sec.GetBoolean() : null,
            DevicePosture = e.GetPropertyOrNull("securityPosture")?.GetPropertyOrNull("devicePosture")?.GetString(),
            Applications = apps,
        };
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string relativeUrl, JsonObject? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri(options.Value.Android.ApiBaseUrl), relativeUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAccessTokenAsync(cancellationToken));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new AndroidManagementApiException((int)response.StatusCode, Truncate(text));
        }

        return string.IsNullOrWhiteSpace(text) ? JsonDocument.Parse("{}").RootElement.Clone() : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static string Required(JsonElement json, string name) =>
        json.GetPropertyOrNull(name)?.GetString() ?? throw new AndroidManagementApiException(502, $"Android Management API response is missing '{name}'.");

    private static string Truncate(string s) => s.Length <= 1000 ? s : s[..1000];
}

internal static class JsonElementExtensions
{
    public static JsonElement? GetPropertyOrNull(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;
}

public sealed class AndroidManagementApiException : Exception
{
    public AndroidManagementApiException(int statusCode, string message)
        : base($"Android Management API error {statusCode}: {message}")
    {
        StatusCode = statusCode;
    }

    public AndroidManagementApiException()
    {
    }

    public AndroidManagementApiException(string message)
        : base(message)
    {
    }

    public AndroidManagementApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public int StatusCode { get; }
}

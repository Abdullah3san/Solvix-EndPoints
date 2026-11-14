namespace Solvix.IntegrationTests.Fixtures;

using System.Net;
using System.Text.Json;

public static class HttpAssert
{
    public static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode expectedStatus, string? expectedCode = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expectedStatus, $"Expected {(int)expectedStatus} but got {(int)response.StatusCode}: {body}");
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType?.MediaType ?? string.Empty, StringComparison.Ordinal);
        var problem = JsonDocument.Parse(body).RootElement;
        if (expectedCode is not null)
        {
            Assert.Equal(expectedCode, problem.GetProperty("code").GetString());
        }

        return problem;
    }

    public static async Task<T> OkAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        ArgumentNullException.ThrowIfNull(response);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected} but got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, ApiFixture.Json)!;
    }
}

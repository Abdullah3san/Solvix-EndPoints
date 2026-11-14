namespace Solvix.Modules.Assistant.Knowledge.Embeddings;

using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Solvix.Modules.Assistant.Configuration;
using Solvix.SharedKernel.Exceptions;

public interface IEmbeddingProvider
{
    /// <summary>Identifies the vector space. Chunks are only compared with queries embedded by the same model.</summary>
    string ModelId { get; }

    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}

/// <summary>
/// Offline embedder: signed feature hashing of lower-cased, lightly stemmed unigrams and bigrams into a fixed
/// L2-normalised vector. Deterministic, dependency-free, and no document text leaves the deployment. Captures
/// lexical overlap (not deep semantics); swap in a neural provider through <c>Embeddings:Provider = openai</c>.
/// </summary>
public sealed partial class HashingEmbeddingProvider : IEmbeddingProvider
{
    public const int Dimensions = 512;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "an", "and", "or", "of", "to", "in", "is", "it", "for", "on", "with", "as", "at", "by", "be", "this", "that", "are", "was", "how", "what", "why", "do", "does", "my", "me", "we", "you",
    };

    public string ModelId => "local-hash-v1-" + Dimensions.ToString(CultureInfo.InvariantCulture);

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        return Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Embed).ToList());
    }

    public static float[] Embed(string text)
    {
        var tokens = Tokenize(text);
        var tf = new Dictionary<string, (int Count, double Weight)>(StringComparer.Ordinal);
        for (var i = 0; i < tokens.Count; i++)
        {
            Add(tf, tokens[i], 1.0);
            if (i + 1 < tokens.Count)
            {
                Add(tf, tokens[i] + " " + tokens[i + 1], 0.5);
            }
        }

        var vector = new float[Dimensions];
        foreach (var (term, (count, weight)) in tf)
        {
            var hash = Fnv1a(term);
            var index = (int)(hash % Dimensions);
            var sign = ((hash >> 40) & 1) == 0 ? 1f : -1f;
            vector[index] += sign * (float)((1 + Math.Log(count)) * weight);
        }

        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] = (float)(vector[i] / norm);
            }
        }

        return vector;
    }

    internal static List<string> Tokenize(string text)
    {
        var result = new List<string>();
        foreach (Match m in TokenPattern().Matches(text.ToLowerInvariant()))
        {
            var token = m.Value;
            if (StopWords.Contains(token) || (token.Length < 2 && !char.IsDigit(token[0])))
            {
                continue;
            }

            result.Add(Stem(token));
        }

        return result;
    }

    private static string Stem(string t)
    {
        foreach (var suffix in new[] { "ing", "ed", "es", "s" })
        {
            if (t.Length > suffix.Length + 3 && t.EndsWith(suffix, StringComparison.Ordinal))
            {
                return t[..^suffix.Length];
            }
        }

        return t;
    }

    private static void Add(Dictionary<string, (int Count, double Weight)> tf, string term, double weight) =>
        tf[term] = tf.TryGetValue(term, out var e) ? (e.Count + 1, e.Weight) : (1, weight);

    private static ulong Fnv1a(string s)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TokenPattern();

    public static double Cosine(float[] a, float[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Length != b.Length)
        {
            return 0;
        }

        double dot = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }

        return dot;
    }
}

/// <summary>OpenAI-compatible <c>POST {baseUrl}/embeddings</c>. Note: document text is sent to that provider.</summary>
internal sealed class OpenAiEmbeddingProvider(HttpClient http, IOptions<AssistantOptions> options) : IEmbeddingProvider
{
    private const int BatchSize = 64;
    private readonly EmbeddingOptions _options = options.Value.Knowledge.Embeddings;

    public string ModelId => "openai:" + _options.Model;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new ServiceUnavailableException("The embedding provider is not configured.", "embeddings_unavailable");
        }

        var all = new List<float[]>(texts.Count);
        for (var offset = 0; offset < texts.Count; offset += BatchSize)
        {
            var batch = texts.Skip(offset).Take(BatchSize).ToList();
            using var request = new HttpRequestMessage(HttpMethod.Post, "embeddings") { Content = JsonContent.Create(new { model = _options.Model, input = batch }) };
            request.Headers.Add("Authorization", "Bearer " + _options.ApiKey);
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new ServiceUnavailableException("The embedding provider could not be reached.", ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new ServiceUnavailableException($"The embedding provider returned HTTP {(int)response.StatusCode}.", "embeddings_unavailable");
                }

                var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                foreach (var item in json.GetProperty("data").EnumerateArray().OrderBy(i => i.GetProperty("index").GetInt32()))
                {
                    var vector = item.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray();
                    var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
                    if (norm > 0)
                    {
                        for (var i = 0; i < vector.Length; i++)
                        {
                            vector[i] = (float)(vector[i] / norm);
                        }
                    }

                    all.Add(vector);
                }
            }
        }

        if (all.Count != texts.Count)
        {
            throw new ServiceUnavailableException("The embedding provider returned an unexpected number of vectors.", "embeddings_unavailable");
        }

        return all;
    }
}

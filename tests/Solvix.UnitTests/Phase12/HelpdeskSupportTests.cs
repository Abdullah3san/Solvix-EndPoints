namespace Solvix.UnitTests.Phase12;

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Solvix.Modules.Helpdesk.Application;
using Solvix.Modules.Helpdesk.Configuration;
using Solvix.Modules.Helpdesk.Domain;

public sealed class AttachmentRulesTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    [Fact]
    public void Valid_png_is_accepted_with_a_derived_content_type()
    {
        var (name, type, error) = AttachmentRules.Validate("screen.png", Png);
        Assert.Null(error);
        Assert.Equal("screen.png", name);
        Assert.Equal("image/png", type);
    }

    [Theory]
    [InlineData("run.exe")]
    [InlineData("a.ps1")]
    [InlineData("a.js")]
    [InlineData("a.html")]
    [InlineData("a.svg")]
    [InlineData("noextension")]
    public void Dangerous_or_unknown_extensions_are_rejected(string file) =>
        Assert.NotNull(AttachmentRules.Validate(file, Png).Error);

    [Fact]
    public void Content_must_match_the_extension()
    {
        Assert.NotNull(AttachmentRules.Validate("fake.png", Encoding.ASCII.GetBytes("MZ this is an exe")).Error);
        Assert.NotNull(AttachmentRules.Validate("fake.pdf", Png).Error);
        Assert.NotNull(AttachmentRules.Validate("fake.txt", [0x41, 0x00, 0x42]).Error); // NUL in text
        Assert.NotNull(AttachmentRules.Validate("empty.txt", []).Error);
    }

    [Theory]
    [InlineData("../../etc/passwd.txt", "passwd.txt")]
    [InlineData("..\\..\\windows\\win.txt", "win.txt")]
    [InlineData("invoice.exe.png", "invoice_exe.png")]
    [InlineData("a<b>:c|d?.txt", "a_b__c_d_.txt")]
    public void File_names_are_sanitised(string input, string expected) =>
        Assert.Equal(expected, AttachmentRules.Sanitize(input));

    [Fact]
    public void Very_long_names_are_truncated() =>
        Assert.True(AttachmentRules.Sanitize(new string('a', 500) + ".txt").Length <= 120);
}

public sealed class PromptRedactorTests
{
    [Fact]
    public void Emails_phones_and_secrets_are_masked()
    {
        // Assembled at runtime so that no key-shaped literal sits in source control (secret scanners rightly flag those).
        var fakeKey = "sk" + "_live_" + "51H" + "abcdefghijklmnop12345678";
        var text = $"Contact jane.doe@corp.test or +1 (555) 123-4567; key {fakeKey} broke.";
        var redacted = PromptRedactor.Redact(text, 1000);
        Assert.DoesNotContain("jane.doe", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("555", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(fakeKey, redacted, StringComparison.Ordinal);
        Assert.Contains("[email]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_is_capped() => Assert.Equal(50, PromptRedactor.Redact(new string('x', 500), 50).Length);
}

public sealed class SummarizerTests
{
    private static TicketSummaryInput Input(params SummaryMessage[] messages) =>
        new(42, "VPN down", "The VPN fails to connect for the whole sales team. It started this morning.", "Open", "High", messages, []);

    [Fact]
    public async Task Extractive_summary_contains_issue_activity_and_status()
    {
        var result = await new ExtractiveTicketSummarizer().SummarizeAsync(
            Input(new SummaryMessage(true, false, "Restarted the gateway."), new SummaryMessage(false, false, "Still failing.")), default);
        Assert.Equal("extractive", result.Source);
        Assert.Contains("Issue:", result.Text, StringComparison.Ordinal);
        Assert.Contains("1 technician reply", result.Text, StringComparison.Ordinal);
        Assert.Contains("Latest (requester): Still failing.", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extractive_summary_excludes_internal_notes_from_latest()
    {
        var result = await new ExtractiveTicketSummarizer().SummarizeAsync(
            Input(new SummaryMessage(true, false, "Public update."), new SummaryMessage(true, true, "SECRET internal note")), default);
        Assert.DoesNotContain("SECRET", result.Text, StringComparison.Ordinal);
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://ai.test") };
    }

    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        public string? ApiKey { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            ApiKey = request.Headers.TryGetValues("x-api-key", out var v) ? v.First() : null;
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static AnthropicTicketSummarizer Create(HttpMessageHandler handler) =>
        new(new StubFactory(handler), new ExtractiveTicketSummarizer(),
            Options.Create(new HelpdeskOptions { Ai = new AiOptions { Provider = "anthropic", ApiKey = "test-key", Model = "claude-haiku-5-5" } }),
            NullLogger<AnthropicTicketSummarizer>.Instance);

    [Fact]
    public async Task Model_summary_is_returned_and_personal_data_never_leaves()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"content":[{"type":"text","text":"VPN outage for sales; gateway restarted; still failing."}]}""");
        var input = Input(new SummaryMessage(false, false, "Call me on +44 20 7946 0958 or mail bob@corp.test"));
        var result = await Create(handler).SummarizeAsync(input, default);

        Assert.Equal("claude-haiku-5-5", result.Source);
        Assert.StartsWith("VPN outage", result.Text, StringComparison.Ordinal);
        Assert.Equal("test-key", handler.ApiKey);
        Assert.DoesNotContain("bob@corp.test", handler.RequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("7946", handler.RequestBody!, StringComparison.Ordinal);
        Assert.Contains("untrusted", handler.RequestBody!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "oops")]
    [InlineData(HttpStatusCode.OK, "{\"content\":[]}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task Failures_fall_back_to_the_extractive_summary(HttpStatusCode status, string body)
    {
        var result = await Create(new CapturingHandler(status, body)).SummarizeAsync(Input(), default);
        Assert.Equal("extractive", result.Source);
    }
}

public sealed class EmailIntakeHelperTests
{
    [Theory]
    [InlineData("Re: RE: Fwd: Printer broken", "Printer broken")]
    [InlineData("  AW: Hilfe  ", "Hilfe")]
    [InlineData("", "(no subject)")]
    [InlineData("Normal subject", "Normal subject")]
    public void Subjects_are_cleaned(string input, string expected) =>
        Assert.Equal(expected, EmailIntakeService.CleanSubject(input));

    [Theory]
    [InlineData("User@Example.COM", "user@example.com")]
    [InlineData("not-an-address", null)]
    [InlineData("", null)]
    [InlineData("Name <a@b.test>", null)]
    public void Addresses_are_normalised(string input, string? expected) =>
        Assert.Equal(expected, EmailIntakeService.NormalizeAddress(input));

    [Fact]
    public void Message_ids_lose_their_angle_brackets() =>
        Assert.Equal("abc@mail.test", EmailIntakeService.NormalizeMessageId(" <abc@mail.test> "));

    [Theory]
    [InlineData("Auto-Submitted", "auto-replied", true)]
    [InlineData("auto-submitted", "no", false)]
    [InlineData("Precedence", "bulk", true)]
    [InlineData("X-Auto-Response-Suppress", "All", true)]
    [InlineData("List-Id", "<list.test>", true)]
    [InlineData("X-Mailer", "Outlook", false)]
    public void Automated_mail_is_detected(string header, string value, bool expected) =>
        Assert.Equal(expected, EmailIntakeService.IsAutomated(new Dictionary<string, string> { [header] = value }));

    [Fact]
    public void Html_bodies_are_converted_to_plain_text()
    {
        var request = new InboundEmailRequest("m", null, null, new EmailAddressDto("a@b.test", null), "s", null,
            "<html><style>p{color:red}</style><script>alert(1)</script><p>Hello <b>team</b>,</p><p>It&amp;s broken</p></html>", null, null);
        var body = EmailIntakeService.ExtractBody(request, 1000);
        Assert.Contains("Hello team", body, StringComparison.Ordinal);
        Assert.Contains("It&s broken", body, StringComparison.Ordinal);
        Assert.DoesNotContain("alert", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Crafted_html_cannot_burn_unbounded_cpu()
    {
        // 12k unterminated <script tags: a naive lazy pattern is quadratic on this.
        var evil = string.Concat(Enumerable.Repeat("<script ", 12_000)) + "x";
        var request = new InboundEmailRequest("m", null, null, new EmailAddressDto("a@b.test", null), "s", null, evil, null, null);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var body = EmailIntakeService.ExtractBody(request, 1000);
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
        Assert.False(string.IsNullOrWhiteSpace(body));
    }

    [Fact]
    public void Crafted_subjects_and_redaction_input_stay_fast()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.NotEmpty(EmailIntakeService.CleanSubject(string.Concat(Enumerable.Repeat(" \t re: ", 5_000))));
        var digits = string.Concat(Enumerable.Repeat("1 ", 200_000));
        Assert.True(PromptRedactor.Redact(digits, 12_000).Length <= 12_000);
        var dashes = string.Concat(Enumerable.Repeat("a1-", 200_000));
        Assert.True(PromptRedactor.Redact(dashes, 12_000).Length <= 12_000);
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Bodies_are_truncated_and_never_empty()
    {
        var empty = new InboundEmailRequest("m", null, null, new EmailAddressDto("a@b.test", null), "s", "  ", null, null, null);
        Assert.Equal("(empty message)", EmailIntakeService.ExtractBody(empty, 100));
        var long1 = new InboundEmailRequest("m", null, null, new EmailAddressDto("a@b.test", null), "s", new string('x', 500), null, null, null);
        Assert.Equal(100, EmailIntakeService.ExtractBody(long1, 100).Length);
    }
}

public sealed class ChannelTokenTests
{
    [Fact]
    public void Token_round_trips_and_only_the_hash_matches()
    {
        var tenant = Guid.NewGuid();
        var channel = Guid.NewGuid();
        var (token, hash) = ChannelTokenCodec.Generate(tenant, channel);
        Assert.StartsWith("svxm_", token, StringComparison.Ordinal);
        Assert.True(ChannelTokenCodec.TryParse(token, out var t, out var c, out var secret));
        Assert.Equal(tenant, t);
        Assert.Equal(channel, c);
        Assert.True(ChannelTokenCodec.Matches(hash, secret));
        Assert.False(ChannelTokenCodec.Matches(hash, new byte[32]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("svxe_abc")]
    [InlineData("svxm_not-base64!!")]
    [InlineData("svxm_AAAA")]
    public void Malformed_tokens_are_rejected(string? token) =>
        Assert.False(ChannelTokenCodec.TryParse(token, out _, out _, out _));

    [Fact]
    public void Tokens_are_unique()
    {
        var a = ChannelTokenCodec.Generate(Guid.NewGuid(), Guid.NewGuid()).Token;
        var b = ChannelTokenCodec.Generate(Guid.NewGuid(), Guid.NewGuid()).Token;
        Assert.NotEqual(a, b);
    }
}

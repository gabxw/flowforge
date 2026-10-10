using System.Text;
using System.Text.Json;
using FlowForge.Application.Executions;
using FlowForge.Application.Webhooks;
using Xunit;
namespace FlowForge.Application.Tests;
public sealed class WebhookContractTests
{
    [Theory]
    [InlineData("{}")][InlineData("[]")][InlineData("null")][InlineData("true")][InlineData("42")][InlineData("\"texto\"")]
    public void Any_json_root_is_valid_and_payload_survives_document_disposal(string text)
    {
        var payload = WebhookPayload.Parse(Encoding.UTF8.GetBytes(text));
        Assert.Equal(text, payload.Value.GetRawText()); Assert.Equal(64, payload.RequestDigest.Length);
        Assert.DoesNotContain(text, payload.ToString());
    }
    [Theory]
    [InlineData("")][InlineData("{broken}")][InlineData("{\"a\":1,\"a\":2}")][InlineData("{} {}")][InlineData("{ /* comment */ }")]
    public void Invalid_or_ambiguous_json_is_rejected(string text) =>
        Assert.ThrowsAny<JsonException>(() => WebhookPayload.Parse(Encoding.UTF8.GetBytes(text)));
    [Fact]
    public void Exact_bytes_define_digest_and_both_raw_and_context_budgets_are_enforced()
    {
        Assert.NotEqual(WebhookPayload.Parse("{}"u8.ToArray()).RequestDigest, WebhookPayload.Parse("{ }"u8.ToArray()).RequestDigest);
        Assert.Throws<WebhookPayloadLimitException>(() => WebhookPayload.Parse(new byte[ExecutionPayload.MaxBytes + 1]));
        var atLimit = "\"" + new string('a', ExecutionPayload.MaxBytes - 2) + "\"";
        Assert.Equal(ExecutionPayload.MaxBytes, ExecutionPayload.Summarize(WebhookPayload.Parse(Encoding.UTF8.GetBytes(atLimit)).Value).ByteLength);
        var expanded = "\"" + new string('é', 20000) + "\"";
        Assert.Throws<WebhookPayloadLimitException>(() => WebhookPayload.Parse(Encoding.UTF8.GetBytes(expanded)));
        Assert.ThrowsAny<JsonException>(() => WebhookPayload.Parse(Encoding.UTF8.GetBytes(new string('[', 33) + "0" + new string(']', 33))));
    }
    [Fact]
    public void Invalid_utf8_is_rejected_instead_of_being_replaced() =>
        Assert.ThrowsAny<JsonException>(() => WebhookPayload.Parse(new byte[] { 0x22, 0xff, 0x22 }));
    [Theory]
    [InlineData("")][InlineData("has space")][InlineData("new\nline")][InlineData("não-ascii")]
    public void Unsafe_idempotency_keys_are_rejected(string key) => Assert.Throws<ArgumentException>(() => WebhookService.ValidateKey(key));
    [Fact]
    public void Optional_keys_and_retention_have_explicit_limits()
    {
        WebhookService.ValidateKey(null); WebhookService.ValidateKey(new string('a', 128));
        Assert.Throws<ArgumentException>(() => WebhookService.ValidateKey(new string('a', 129)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebhookAcceptanceOptions(TimeSpan.FromMinutes(59)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebhookAcceptanceOptions(TimeSpan.FromDays(8)));
        Assert.Equal(TimeSpan.FromHours(24), WebhookAcceptanceOptions.Default.IdempotencyLifetime);
    }
}

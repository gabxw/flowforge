using System.Text.Json;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;
using Xunit;

namespace FlowForge.Domain.Tests;

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("http://example.invalid")]
    [InlineData("ftp://example.invalid")]
    [InlineData("/relative")]
    [InlineData("https://user:password@example.invalid")]
    [InlineData("https://user@example.invalid")]
    [InlineData("https://@example.invalid")]
    [InlineData("https://example.invalid/#part")]
    [InlineData("https://example.invalid/#")]
    public void Http_rejects_nonabsolute_nonhttps_userinfo_or_fragment(string value) =>
        Assert.Throws<ArgumentException>(() => new HttpRequestConfiguration(
            new Uri(value, UriKind.RelativeOrAbsolute), HttpRequestMethod.Get));

    [Fact]
    public void Http_rejects_null_url() =>
        Assert.Throws<ArgumentNullException>(() => new HttpRequestConfiguration(null!, HttpRequestMethod.Get));

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(-1)]
    public void Http_rejects_unknown_method(int method) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new HttpRequestConfiguration(
            new Uri("https://example.invalid"), (HttpRequestMethod)method));

    [Theory]
    [InlineData(HttpRequestMethod.Get)]
    [InlineData(HttpRequestMethod.Post)]
    [InlineData(HttpRequestMethod.Put)]
    [InlineData(HttpRequestMethod.Patch)]
    [InlineData(HttpRequestMethod.Delete)]
    [InlineData(HttpRequestMethod.Head)]
    [InlineData(HttpRequestMethod.Options)]
    public void Http_accepts_supported_method_and_preserves_query(HttpRequestMethod method)
    {
        var configuration = new HttpRequestConfiguration(new Uri("https://example.invalid/path?q=value"), method);
        Assert.Equal(NodeType.HttpRequest, configuration.Type);
        Assert.Equal(method, configuration.Method);
        Assert.Equal("?q=value", configuration.Url.Query);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(864000000001L)]
    public void Delay_rejects_nonpositive_or_more_than_24_hours(long ticks) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DelayConfiguration(TimeSpan.FromTicks(ticks)));

    [Theory]
    [InlineData(1L)]
    [InlineData(864000000000L)]
    public void Delay_accepts_duration_boundaries(long ticks)
    {
        var configuration = new DelayConfiguration(TimeSpan.FromTicks(ticks));
        Assert.Equal(TimeSpan.FromTicks(ticks), configuration.Duration);
        Assert.Equal(NodeType.Delay, configuration.Type);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void Log_rejects_missing_message(string? message) =>
        Assert.ThrowsAny<ArgumentException>(() => new LogConfiguration(message!));

    [Fact]
    public void Log_counts_utf16_units_without_interpreting_or_trimming_message()
    {
        var message = " " + string.Concat(Enumerable.Repeat("😀", 999)) + " ";
        var configuration = new LogConfiguration(message);
        Assert.Equal(message, configuration.Message);
        Assert.Equal(NodeType.Log, configuration.Type);
        Assert.Throws<ArgumentException>(() => new LogConfiguration(message + "x"));
        Assert.Equal(NodeType.WebhookTrigger, new WebhookTriggerConfiguration().Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/a//b/")]
    [InlineData("/~0/~1/~01")]
    [InlineData("/*/0/01/-")]
    [InlineData("/á/😀/漢字")]
    public void Pointer_keeps_valid_string_form_without_resolving_tokens(string value) =>
        Assert.Equal(value, new JsonPointer(value).Value);

    [Theory]
    [InlineData(null)]
    [InlineData("total")]
    [InlineData("#/total")]
    [InlineData("/~")]
    [InlineData("/~2")]
    [InlineData("/a~x")]
    public void Pointer_rejects_null_non_string_form_or_bad_escape(string? value) =>
        Assert.ThrowsAny<ArgumentException>(() => new JsonPointer(value!));

    [Fact]
    public void Pointer_rejects_malformed_unicode()
    {
        foreach (var value in new[] { "/\ud800", "/\udc00", "/\ud800a", "/\udc00\ud800" })
            Assert.Throws<ArgumentException>(() => new JsonPointer(value));
    }

    [Fact]
    public void Pointer_enforces_utf16_length_and_segment_boundaries()
    {
        Assert.Equal(1024, new JsonPointer("/" + new string('a', 1023)).Value.Length);
        Assert.Equal(1024, new JsonPointer("/a" + string.Concat(Enumerable.Repeat("😀", 511))).Value.Length);
        Assert.Throws<ArgumentException>(() => new JsonPointer("/" + new string('a', 1024)));
        Assert.Equal(new string('/', 32), new JsonPointer(new string('/', 32)).Value);
        Assert.Throws<ArgumentException>(() => new JsonPointer(new string('/', 33)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(-1)]
    public void Condition_rejects_unknown_operation(int operation) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConditionConfiguration(new JsonPointer(""),
            (ConditionOperator)operation));

    [Fact]
    public void Condition_requires_pointer_and_exists_has_no_expected_value()
    {
        Assert.Throws<ArgumentNullException>(() => new ConditionConfiguration(null!, ConditionOperator.Exists));
        var configuration = new ConditionConfiguration(new JsonPointer("/total"), ConditionOperator.Exists);
        Assert.Equal("/total", configuration.SourcePointer.Value);
        Assert.Equal(NodeType.Condition, configuration.Type);
        Assert.Equal(ConditionOperator.Exists, configuration.Operation);
        Assert.Null(configuration.ExpectedValue);
        using var document = JsonDocument.Parse("null");
        Assert.Throws<ArgumentException>(() => new ConditionConfiguration(new JsonPointer(""),
            ConditionOperator.Exists, document.RootElement));
    }

    [Theory]
    [InlineData(ConditionOperator.Equals)]
    [InlineData(ConditionOperator.NotEquals)]
    [InlineData(ConditionOperator.GreaterThan)]
    [InlineData(ConditionOperator.GreaterThanOrEqual)]
    [InlineData(ConditionOperator.LessThan)]
    [InlineData(ConditionOperator.LessThanOrEqual)]
    public void Comparison_requires_expected_value(ConditionOperator operation) =>
        Assert.Throws<ArgumentException>(() => new ConditionConfiguration(new JsonPointer(""), operation));

    [Theory]
    [InlineData(ConditionOperator.Equals, "\"value\"")]
    [InlineData(ConditionOperator.Equals, "false")]
    [InlineData(ConditionOperator.Equals, "null")]
    [InlineData(ConditionOperator.Equals, "79228162514264337593543950335")]
    [InlineData(ConditionOperator.NotEquals, "true")]
    [InlineData(ConditionOperator.NotEquals, "1.5")]
    [InlineData(ConditionOperator.GreaterThan, "-1.5")]
    [InlineData(ConditionOperator.GreaterThanOrEqual, "0")]
    [InlineData(ConditionOperator.LessThan, "1e2")]
    [InlineData(ConditionOperator.LessThanOrEqual, "-79228162514264337593543950335")]
    public void Comparison_accepts_scalar_and_retains_json_after_document_disposal(ConditionOperator operation, string json)
    {
        ConditionConfiguration configuration;
        using (var document = JsonDocument.Parse(json))
            configuration = new ConditionConfiguration(new JsonPointer(""), operation, document.RootElement);
        Assert.Equal(json, configuration.ExpectedValue!.Value.GetRawText());
        Assert.Equal(operation, configuration.Operation);
    }

    [Theory]
    [InlineData(ConditionOperator.Equals, "{}")]
    [InlineData(ConditionOperator.NotEquals, "[]")]
    [InlineData(ConditionOperator.Equals, "1e100")]
    [InlineData(ConditionOperator.NotEquals, "79228162514264337593543950336")]
    [InlineData(ConditionOperator.GreaterThan, "\"1\"")]
    [InlineData(ConditionOperator.GreaterThanOrEqual, "true")]
    [InlineData(ConditionOperator.LessThan, "null")]
    [InlineData(ConditionOperator.LessThanOrEqual, "1e100")]
    public void Comparison_rejects_wrong_kind_or_number_outside_decimal(ConditionOperator operation, string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => new ConditionConfiguration(new JsonPointer(""), operation, document.RootElement));
    }

    [Fact]
    public void Json_literals_reject_undefined()
    {
        Assert.Throws<ArgumentException>(() => new ConditionConfiguration(new JsonPointer(""),
            ConditionOperator.Equals, default(JsonElement)));
        Assert.Throws<ArgumentException>(() => TransformField.FromValue("a", default));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void Transform_rejects_missing_target_in_both_factories(string? target)
    {
        using var document = JsonDocument.Parse("1");
        Assert.ThrowsAny<ArgumentException>(() => TransformField.FromPath(target!, new JsonPointer("")));
        Assert.ThrowsAny<ArgumentException>(() => TransformField.FromValue(target!, document.RootElement));
    }

    [Fact]
    public void Transform_target_counts_utf16_and_path_requires_pointer()
    {
        var target = string.Concat(Enumerable.Repeat("😀", 64));
        using var document = JsonDocument.Parse("null");
        Assert.Equal(target, TransformField.FromPath(target, new JsonPointer("")).TargetProperty);
        Assert.Equal(target, TransformField.FromValue(target, document.RootElement).TargetProperty);
        Assert.Throws<ArgumentException>(() => TransformField.FromPath(target + "a", new JsonPointer("")));
        Assert.Throws<ArgumentException>(() => TransformField.FromValue(target + "a", document.RootElement));
        Assert.Throws<ArgumentNullException>(() => TransformField.FromPath("a", null!));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("\"text\"")]
    [InlineData("{\"nested\":[1,2]}")]
    [InlineData("[1,2]")]
    public void Transform_literal_survives_document_disposal_and_excludes_source(string json)
    {
        TransformField field;
        using (var document = JsonDocument.Parse(json))
            field = TransformField.FromValue("value", document.RootElement);
        Assert.Null(field.SourcePointer);
        Assert.Equal(json, field.Literal!.Value.GetRawText());
        var pathField = TransformField.FromPath("fromPath", new JsonPointer("/source"));
        Assert.Null(pathField.Literal);
        Assert.Equal("/source", pathField.SourcePointer!.Value);
    }

    [Fact]
    public void Transform_rejects_null_empty_null_element_duplicate_and_more_than_50_fields()
    {
        Assert.Throws<ArgumentNullException>(() => new TransformJsonConfiguration(null!));
        Assert.Throws<ArgumentException>(() => new TransformJsonConfiguration([]));
        Assert.Throws<ArgumentException>(() => new TransformJsonConfiguration([null!]));
        Assert.Throws<ArgumentException>(() => new TransformJsonConfiguration([
            TransformField.FromPath("a", new JsonPointer("")), TransformField.FromPath("a", new JsonPointer("/other"))]));
        Assert.Throws<ArgumentException>(() => new TransformJsonConfiguration(
            Enumerable.Range(0, 51).Select(i => TransformField.FromPath($"field{i}", new JsonPointer("")))));
    }

    [Fact]
    public void Transform_copies_fields_exposes_readonly_collection_and_uses_ordinal_targets()
    {
        var fields = new List<TransformField>
        {
            TransformField.FromPath("a", new JsonPointer("/a")),
            TransformField.FromPath("A", new JsonPointer("/A"))
        };
        var configuration = new TransformJsonConfiguration(fields);
        fields.Clear();
        Assert.Equal(NodeType.TransformJson, configuration.Type);
        Assert.Equal(["a", "A"], configuration.Fields.Select(field => field.TargetProperty));
        if (configuration.Fields is IList<TransformField> writableView)
            Assert.Throws<NotSupportedException>(() => writableView[0] = TransformField.FromPath("changed", new JsonPointer("")));
        Assert.Equal(50, new TransformJsonConfiguration(Enumerable.Range(0, 50)
            .Select(i => TransformField.FromPath($"field{i}", new JsonPointer("")))).Fields.Count);
    }
}

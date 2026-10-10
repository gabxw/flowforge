using System.Text.Json;
using FlowForge.Application.Executions;
using FlowForge.Domain.Executions;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;
using Xunit;

namespace FlowForge.Application.Tests;

public sealed class DeclarativeNodeTests
{
    private static JsonElement Json(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
    private static NodeRunContext Context(NodeConfiguration config, string input) => new(Guid.NewGuid(), Guid.NewGuid(),
        new WorkflowNode(Guid.NewGuid(), Guid.NewGuid(), config.Type, config), Json(input));
    private static ConditionConfiguration Condition(string path, ConditionOperator op, string? expected = null) =>
        new(new JsonPointer(path), op, expected is null ? null : Json(expected));

    [Theory]
    [InlineData("null", "", true)]
    [InlineData("{}", "/absent", false)]
    [InlineData("{\"value\":null}", "/value", true)]
    [InlineData("{\"a/b\":{\"m~n\":7}}", "/a~1b/m~0n", true)]
    [InlineData("{\"~1\":7}", "/~01", true)]
    [InlineData("{\"\":7}", "/", true)]
    [InlineData("{\"values\":[7]}", "/values/0", true)]
    [InlineData("{\"values\":[7]}", "/values/1", false)]
    [InlineData("{\"values\":[7]}", "/values/01", false)]
    [InlineData("{\"values\":[7]}", "/values/+0", false)]
    [InlineData("{\"values\":[7]}", "/values/-", false)]
    [InlineData("{\"values\":[7]}", "/values/-1", false)]
    [InlineData("{\"values\":[7]}", "/values/9999999999999999999999", false)]
    [InlineData("{\"value\":null}", "/value/nested", false)]
    [InlineData("{\"01\":7}", "/01", true)]
    [InlineData("{\"Value\":7}", "/value", false)]
    public async Task Exists_distinguishes_missing_null_and_rfc_pointer_tokens(string input, string path, bool expected)
    {
        var context = Context(Condition(path, ConditionOperator.Exists), input);
        var result = await new ConditionNodeExecutor().ExecuteAsync(context, default);
        Assert.Null(result.ErrorCode); Assert.Equal(expected ? "true" : "false", result.Port);
        Assert.Equal(context.Input.GetRawText(), result.Output!.Value.GetRawText());
    }

    [Theory]
    [InlineData(ConditionOperator.Equals, "100", "100.0", true)]
    [InlineData(ConditionOperator.Equals, "1e2", "100", true)]
    [InlineData(ConditionOperator.Equals, "-0", "0", true)]
    [InlineData(ConditionOperator.Equals, "1e-28", "0.0000000000000000000000000001", true)]
    [InlineData(ConditionOperator.Equals, "79228162514264337593543950335", "79228162514264337593543950335", true)]
    [InlineData(ConditionOperator.NotEquals, "100", "99", true)]
    [InlineData(ConditionOperator.Equals, "\"100\"", "100", false)]
    [InlineData(ConditionOperator.NotEquals, "\"100\"", "100", true)]
    [InlineData(ConditionOperator.Equals, "null", "null", true)]
    [InlineData(ConditionOperator.Equals, "true", "true", true)]
    [InlineData(ConditionOperator.Equals, "true", "false", false)]
    [InlineData(ConditionOperator.Equals, "\"Abc\"", "\"abc\"", false)]
    [InlineData(ConditionOperator.Equals, "{}", "null", false)]
    [InlineData(ConditionOperator.GreaterThan, "101", "100", true)]
    [InlineData(ConditionOperator.GreaterThan, "100", "100", false)]
    [InlineData(ConditionOperator.GreaterThanOrEqual, "100", "100", true)]
    [InlineData(ConditionOperator.LessThan, "99", "100", true)]
    [InlineData(ConditionOperator.LessThanOrEqual, "100", "100", true)]
    public async Task Comparison_is_typed_ordinal_and_exact_decimal(ConditionOperator op, string actual, string expected, bool predicate)
    {
        var context = Context(Condition("", op, expected), actual);
        var result = await new ConditionNodeExecutor().ExecuteAsync(context, default);
        Assert.Null(result.ErrorCode); Assert.Equal(predicate ? "true" : "false", result.Port);
    }

    [Theory]
    [InlineData(ConditionOperator.Equals)] [InlineData(ConditionOperator.NotEquals)] [InlineData(ConditionOperator.GreaterThan)]
    public async Task Missing_operand_is_false_even_for_not_equals(ConditionOperator op)
    {
        var result = await new ConditionNodeExecutor().ExecuteAsync(Context(Condition("/missing", op, "1"), "{}"), default);
        Assert.Null(result.ErrorCode); Assert.Equal("false", result.Port);
    }

    [Theory]
    [InlineData("1e-29")] [InlineData("1.000000000000000000000000000001")] [InlineData("1e1000")]
    [InlineData("\"100\"")] [InlineData("null")] [InlineData("true")]
    public async Task Ordered_comparison_refuses_coercion_overflow_underflow_and_rounding(string actual)
    {
        var result = await new ConditionNodeExecutor().ExecuteAsync(Context(Condition("", ConditionOperator.GreaterThan, "1"), actual), default);
        Assert.Equal(ExecutionFailureCode.ConditionValueNotComparable, result.ErrorCode); Assert.Null(result.Output);
    }

    [Fact]
    public async Task Literal_that_try_get_decimal_would_round_is_not_silently_used()
    {
        var config = Condition("", ConditionOperator.Equals, "1.000000000000000000000000000001");
        var result = await new ConditionNodeExecutor().ExecuteAsync(Context(config, "1"), default);
        Assert.Equal(ExecutionFailureCode.ConditionValueNotComparable, result.ErrorCode);
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task Duplicate_referenced_member_is_a_safe_failure(bool condition)
    {
        var config = condition ? (NodeConfiguration)Condition("/value", ConditionOperator.Exists)
            : new TransformJsonConfiguration([TransformField.FromPath("selected", new JsonPointer("/value"))]);
        INodeExecutor executor = condition ? new ConditionNodeExecutor() : new TransformJsonNodeExecutor();
        var result = await executor.ExecuteAsync(Context(config, "{\"value\":1,\"value\":2}"), default);
        Assert.Equal(ExecutionFailureCode.JsonPointerAmbiguous, result.ErrorCode); Assert.Null(result.Output);
    }

    [Fact]
    public async Task Transform_builds_a_flat_object_from_paths_and_literals_without_mutating_input()
    {
        var config = new TransformJsonConfiguration([
            TransformField.FromPath("order", new JsonPointer("/orders/0")),
            TransformField.FromPath("root", new JsonPointer("")),
            TransformField.FromValue("literal", Json("{\"nested\":[1,null,true]}")),
            TransformField.FromValue("nil", Json("null"))]);
        var context = Context(config, "{\"orders\":[{\"id\":7}],\"private\":\"fake\"}");
        var result = await new TransformJsonNodeExecutor().ExecuteAsync(context, default);
        Assert.Null(result.ErrorCode); var output = result.Output!.Value;
        Assert.Equal(7, output.GetProperty("order").GetProperty("id").GetInt32());
        Assert.Equal(context.Input.GetRawText(), output.GetProperty("root").GetRawText());
        Assert.Equal(JsonValueKind.Null, output.GetProperty("nil").ValueKind);
        Assert.Equal(3, output.GetProperty("literal").GetProperty("nested").GetArrayLength());
        Assert.Equal("fake", context.Input.GetProperty("private").GetString());
        Assert.Equal("next", result.Port); Assert.True(new TransformJsonNodeExecutor().CanReplayAfterInterruption);
    }

    [Theory] [InlineData("/absent")] [InlineData("/values/01")] [InlineData("/values/-")] [InlineData("/nil/child")]
    public async Task Transform_missing_path_fails_instead_of_inventing_null(string path)
    {
        var config = new TransformJsonConfiguration([TransformField.FromPath("value", new JsonPointer(path))]);
        var result = await new TransformJsonNodeExecutor().ExecuteAsync(Context(config, "{\"values\":[7],\"nil\":null}"), default);
        Assert.Equal(ExecutionFailureCode.TransformSourceMissing, result.ErrorCode); Assert.Null(result.Output);
    }

    [Fact]
    public async Task Transform_cannot_amplify_input_past_the_64_kib_context_budget()
    {
        var fields = Enumerable.Range(0, 50).Select(i => TransformField.FromPath("copy" + i, new JsonPointer("")));
        var input = JsonSerializer.Serialize(new { text = new string('x', 4000) });
        var result = await new TransformJsonNodeExecutor().ExecuteAsync(Context(new TransformJsonConfiguration(fields), input), default);
        Assert.Equal(ExecutionFailureCode.ContextLimitExceeded, result.ErrorCode); Assert.Null(result.Output);
    }

    [Fact]
    public async Task Transform_enforces_depth_before_persisting_an_invalid_context()
    {
        var nested = new string('[', 32) + "1" + new string(']', 32);
        var config = new TransformJsonConfiguration([TransformField.FromValue("nested", Json(nested))]);
        var result = await new TransformJsonNodeExecutor().ExecuteAsync(Context(config, "{}"), default);
        Assert.Equal(ExecutionFailureCode.TransformValueInvalid, result.ErrorCode); Assert.Null(result.Output);
    }

    [Fact]
    public async Task Delay_returns_a_durable_intention_immediately_and_cancellation_is_honored()
    {
        var context = Context(new DelayConfiguration(TimeSpan.FromHours(24)), "{\"value\":42}");
        var task = new DelayNodeExecutor().ExecuteAsync(context, default);
        Assert.True(task.IsCompletedSuccessfully); var result = await task;
        Assert.Equal(TimeSpan.FromHours(24), result.SuspendFor); Assert.Equal(context.Input.GetRawText(), result.Output!.Value.GetRawText());
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DelayNodeExecutor().ExecuteAsync(context, stop.Token));
    }
}

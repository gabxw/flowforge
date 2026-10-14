using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using FlowForge.Application.Workflows;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FlowForge.Api.Tests;

[Collection("Workflow API")]
public sealed class UnhandledFailureLoggingTests(WorkflowApiFixture fixture)
{
    private const string Sensitive = "SENTINELA_SEGREDO_NA_MENSAGEM";

    [Fact]
    public async Task Unexpected_failure_returns_generic_500_and_logs_type_route_and_stack_without_message()
    {
        var logs = new CapturingLoggerProvider();
        await using var api = fixture.CreateApi().WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
            services.AddScoped(_ => ThrowingStore.Create());
            services.AddSingleton<ILoggerProvider>(logs);
        }));
        using var client = api.CreateClient();

        using var response = await client.GetAsync("/api/workflows");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Erro interno.", body);
        Assert.DoesNotContain(Sensitive, body);
        var entry = Assert.Single(logs.Entries, e => e.Category == "FlowForge.Api.ApiExceptionHandler" && e.Level == LogLevel.Error);
        Assert.Equal("unhandled_exception", entry.Values["EventCode"]);
        Assert.Equal(typeof(InvalidTimeZoneException).FullName, entry.Values["ErrorType"]);
        Assert.Equal("/api/workflows/", entry.Values["Route"]);
        Assert.Contains(nameof(ThrowingStore), (string)entry.Values["StackTrace"]!);
        Assert.DoesNotContain(Sensitive, entry.Message);
        Assert.DoesNotContain(Sensitive, (string)entry.Values["StackTrace"]!);
    }

    // Qualquer chamada ao store falha com uma exceção que o handler não mapeia.
    public class ThrowingStore : DispatchProxy
    {
        public static IWorkflowStore Create() => Create<IWorkflowStore, ThrowingStore>();
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new InvalidTimeZoneException(Sensitive);
    }

    private sealed record Entry(string Category, LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Values);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentBag<Entry> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);
        public void Dispose() { }

        private sealed class Logger(string category, ConcurrentBag<Entry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var values = state is IEnumerable<KeyValuePair<string, object?>> pairs ? pairs.ToDictionary(p => p.Key, p => p.Value) : [];
                entries.Add(new(category, logLevel, formatter(state, exception), values));
            }
        }
    }
}

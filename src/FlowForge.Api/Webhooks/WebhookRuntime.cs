using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using FlowForge.Application.Webhooks;
using FlowForge.Infrastructure.Persistence;
using FlowForge.Infrastructure.Runtime;

namespace FlowForge.Api.Webhooks;
internal static class WebhookRuntime
{
    public static void AddWebhookRuntime(this IServiceCollection services)
    {
        // Lazy: liveness e os comandos manuais continuam independentes do keyring.
        services.AddSingleton(sp => ExecutionProtectionRuntime.Create(sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IHostEnvironment>().IsDevelopment()));
        services.AddSingleton(sp => {
            var raw = sp.GetRequiredService<IConfiguration>()["FlowForge:Webhooks:IdempotencyHours"];
            if (raw is null) return WebhookAcceptanceOptions.Default;
            if (!int.TryParse(raw, out var hours) || hours is < 1 or > 168) throw new RuntimeConfigurationException();
            return new WebhookAcceptanceOptions(TimeSpan.FromHours(hours));
        });
        services.AddScoped<IWebhookStore, PostgresWebhookStore>();
        services.AddScoped<WebhookService>();
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IConfiguration>((options, configuration) => {
            var permits = ReadLimit(configuration, "PermitLimit", 60, 1, 10000);
            var windowSeconds = ReadLimit(configuration, "WindowSeconds", 60, 1, 3600);
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // Uma única partição local evita crescimento com IDs/IPs fornecidos pelo cliente.
            options.AddPolicy("webhooks", context => RateLimitPartition.GetFixedWindowLimiter("webhooks", partition => new()
            { PermitLimit = permits, Window = TimeSpan.FromSeconds(windowSeconds), QueueLimit = 0, AutoReplenishment = true }));
            options.OnRejected = async (context, ct) => {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                    context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retry.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                await Results.Problem(statusCode: 429, title: "Limite de webhooks atingido.", detail: "Aguarde antes de tentar novamente.").ExecuteAsync(context.HttpContext);
            };
        });
    }
    private static int ReadLimit(IConfiguration configuration, string key, int fallback, int min, int max)
    {
        var raw = configuration["FlowForge:Webhooks:" + key];
        if (raw is null) return fallback;
        if (int.TryParse(raw, out var value) && value >= min && value <= max) return value;
        throw new RuntimeConfigurationException();
    }
}

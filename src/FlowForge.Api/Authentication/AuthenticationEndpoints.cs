using System.Security.Claims;
using System.Text.Json;
using FlowForge.Application.Users;
using FlowForge.Infrastructure.Runtime;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
namespace FlowForge.Api.Authentication;
internal sealed record SignInRequest
{
    public required string Email { get; init; }
    public required string Password { get; init; }
    public override string ToString() => "SignInRequest [redacted]";
}
internal static class AuthenticationEndpoints
{
    private const string Cookie = "flowforge.refresh";
    public static void MapAuthentication(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Autenticação").AllowAnonymous().RequireRateLimiting("auth");
        group.AddEndpointFilter(async (context, next) => {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (HttpMethods.IsPost(context.HttpContext.Request.Method)) {
                try { await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context.HttpContext); }
                catch (AntiforgeryValidationException) { return Results.Problem(statusCode: 403, title: "Requisição recusada.", detail: "Reabra a tela e tente novamente."); }
            }
            return await next(context);
        });
        group.MapGet("/csrf", (HttpContext http, IAntiforgery antiforgery) => Results.Ok(new { csrfToken = antiforgery.GetAndStoreTokens(http).RequestToken })).WithName("GetAntiforgeryToken");
        group.MapGet("/status", async (HttpContext http, IAuthenticationStore store, IConfiguration config, IHostEnvironment env, CancellationToken ct) =>
            Results.Ok(new { hasSession = http.Request.Cookies.ContainsKey(Cookie), canRegister = AllowRegistration(config, env), setupRequired = env.IsDevelopment() && config.GetValue<bool>("FlowForge:Authentication:AllowInitialSetup") && !await store.HasAccountsAsync(ct) }));
        group.MapPost("/register", async (HttpContext http, AuthenticationService service, JwtIssuer issuer, ProtectedJwtKey key,
            IConfiguration config, IHostEnvironment env, CancellationToken ct) => {
            if (!AllowRegistration(config, env)) return Results.Problem(statusCode: 403, title: "Cadastro indisponível.", detail: "Solicite uma conta ao administrador.");
            var request = await ReadAsync(http.Request, ct); _ = key.SigningKey;
            Guid? initial = null;
            if (env.IsDevelopment() && config.GetValue<bool>("FlowForge:Authentication:AllowInitialSetup")) {
                var configured = config["FlowForge:Authentication:InitialOwnerId"] ?? config["FlowForge:TechnicalOwnerId"];
                if (configured is null) initial = Guid.NewGuid();
                else if (Guid.TryParse(configured, out var value) && value != Guid.Empty) initial = value;
                else throw new RuntimeConfigurationException();
            }
            var grant = await service.RegisterAsync(request.Email, request.Password, initial, ct);
            SetCookie(http, grant); return Results.Json(issuer.Issue(grant.Session), statusCode: 201);
        }).Accepts<SignInRequest>("application/json").RequireRateLimiting("login").WithName("RegisterAccount");
        group.MapPost("/login", async (HttpContext http, AuthenticationService service, JwtIssuer issuer, ProtectedJwtKey key, CancellationToken ct) => {
            var request = await ReadAsync(http.Request, ct); _ = key.SigningKey;
            var grant = await service.LoginAsync(request.Email, request.Password, ct);
            SetCookie(http, grant); return Results.Ok(issuer.Issue(grant.Session));
        }).Accepts<SignInRequest>("application/json").RequireRateLimiting("login").WithName("Login");
        group.MapPost("/refresh", async (HttpContext http, AuthenticationService service, JwtIssuer issuer, CancellationToken ct) => {
            if (!http.Request.Cookies.TryGetValue(Cookie, out var token)) throw new AuthenticationRejectedException();
            try {
                var grant = await service.RefreshAsync(token, ct); SetCookie(http, grant); return Results.Ok(issuer.Issue(grant.Session));
            } catch (AuthenticationRejectedException) { http.Response.Cookies.Delete(Cookie, CookieOptions(http)); throw; }
        }).WithName("RefreshSession");
        group.MapPost("/logout", async (HttpContext http, AuthenticationService service, CancellationToken ct) => {
            if (http.Request.Cookies.TryGetValue(Cookie, out var token)) {
                try { await service.LogoutAsync(token, ct); } catch (AuthenticationRejectedException) { }
            }
            http.Response.Cookies.Delete(Cookie, CookieOptions(http)); return Results.Ok(new { signedOut = true });
        }).WithName("Logout");
        app.MapPost("/api/sessions/revoke", async (CurrentOwner owner, AuthenticationService service, CancellationToken ct) => {
            await service.RevokeAllAsync(owner.Id, ct); return Results.Ok(new { revoked = true });
        }).RequireAuthorization().RequireRateLimiting("account").WithTags("Autenticação");
        app.MapGet("/api/admin/users", async (IAuthenticationStore store, CancellationToken ct) => Results.Ok(await store.ListAsync(ct)))
            .RequireAuthorization("Admin").RequireRateLimiting("account").WithTags("Admin");
        app.MapPost("/api/admin/users", async (HttpContext http, AuthenticationService service, CancellationToken ct) => {
            var request = await ReadAsync(http.Request, ct);
            var user = await service.CreateOwnerAsync(request.Email, request.Password, ct); return Results.Json(user, statusCode: 201);
        }).RequireAuthorization("Admin").RequireRateLimiting("account").WithTags("Admin").Accepts<SignInRequest>("application/json");
        app.MapPost("/api/admin/users/{id:guid}/sessions/revoke", async (Guid id, AuthenticationService service, CancellationToken ct) => {
            await service.RevokeAllAsync(id, ct); return Results.Ok(new { revoked = true });
        }).RequireAuthorization("Admin").RequireRateLimiting("account").WithTags("Admin");
    }
    private static bool AllowRegistration(IConfiguration config, IHostEnvironment env) => env.IsDevelopment() && config.GetValue<bool>("FlowForge:Authentication:AllowRegistration");
    private static CookieOptions CookieOptions(HttpContext http) => new() {
        HttpOnly = true, Secure = http.Request.IsHttps || !http.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment(),
        SameSite = SameSiteMode.Strict, Path = "/api/auth", IsEssential = true
    };
    private static void SetCookie(HttpContext http, SessionGrant grant) {
        var options = CookieOptions(http); options.Expires = grant.Session.ExpiresAt;
        http.Response.Cookies.Append(Cookie, grant.RefreshToken, options);
    }
    private static async Task<SignInRequest> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasJsonContentType() || request.Headers.ContentEncoding.Count > 0) throw new BadHttpRequestException("JSON sem compressão obrigatório.", 415);
        const int max = 8192;
        if (request.ContentLength > max) throw new BadHttpRequestException("Conteúdo excedido.", 413);
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = max;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var body = new MemoryStream(); var buffer = new byte[4096];
        try {
            while (true) {
                var count = await request.Body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, max + 1 - (int)body.Length)), timeout.Token);
                if (count == 0) break; body.Write(buffer, 0, count);
                if (body.Length > max) throw new BadHttpRequestException("Conteúdo excedido.", 413);
            }
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new BadHttpRequestException("Tempo excedido.", 408); }
        var bytes = body.ToArray(); _ = new System.Text.UTF8Encoding(false, true).GetCharCount(bytes);
        var options = request.HttpContext.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        return JsonSerializer.Deserialize<SignInRequest>(bytes, options) ?? throw new JsonException();
    }
}

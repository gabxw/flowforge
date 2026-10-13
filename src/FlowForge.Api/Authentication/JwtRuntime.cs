using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using FlowForge.Application.Users;
using FlowForge.Infrastructure.Persistence;
using FlowForge.Infrastructure.Runtime;
using FlowForge.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;
namespace FlowForge.Api.Authentication;
internal sealed class ProtectedJwtKey(IConfiguration config, IHostEnvironment environment, IDataProtectionProvider protection)
{
    private readonly Lazy<byte[]> key = new(() => Load(config, environment, protection));
    public SymmetricSecurityKey SigningKey => new(key.Value);
    private static byte[] Load(IConfiguration config, IHostEnvironment environment, IDataProtectionProvider protection)
    {
        try
        {
            if (config["FlowForge:Authentication:SigningKey"] is { } supplied)
            {
                var bytes = Convert.FromBase64String(supplied);
                if (bytes.Length != 64) throw new RuntimeConfigurationException(); return bytes;
            }
            var directory = config["FlowForge:DataProtection:KeyRingPath"];
            if (directory is null || !Path.IsPathFullyQualified(directory)) throw new RuntimeConfigurationException();
            var path = Path.Combine(directory, "jwt-signing.protected");
            var protector = protection.CreateProtector("FlowForge.Authentication.SigningKey.v1");
            if (!File.Exists(path))
            {
                if (!environment.IsDevelopment()) throw new RuntimeConfigurationException();
                Directory.CreateDirectory(directory);
                var temporary = path + "." + Guid.NewGuid().ToString("N");
                try
                {
                    File.WriteAllBytes(temporary, protector.Protect(RandomNumberGenerator.GetBytes(64)));
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    try { File.Move(temporary, path, false); }
                    catch (IOException) when (File.Exists(path)) { }
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            if (new FileInfo(path).Length is < 1 or > 4096) throw new RuntimeConfigurationException();
            var result = protector.Unprotect(File.ReadAllBytes(path));
            if (result.Length != 64) throw new RuntimeConfigurationException(); return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or FormatException)
        { throw new RuntimeConfigurationException(); }
    }
}
internal sealed record AccessGrant(string AccessToken, DateTimeOffset ExpiresAt, FlowForge.Domain.Users.UserIdentity User)
{
    public override string ToString() => "AccessGrant [redacted]";
}
internal sealed class JwtIssuer(ProtectedJwtKey key, TimeProvider clock)
{
    public AccessGrant Issue(SessionIdentity session)
    {
        var now = clock.GetUtcNow(); var expires = now.AddMinutes(5);
        if (expires > session.ExpiresAt) expires = session.ExpiresAt;
        var token = new JwtSecurityToken("flowforge", "flowforge-api",
            [new("sub", session.User.Id.ToString()), new("role", session.User.Role.ToString()),
             new("sid", session.Id.ToString()), new("jti", Guid.NewGuid().ToString()),
             new("iat", now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)],
            now.UtcDateTime, expires.UtcDateTime, new SigningCredentials(key.SigningKey, SecurityAlgorithms.HmacSha512));
        return new(new JwtSecurityTokenHandler().WriteToken(token), expires, session.User);
    }
}
internal static class AuthenticationRuntime
{
    public static void AddAuthenticationRuntime(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<ProtectedJwtKey>(); services.AddSingleton<JwtIssuer>();
        services.AddSingleton<IPasswordHashing, IdentityPasswordHashing>();
        services.AddSingleton<IAuthSecrets, AuthenticationSecrets>();
        services.AddScoped<IAuthenticationStore, PostgresAuthenticationStore>();
        services.AddScoped<AuthenticationService>();
        services.AddAntiforgery(options => {
            options.HeaderName = "X-CSRF-Token"; options.Cookie.Name = "flowforge.csrf";
            options.Cookie.Path = "/api/auth"; options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.HttpOnly = true; options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<IServiceProvider>((options, sp) => {
            options.MapInboundClaims = false; options.IncludeErrorDetails = false; options.SaveToken = false;
            options.TokenValidationParameters = new() {
                ValidateIssuer = true, ValidIssuer = "flowforge", ValidateAudience = true, ValidAudience = "flowforge-api",
                ValidateLifetime = true, RequireExpirationTime = true, RequireSignedTokens = true, ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.Zero, ValidAlgorithms = [SecurityAlgorithms.HmacSha512], NameClaimType = "sub", RoleClaimType = "role",
                IssuerSigningKeyResolver = (_, _, _, _) => [sp.GetRequiredService<ProtectedJwtKey>().SigningKey]
            };
            options.Events = new() {
                OnTokenValidated = async context => {
                    var user = context.Principal!;
                    if (!Guid.TryParse(user.FindFirstValue("sub"), out var id) || id == Guid.Empty ||
                        !Guid.TryParse(user.FindFirstValue("sid"), out var session) ||
                        !await context.HttpContext.RequestServices.GetRequiredService<IAuthenticationStore>().IsActiveAsync(id, session,
                            context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow(), context.HttpContext.RequestAborted))
                        context.Fail("Sessão inválida.");
                },
                OnChallenge = async context => {
                    context.HandleResponse(); context.Response.Headers.WWWAuthenticate = "Bearer";
                    await Results.Problem(statusCode: 401, title: "Sessão inválida.", detail: "Entre novamente.").ExecuteAsync(context.HttpContext);
                },
                OnForbidden = async context => await Results.Problem(statusCode: 403, title: "Acesso negado.", detail: "Sua conta não tem essa permissão.").ExecuteAsync(context.HttpContext)
            };
        });
        services.AddAuthorization(options => {
            var owner = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireRole("Owner", "Admin").Build();
            options.DefaultPolicy = owner; options.FallbackPolicy = owner;
            options.AddPolicy("Admin", p => p.RequireAuthenticatedUser().RequireRole("Admin"));
        });
        services.AddOptions<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>().Configure<IConfiguration>((options, configuration) => {
            options.AddPolicy("login", _ => RateLimitPartition.GetFixedWindowLimiter("login", _ => new() { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("auth", _ => RateLimitPartition.GetFixedWindowLimiter("auth", _ => new() { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            var accountLimit = configuration.GetValue<int?>("FlowForge:Authentication:AccountPermitLimit") ?? 120;
            if (accountLimit is < 1 or > 10000) throw new RuntimeConfigurationException();
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => context.User.Identity?.IsAuthenticated == true
                ? RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirstValue("sub")!, _ => new() { PermitLimit = accountLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                : RateLimitPartition.GetNoLimiter("anonymous"));
            options.AddPolicy("account", context => RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirstValue("sub") ?? "anonymous",
                _ => new() { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }
}

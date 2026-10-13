using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Mvc.Testing;
using FlowForge.Application.Users;
using FlowForge.Infrastructure.Security;
using Xunit;
namespace FlowForge.Api.Tests;
[Collection("Workflow API")]
public sealed class AuthenticationEndpointTests(WorkflowApiFixture fixture)
{
    private static HttpClient Client(WebApplicationFactory<Program> api, bool cookies = true) => api.CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = cookies });
    private static async Task Csrf(HttpClient client)
    {
        using var r = await client.GetAsync("/api/auth/csrf"); Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var json = await r.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Remove("X-CSRF-Token"); client.DefaultRequestHeaders.Add("X-CSRF-Token", json.GetProperty("csrfToken").GetString());
        if (!r.Headers.TryGetValues("Set-Cookie", out var cookies)) return;
        var cookie = cookies.FirstOrDefault(x => x.StartsWith("flowforge.csrf="));
        if (cookie is not null) { client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]); }
    }
    private sealed record Grant(string AccessToken, string RefreshToken, Guid UserId, string Role, string CookieHeader)
    { public override string ToString() => "Grant [redacted]"; }
    private static async Task<Grant> ReadGrant(HttpResponseMessage r)
    {
        var json = await r.Content.ReadFromJsonAsync<JsonElement>();
        var cookie = r.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("flowforge.refresh="));
        return new(json.GetProperty("accessToken").GetString()!, cookie.Split(';')[0].Split('=', 2)[1], json.GetProperty("user").GetProperty("id").GetGuid(), json.GetProperty("user").GetProperty("role").GetString()!, cookie);
    }
    private async Task<Grant> Login(HttpClient client)
    {
        await Csrf(client); using var r = await client.PostAsJsonAsync("/api/auth/login", new {email=fixture.AdminEmail,password=fixture.AdminPassword});
        Assert.Equal(HttpStatusCode.OK, r.StatusCode); return await ReadGrant(r);
    }
    private static async Task<Grant> Register(HttpClient client)
    {
        await Csrf(client); using var r = await client.PostAsJsonAsync("/api/auth/register", new {email="owner-"+Guid.NewGuid().ToString("N")+"@flowforge.local",password=Guid.NewGuid().ToString("N")});
        Assert.Equal(HttpStatusCode.Created, r.StatusCode); return await ReadGrant(r);
    }
    private static Task<HttpResponseMessage> Bearer(HttpClient client, string token, string path, HttpMethod? method = null, object? body = null)
    {
        var message = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        message.Headers.Authorization = new("Bearer", token); if (body is not null) message.Content=JsonContent.Create(body);
        return client.SendAsync(message);
    }
    private static async Task<HttpResponseMessage> RefreshWith(WebApplicationFactory<Program> api, string token)
    {
        using var client=Client(api,false);await Csrf(client);
        var anti=client.DefaultRequestHeaders.GetValues("Cookie").Single();client.DefaultRequestHeaders.Remove("Cookie");client.DefaultRequestHeaders.Add("Cookie",anti+"; flowforge.refresh="+token);
        return await client.PostAsync("/api/auth/refresh",null);
    }
    [Fact]
    public async Task Anonymous_cannot_access_workflows_but_liveness_remains_public()
    {
        using var api=fixture.CreateAuthenticationApi();using var client=Client(api);
        using var denied=await client.GetAsync("/api/workflows");Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);
        Assert.Contains(denied.Headers.WwwAuthenticate, x=>x.Scheme=="Bearer");
        using var live=await client.GetAsync("/health/live");Assert.Equal(HttpStatusCode.OK,live.StatusCode);
    }
    [Fact]
    public async Task Login_issues_short_lived_JWT_and_HttpOnly_Strict_cookie_without_refresh_in_body()
    {
        using var api=fixture.CreateAuthenticationApi();using var client=Client(api);var grant=await Login(client);
        var jwt=new JwtSecurityTokenHandler().ReadJwtToken(grant.AccessToken);
        Assert.Equal("HS512",jwt.Header.Alg);Assert.True(jwt.ValidTo>DateTime.UtcNow && jwt.ValidTo<=DateTime.UtcNow.AddMinutes(5));
        Assert.True(grant.CookieHeader.Contains("httponly",StringComparison.OrdinalIgnoreCase));Assert.True(grant.CookieHeader.Contains("secure",StringComparison.OrdinalIgnoreCase));Assert.True(grant.CookieHeader.Contains("samesite=strict",StringComparison.OrdinalIgnoreCase));
        using var allowed=await Bearer(client,grant.AccessToken,"/api/workflows");Assert.Equal(HttpStatusCode.OK,allowed.StatusCode);
        using var fresh=await RefreshWith(api,grant.RefreshToken);Assert.Equal(HttpStatusCode.OK,fresh.StatusCode);
        var body=await fresh.Content.ReadAsStringAsync();Assert.False(body.Contains(grant.RefreshToken,StringComparison.Ordinal));Assert.False(body.Contains(fixture.AdminPassword,StringComparison.Ordinal));
    }
    [Fact]
    public async Task Refresh_rotation_replay_revokes_the_family_and_issued_access_tokens()
    {
        using var api=fixture.CreateAuthenticationApi();using var client=Client(api);var original=await Login(client);
        using var first=await RefreshWith(api,original.RefreshToken);Assert.Equal(HttpStatusCode.OK,first.StatusCode);var next=await ReadGrant(first);
        Assert.False(next.RefreshToken==original.RefreshToken);
        using var replay=await RefreshWith(api,original.RefreshToken);Assert.Equal(HttpStatusCode.Unauthorized,replay.StatusCode);
        using var revoked=await RefreshWith(api,next.RefreshToken);Assert.Equal(HttpStatusCode.Unauthorized,revoked.StatusCode);
        using var denied=await Bearer(client,next.AccessToken,"/api/workflows");Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);
    }
    [Fact]
    public async Task Concurrent_reuse_has_one_rotation_then_revokes_even_the_winning_grant()
    {
        using var api=fixture.CreateAuthenticationApi();using var client=Client(api);var grant=await Login(client);
        var responses=await Task.WhenAll(RefreshWith(api,grant.RefreshToken),RefreshWith(api,grant.RefreshToken));
        try {
            Assert.Equal([HttpStatusCode.OK,HttpStatusCode.Unauthorized], responses.Select(r=>r.StatusCode).OrderBy(x=>(int)x));
            var winner=await ReadGrant(responses.Single(r=>r.StatusCode==HttpStatusCode.OK));
            using var denied=await Bearer(client,winner.AccessToken,"/api/workflows");Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);
        } finally {foreach(var r in responses)r.Dispose();}
    }
    [Fact]
    public async Task Logout_invalidates_current_session_and_is_idempotent()
    {
        using var api=fixture.CreateAuthenticationApi();using var client=Client(api);var grant=await Login(client);
        using var logout=await client.PostAsync("/api/auth/logout",null);Assert.Equal(HttpStatusCode.OK,logout.StatusCode);
        using var twice=await client.PostAsync("/api/auth/logout",null);Assert.Equal(HttpStatusCode.OK,twice.StatusCode);
        using var denied=await Bearer(client,grant.AccessToken,"/api/workflows");Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);
    }
    [Theory]
    [InlineData("login")]
    [InlineData("register")]
    [InlineData("refresh")]
    [InlineData("logout")]
    public async Task Cookie_commands_require_antiforgery_token(string path)
    {
        using var api=fixture.CreateAuthenticationApi(true);using var client=Client(api);
        using var denied=await client.PostAsJsonAsync("/api/auth/"+path,new {email=fixture.AdminEmail,password=fixture.AdminPassword});Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
    }
    [Fact]
    public async Task Expired_family_does_not_rotate_or_extend_its_absolute_lifetime()
    {
        using var api=fixture.CreateAuthenticationApi();using var client=Client(api);
        var secrets=new AuthenticationSecrets();var token=secrets.Create();var now=DateTimeOffset.UtcNow.AddDays(-8);
        await fixture.AuthenticationStore.CreateSessionAsync(fixture.AdminId,secrets.Hash(token),now,now.AddDays(7),CancellationToken.None);
        using var denied=await RefreshWith(api,token);Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);
    }
    [Theory]
    [InlineData("expired")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("algorithm")]
    [InlineData("signature")]
    public async Task JWT_validation_rejects_expiry_issuer_audience_algorithm_and_signature(string kind)
    {
        using var api=fixture.CreateAuthenticationApi();using var client=Client(api);var grant=await Login(client);
        var original=new JwtSecurityTokenHandler().ReadJwtToken(grant.AccessToken);var now=DateTime.UtcNow;
        var bytes=Convert.FromBase64String(fixture.SigningKey);if(kind=="signature")bytes=System.Security.Cryptography.RandomNumberGenerator.GetBytes(64);
        var invalid=new JwtSecurityToken(kind=="issuer"?"wrong":"flowforge",kind=="audience"?"wrong":"flowforge-api",original.Claims.Where(c => c.Type is not "iss" and not "aud" and not "exp" and not "nbf" and not "iat"),
            kind=="expired"?now.AddMinutes(-10):now.AddSeconds(-1),kind=="expired"?now.AddMinutes(-5):now.AddMinutes(5),new SigningCredentials(new SymmetricSecurityKey(bytes),kind=="algorithm"?SecurityAlgorithms.HmacSha256:SecurityAlgorithms.HmacSha512));
        using var denied=await Bearer(client,new JwtSecurityTokenHandler().WriteToken(invalid),"/api/workflows");Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);
    }
    [Fact]
    public async Task Owner_isolated_by_object_and_Admin_cannot_bypass_ownership()
    {
        using var api=fixture.CreateAuthenticationApi(true);using var first=Client(api);using var other=Client(api);using var admin=Client(api);
        var a=await Register(first);var b=await Register(other);var root=await Login(admin);
        using var created=await Bearer(first,a.AccessToken,"/api/workflows",HttpMethod.Post,new {name="Isolamento real"});Assert.Equal(HttpStatusCode.Created,created.StatusCode);
        var id=(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var denied=await Bearer(other,b.AccessToken,"/api/workflows/"+id);Assert.Equal(HttpStatusCode.NotFound,denied.StatusCode);
        using var privileged=await Bearer(admin,root.AccessToken,"/api/workflows/"+id);Assert.Equal(HttpStatusCode.NotFound,privileged.StatusCode);
        using var roleDenied=await Bearer(first,a.AccessToken,"/api/admin/users");Assert.Equal(HttpStatusCode.Forbidden,roleDenied.StatusCode);
        using var users=await Bearer(admin,root.AccessToken,"/api/admin/users");Assert.Equal(HttpStatusCode.OK,users.StatusCode);
        using var spoof=await Bearer(first,a.AccessToken,"/api/workflows",HttpMethod.Post,new {name="Spoof",ownerUserId=b.UserId});Assert.Equal(HttpStatusCode.BadRequest,spoof.StatusCode);
    }
    [Fact]
    public async Task Credential_reads_and_rotation_require_the_owner_even_with_valid_JWT()
    {
        using var api=fixture.CreateAuthenticationApi(true);using var first=Client(api);using var other=Client(api);var a=await Register(first);var b=await Register(other);
        using var created=await Bearer(first,a.AccessToken,"/api/credentials",HttpMethod.Post,new {name="Fictícia",type="apiKey",origin="https://example.com",headerName="X-Api-Key",secret=Guid.NewGuid().ToString("N")});Assert.Equal(HttpStatusCode.Created,created.StatusCode);
        var id=(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var read=await Bearer(other,b.AccessToken,"/api/credentials/"+id);Assert.Equal(HttpStatusCode.NotFound,read.StatusCode);
        using var rotate=await Bearer(other,b.AccessToken,"/api/credentials/"+id+"/rotate-secret",HttpMethod.Post,new {expectedRevision=1,secret=Guid.NewGuid().ToString("N")});Assert.Equal(HttpStatusCode.NotFound,rotate.StatusCode);
    }
    [Fact]
    public async Task Admin_can_revoke_families_but_Owner_cannot_revoke_another_account()
    {
        using var api=fixture.CreateAuthenticationApi(true);using var owner=Client(api);using var admin=Client(api);var a=await Register(owner);var root=await Login(admin);
        using var denied=await Bearer(owner,a.AccessToken,"/api/admin/users/"+fixture.AdminId+"/sessions/revoke",HttpMethod.Post);Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
        using var revoke=await Bearer(admin,root.AccessToken,"/api/admin/users/"+a.UserId+"/sessions/revoke",HttpMethod.Post);Assert.Equal(HttpStatusCode.OK,revoke.StatusCode);
        using var invalid=await Bearer(owner,a.AccessToken,"/api/workflows");Assert.Equal(HttpStatusCode.Unauthorized,invalid.StatusCode);
    }
    [Fact]
    public async Task Registration_cannot_supply_role_and_production_does_not_offer_public_registration()
    {
        using var api=fixture.CreateAuthenticationApi(true);using var client=Client(api);await Csrf(client);
        using var spoof=await client.PostAsJsonAsync("/api/auth/register",new {email="safe@flowforge.local",password=Guid.NewGuid().ToString("N"),role="admin"});Assert.Equal(HttpStatusCode.BadRequest,spoof.StatusCode);
        using var production=fixture.CreateAuthenticationApi();using var closed=Client(production);await Csrf(closed);
        using var denied=await closed.PostAsJsonAsync("/api/auth/register",new {email="safe@flowforge.local",password=Guid.NewGuid().ToString("N")});Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
    }
    [Fact]
    public async Task Rate_limits_are_partitioned_by_authenticated_account()
    {
        using var api=fixture.CreateAuthenticationApi(true);using var first=Client(api);using var other=Client(api);
        var a=await Register(first);var b=await Register(other);bool limited=false;
        for(var i=0;i<125;i++) {using var r=await Bearer(first,a.AccessToken,"/api/workflows");if(r.StatusCode==HttpStatusCode.TooManyRequests){limited=true;break;}Assert.Equal(HttpStatusCode.OK,r.StatusCode);}
        Assert.True(limited);using var allowed=await Bearer(other,b.AccessToken,"/api/workflows");Assert.Equal(HttpStatusCode.OK,allowed.StatusCode);
    }
    [Fact]
    public async Task Repeated_wrong_password_locks_only_the_existing_account()
    {
        using var api=fixture.CreateAuthenticationApi(true);using var client=Client(api);await Csrf(client);
        var email="lock-"+Guid.NewGuid().ToString("N")+"@flowforge.local";var password=Guid.NewGuid().ToString("N");
        using var created=await client.PostAsJsonAsync("/api/auth/register",new{email,password});Assert.Equal(HttpStatusCode.Created,created.StatusCode);
        for(var i=0;i<5;i++){using var bad=await client.PostAsJsonAsync("/api/auth/login",new{email,password="incorrect-long-password"});Assert.Equal(HttpStatusCode.Unauthorized,bad.StatusCode);}
        using var locked=await client.PostAsJsonAsync("/api/auth/login",new{email,password});Assert.Equal(HttpStatusCode.Unauthorized,locked.StatusCode);
        using var admin=Client(api);var grant=await Login(admin);using var active=await Bearer(admin,grant.AccessToken,"/api/workflows");Assert.Equal(HttpStatusCode.OK,active.StatusCode);
    }

}

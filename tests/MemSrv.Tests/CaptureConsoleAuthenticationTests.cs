using MemSrv.Core;
using MemSrv.Server;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;

namespace MemSrv.Tests;

[Collection("database")]
public sealed class CaptureConsoleAuthenticationTests : HttpSeamTestBase
{
    [Fact]
    public async Task UnauthenticatedInteractiveAccessIsChallengedByTheConfiguredOidcProvider()
    {
        var oidc = _app.Services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(CaptureConsoleAuthentication.OidcScheme);
        oidc.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
            new OpenIdConnectConfiguration
            {
                AuthorizationEndpoint = "https://authentik.test/application/o/authorize/",
            });

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{_baseUrl}/capture/console");
        request.Headers.Add("X-Forwarded-Proto", "https");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("authentik.test", response.Headers.Location!.Host);
        Assert.Contains("client_id=capture-console-test", response.Headers.Location.Query);
        Assert.Contains(
            Uri.EscapeDataString(
                $"https://{new Uri(_baseUrl).Authority}/capture/console/signin-oidc"),
            response.Headers.Location.Query);
    }

    [Fact]
    public async Task OidcAuthenticatedOperatorSubjectComesFromTheServerPrincipalNotRequestInput()
    {
        const string providerSubject = "authentik|operator-179";
        var cookie = _app.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CaptureConsoleAuthentication.CookieScheme);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", providerSubject)],
            CaptureConsoleAuthentication.OidcScheme));
        var ticket = new AuthenticationTicket(
            principal,
            new AuthenticationProperties(),
            CaptureConsoleAuthentication.CookieScheme);
        string protectedTicket = cookie.TicketDataFormat.Protect(ticket);

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add(
            "Cookie", $"{cookie.Cookie.Name}={protectedTicket}");
        using var response = await client.GetAsync(
            $"{_baseUrl}/capture/console?operator=caller-supplied");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string console = await response.Content.ReadAsStringAsync();
        Assert.Contains(providerSubject, console);
        Assert.DoesNotContain("caller-supplied", console);
    }

    [Fact]
    public async Task AgentAndCaptureBearerCredentialsCannotEnterTheConsole()
    {
        ConfigureStaticOidcChallenge();
        string captureCredential = $"mcap_{Guid.NewGuid():N}";
        await EnrollCaptureCredentialAsync(captureCredential);

        foreach (string credential in new[] { AgentAKey, captureCredential })
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", credential);

            using var response = await client.GetAsync($"{_baseUrl}/capture/console");

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("authentik.test", response.Headers.Location!.Host);
        }
    }

    [Fact]
    public async Task CaptureImportRemainsAvailableWhenTheOidcAuthorityIsUnavailable()
    {
        var oidc = _app.Services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(CaptureConsoleAuthentication.OidcScheme);
        oidc.ConfigurationManager = new UnavailableConfigurationManager();

        using (var console = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
        {
            using var refused = await console.GetAsync($"{_baseUrl}/capture/console");
            Assert.False(refused.IsSuccessStatusCode);
        }

        string captureCredential = $"mcap_{Guid.NewGuid():N}";
        await EnrollCaptureCredentialAsync(captureCredential);
        using var capture = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        capture.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", captureCredential);
        using var accepted = await capture.PostAsJsonAsync(
            "/capture/v1/observations",
            Observation());

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public void HttpHostRequiresCompleteOidcConfiguration()
    {
        var options = RuntimeOptions();
        options.CaptureConsoleOidc.ClientId = "";

        var failure = Assert.Throws<InvalidOperationException>(
            () => HttpServerHost.Build(options, AgentKeyStore.Load(_keysPath)));

        Assert.Contains("client id is required", failure.Message);
    }

    [Fact]
    public void HttpHostWiresStandardAuthentikCompatibleOidcCodeFlow()
    {
        var oidc = _app.Services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(CaptureConsoleAuthentication.OidcScheme);

        Assert.Equal(
            "https://authentik.test/application/o/capture-console/",
            oidc.Authority);
        Assert.Equal("capture-console-test", oidc.ClientId);
        Assert.Equal("code", oidc.ResponseType);
        Assert.Equal(CaptureConsoleAuthentication.CookieScheme, oidc.SignInScheme);
        Assert.False(oidc.MapInboundClaims);
        Assert.Contains("openid", oidc.Scope);
    }

    private void ConfigureStaticOidcChallenge()
    {
        var oidc = _app.Services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(CaptureConsoleAuthentication.OidcScheme);
        oidc.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
            new OpenIdConnectConfiguration
            {
                AuthorizationEndpoint = "https://authentik.test/application/o/authorize/",
            });
    }

    private async Task EnrollCaptureCredentialAsync(string credential)
    {
        string path = Path.Combine(Path.GetTempPath(), $"capture-key-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, credential);
        try
        {
            await RunMemCtlAsync(
                "capture", "enroll", $"console-auth-{Guid.NewGuid():N}",
                "--harness", "codex",
                "--agent-id", $"capture:console-auth-{Guid.NewGuid():N}",
                "--credential-file", path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static object Observation()
    {
        string marker = Guid.NewGuid().ToString("N");
        return new
        {
            contractVersion = 1,
            sourceSessionId = $"console-auth-session-{marker}",
            sourcePosition = 0,
            locator = new { kind = "native_id", nativeId = $"record-{marker}" },
            source = new { harness = "codex", harnessVersion = "synthetic", recordType = "turn" },
            adapter = new { name = "codex-synthetic", version = "1" },
            sourcePayload = new { message = "OIDC-independent capture" },
            events = new[]
            {
                new
                {
                    partKey = "message/0",
                    partOrder = 0,
                    kind = "message",
                    actor = "user",
                    payload = new { text = "OIDC-independent capture" },
                },
            },
        };
    }

    private sealed class UnavailableConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
            Task.FromException<OpenIdConnectConfiguration>(
                new HttpRequestException("Synthetic OIDC authority outage."));

        public void RequestRefresh()
        {
        }
    }
}

using MemSrv.Core;
using MemSrv.Server;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace MemSrv.Tests;

[Collection("database")]
public sealed class CaptureConsoleAuthenticationTests : HttpSeamTestBase
{
    [Fact]
    public async Task ConsoleIsUnavailableWhenOidcConfigurationIsAbsent()
    {
        var options = RuntimeOptions();
        options.CaptureConsoleOidc = new();
        var app = HttpServerHost.Build(options, AgentKeyStore.Load(_keysPath));
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        try
        {
            string baseUrl = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
                .Addresses.First();
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

            using var console = await client.GetAsync($"{baseUrl}/capture/console");

            Assert.Equal(HttpStatusCode.NotFound, console.StatusCode);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

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
        Assert.Contains("response_type=code", response.Headers.Location.Query);
        Assert.Contains("scope=openid", response.Headers.Location.Query);
        Assert.Contains(
            Uri.EscapeDataString(
                $"https://{new Uri(_baseUrl).Authority}/capture/console/signin-oidc"),
            response.Headers.Location.Query);
    }

    [Fact]
    public async Task AuthorizationCodeCallbackIssuesFixedNonSlidingSessionForProviderSubject()
    {
        const string providerSubject = "authentik|operator-179";
        using var provider = ConfigureFakeOidcProvider(providerSubject);

        OidcSignIn signIn = await CompleteOidcSignInAsync(provider);

        var cookie = _app.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CaptureConsoleAuthentication.CookieScheme);
        AuthenticationTicket ticket = Assert.IsType<AuthenticationTicket>(
            cookie.TicketDataFormat.Unprotect(signIn.ProtectedTicket));

        Assert.Equal(providerSubject, ticket.Principal.FindFirstValue("sub"));
        Assert.NotNull(ticket.Properties.IssuedUtc);
        Assert.NotNull(ticket.Properties.ExpiresUtc);
        Assert.Equal(
            TimeSpan.FromHours(8),
            ticket.Properties.ExpiresUtc.Value - ticket.Properties.IssuedUtc.Value);
        Assert.False(cookie.SlidingExpiration);
        Assert.Equal(1, provider.TokenExchangeCount);
        Assert.Equal("synthetic-authorization-code", provider.ExchangedCode);

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", signIn.CookieHeader);
        using var response = await client.GetAsync(
            $"{_baseUrl}/capture/console?operator=caller-supplied");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out _));
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
    public async Task ProviderOutagePreservesLocalSessionOnlyUntilFixedExpirationAndOtherHttpSurfacesRemainAvailable()
    {
        using var provider = ConfigureFakeOidcProvider("authentik|outage-operator");
        OidcSignIn signIn = await CompleteOidcSignInAsync(provider);

        var oidc = _app.Services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(CaptureConsoleAuthentication.OidcScheme);
        oidc.ConfigurationManager = new UnavailableConfigurationManager();

        var cookie = _app.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CaptureConsoleAuthentication.CookieScheme);
        Assert.Equal(TimeSpan.FromHours(8), cookie.ExpireTimeSpan);
        Assert.False(cookie.SlidingExpiration);

        using (var console = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
        {
            using var refused = await console.GetAsync($"{_baseUrl}/capture/console");
            Assert.False(refused.IsSuccessStatusCode);

            console.DefaultRequestHeaders.Add("Cookie", signIn.CookieHeader);
            using var acceptedSession = await console.GetAsync($"{_baseUrl}/capture/console");
            Assert.Equal(HttpStatusCode.OK, acceptedSession.StatusCode);
            Assert.False(acceptedSession.Headers.TryGetValues("Set-Cookie", out _));
        }

        using (var expiredConsole = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
        {
            expiredConsole.DefaultRequestHeaders.Add("Cookie", ConsoleCookie(
                cookie, DateTimeOffset.UtcNow.AddHours(-9), DateTimeOffset.UtcNow.AddHours(-1)));
            using var expiredSession = await expiredConsole.GetAsync($"{_baseUrl}/capture/console");
            Assert.False(expiredSession.IsSuccessStatusCode);
        }

        using var health = new HttpClient();
        using var healthy = await health.GetAsync($"{_baseUrl}/healthz");
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);

        await using var mcp = await ConnectAsync(AgentAKey);

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
    public void HttpHostRejectsPartialOidcConfigurationWithoutDisclosingItsSecret()
    {
        var options = RuntimeOptions();
        options.CaptureConsoleOidc.ClientId = "";
        options.CaptureConsoleOidc.ClientSecret = "secret-that-must-not-appear";

        var failure = Assert.Throws<InvalidOperationException>(
            () => HttpServerHost.Build(options, AgentKeyStore.Load(_keysPath)));

        Assert.Contains("must provide authority, client id, and client secret together", failure.Message);
        Assert.DoesNotContain(options.CaptureConsoleOidc.ClientSecret, failure.Message);
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

    private FakeOidcProvider ConfigureFakeOidcProvider(string providerSubject)
    {
        var provider = new FakeOidcProvider(
            providerSubject,
            $"https://{new Uri(_baseUrl).Authority}/capture/console/signin-oidc");
        var oidc = _app.Services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(CaptureConsoleAuthentication.OidcScheme);
        oidc.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
            provider.Configuration);
        oidc.Backchannel = new HttpClient(provider);
        return provider;
    }

    private async Task<OidcSignIn> CompleteOidcSignInAsync(FakeOidcProvider provider)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var challenge = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/capture/console");
        challenge.Headers.Add("X-Forwarded-Proto", "https");
        using var challenged = await client.SendAsync(challenge);

        Assert.Equal(HttpStatusCode.Redirect, challenged.StatusCode);
        Uri authorization = Assert.IsType<Uri>(challenged.Headers.Location);
        var parameters = ParseQuery(authorization.Query);
        provider.Nonce = parameters["nonce"];
        Assert.False(string.IsNullOrWhiteSpace(parameters["state"]));

        string correlationCookies = string.Join("; ", challenged.Headers.GetValues("Set-Cookie")
            .Select(header => header[..header.IndexOf(';')]));
        using var callback = new HttpRequestMessage(
            HttpMethod.Post, $"{_baseUrl}/capture/console/signin-oidc");
        callback.Headers.Add("X-Forwarded-Proto", "https");
        callback.Headers.Add("Cookie", correlationCookies);
        callback.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = "synthetic-authorization-code",
            ["state"] = parameters["state"],
        });
        using var completed = await client.SendAsync(callback);

        Assert.Equal(HttpStatusCode.Redirect, completed.StatusCode);
        Assert.Equal("/capture/console", completed.Headers.Location?.OriginalString);
        string applicationCookie = completed.Headers.GetValues("Set-Cookie")
            .Single(header => header.StartsWith("__Secure-MemSrv-CaptureConsole=", StringComparison.Ordinal));
        string cookiePair = applicationCookie[..applicationCookie.IndexOf(';')];
        return new OidcSignIn(cookiePair, cookiePair[(cookiePair.IndexOf('=') + 1)..]);
    }

    private static Dictionary<string, string> ParseQuery(string query) => query
        .TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .ToDictionary(
            part => Uri.UnescapeDataString(part[0]),
            part => Uri.UnescapeDataString(part.Length == 2 ? part[1] : ""),
            StringComparer.Ordinal);

    private static string ConsoleCookie(
        CookieAuthenticationOptions cookie, DateTimeOffset issuedUtc, DateTimeOffset expiresUtc)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "authentik|outage-operator")],
            CaptureConsoleAuthentication.OidcScheme));
        var ticket = new AuthenticationTicket(
            principal,
            new AuthenticationProperties
            {
                IssuedUtc = issuedUtc,
                ExpiresUtc = expiresUtc,
                AllowRefresh = false,
            },
            CaptureConsoleAuthentication.CookieScheme);
        return $"{cookie.Cookie.Name}={cookie.TicketDataFormat.Protect(ticket)}";
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

    private sealed record OidcSignIn(string CookieHeader, string ProtectedTicket);

    private sealed class FakeOidcProvider : HttpMessageHandler, IDisposable
    {
        private const string Issuer = "https://authentik.test/application/o/capture-console/";
        private readonly RSA _rsa = RSA.Create(2048);
        private readonly string _providerSubject;

        public FakeOidcProvider(string providerSubject, string expectedRedirectUri)
        {
            _providerSubject = providerSubject;
            ExpectedRedirectUri = expectedRedirectUri;
            var signingKey = new RsaSecurityKey(_rsa) { KeyId = "synthetic-oidc-signing-key" };
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256);
            Configuration = new OpenIdConnectConfiguration
            {
                Issuer = Issuer,
                AuthorizationEndpoint = $"{Issuer}authorize/",
                TokenEndpoint = $"{Issuer}token/",
            };
            Configuration.SigningKeys.Add(signingKey);
        }

        public OpenIdConnectConfiguration Configuration { get; }
        private SigningCredentials SigningCredentials { get; }
        private string ExpectedRedirectUri { get; }
        public string Nonce { get; set; } = "";
        public int TokenExchangeCount { get; private set; }
        public string? ExchangedCode { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(Configuration.TokenEndpoint, request.RequestUri?.AbsoluteUri);
            Assert.NotNull(request.Content);
            string form = await request.Content.ReadAsStringAsync(cancellationToken);
            var parameters = ParseQuery(form);
            Assert.Equal("authorization_code", parameters["grant_type"]);
            Assert.Equal("capture-console-test", parameters["client_id"]);
            Assert.Equal(ExpectedRedirectUri, parameters["redirect_uri"]);
            ExchangedCode = parameters["code"];
            Assert.True(parameters.ContainsKey("code_verifier"));
            TokenExchangeCount++;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            string idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = "capture-console-test",
                Subject = new ClaimsIdentity(
                [
                    new Claim("sub", _providerSubject),
                    new Claim("nonce", Nonce),
                ]),
                IssuedAt = now.UtcDateTime,
                NotBefore = now.AddMinutes(-1).UtcDateTime,
                Expires = now.AddMinutes(5).UtcDateTime,
                SigningCredentials = SigningCredentials,
            });
            string json = JsonSerializer.Serialize(new
            {
                access_token = "synthetic-access-token",
                token_type = "Bearer",
                expires_in = 300,
                id_token = idToken,
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _rsa.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

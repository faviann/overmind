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
using System.Text.RegularExpressions;

namespace MemSrv.Tests;

[Collection("database")]
public sealed class CaptureConsoleAuthenticationTests : HttpSeamTestBase
{
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.UtcNow);

    protected override TimeProvider RuntimeTimeProvider() => _clock;

    protected override CaptureConsoleOidcOptions ConsoleOidcOptions() => new()
    {
        Authority = "https://authentik.test/application/o/capture-console/",
        ClientId = "capture-console-test",
        ClientSecret = "test-only-client-secret",
    };

    [Fact]
    public async Task OidcOperatorCreatesSameClosedBindingScopedInstructionAsMemctl()
    {
        string stableName = $"console-instruction-{Guid.NewGuid():N}";
        string credential = $"mcap_{Guid.NewGuid():N}";
        string credentialPath = Path.Combine(
            Path.GetTempPath(), $"capture-console-key-{Guid.NewGuid():N}");
        try
        {
            await WritePrivateCredentialAsync(credentialPath, credential);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(credentialPath));
            await RunMemCtlAsync(
                "capture", "enroll", stableName,
                "--harness", "codex",
                "--agent-id", $"capture:{stableName}",
                "--credential-file", credentialPath);
            using var provider = ConfigureFakeOidcProvider("authentik|instruction-operator");
            OidcSignIn signIn = await CompleteOidcSignInAsync(provider);
            using var console = new HttpClient { BaseAddress = new Uri(_baseUrl) };
            console.DefaultRequestHeaders.Add("Cookie", signIn.CookieHeader);
            console.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
            using HttpResponseMessage created = await console.PostAsJsonAsync(
                "/capture/console/api/instructions",
                new { stableName, operation = "retry" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Guid instructionId = (await created.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("instructionId").GetGuid();

            using var runtime = new HttpClient { BaseAddress = new Uri(_baseUrl) };
            runtime.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", credential);
            JsonElement polled = await runtime.GetFromJsonAsync<JsonElement>(
                "/capture/v1/instructions");
            JsonElement instruction = Assert.Single(
                polled.GetProperty("instructions").EnumerateArray());
            Assert.Equal(instructionId, instruction.GetProperty("instructionId").GetGuid());
            Assert.Equal("retry", instruction.GetProperty("operation").GetString());
        }
        finally
        {
            File.Delete(credentialPath);
            Assert.False(File.Exists(credentialPath));
        }
    }

    [Fact]
    public async Task AuthenticatedOperatorPairsRuntimeWithoutDisclosingCredentialFromApproval()
    {
        using var provider = ConfigureFakeOidcProvider("authentik|pairing-operator");
        OidcSignIn signIn = await CompleteOidcSignInAsync(provider);
        using var runtime = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        using var created = await runtime.PostAsJsonAsync("/capture/v1/pairing-requests", new
        {
            machineName = "detected-machine",
            codexInstallationId = "detected-installation"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        JsonElement request = await created.Content.ReadFromJsonAsync<JsonElement>();
        string requestId = request.GetProperty("requestId").GetString()!;
        string userCode = request.GetProperty("userCode").GetString()!;
        string pollingToken = request.GetProperty("pollingToken").GetString()!;
        Assert.NotEqual(pollingToken, userCode);
        Assert.DoesNotContain(pollingToken, request.GetProperty("verificationUri").GetString());

        using var console = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        console.DefaultRequestHeaders.Add("Cookie", signIn.CookieHeader);
        console.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var inspected = await console.GetAsync($"/capture/console/pair/{userCode}");
        Assert.Equal(HttpStatusCode.OK, inspected.StatusCode);
        string detected = await inspected.Content.ReadAsStringAsync();
        Assert.Contains("detected-machine", detected);
        Assert.Contains("detected-installation", detected);
        Assert.Contains("authentik|pairing-operator", detected);
        Assert.Contains($"action=\"/capture/console/pair/{userCode}/approve\"", detected);
        Assert.DoesNotContain(requestId, detected);
        Assert.DoesNotContain(pollingToken, detected);

        using var tokenless = await console.PostAsync(
            $"/capture/console/pair/{userCode}/approve", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["label"] = "My Codex",
                ["allowedRepositoryPatterns"] = "example/*",
                ["specialMappings"] = "notes=homelab",
                ["specialDirectoryRoutes"] = "/workspace/notes=notes",
                ["agentId"] = "caller-must-not-control-this"
            }));
        Assert.Equal(HttpStatusCode.BadRequest, tokenless.StatusCode);

        string csrfToken = HtmlInputValue(detected, "__RequestVerificationToken");
        string csrfCookie = inspected.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(
                "__Host-MemSrv-CaptureConsole-Csrf=", StringComparison.Ordinal));
        csrfCookie = csrfCookie[..csrfCookie.IndexOf(';')];
        console.DefaultRequestHeaders.Remove("Cookie");
        console.DefaultRequestHeaders.Add("Cookie", $"{signIn.CookieHeader}; {csrfCookie}");
        using var approved = await console.PostAsync(
            $"/capture/console/pair/{userCode}/approve", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = csrfToken,
                ["label"] = "My Codex",
                ["allowedRepositoryPatterns"] = "example/*",
                ["specialMappings"] = "notes=homelab",
                ["specialDirectoryRoutes"] = "/workspace/notes=notes",
                ["agentId"] = "caller-must-not-control-this"
            }));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        string approvalBody = await approved.Content.ReadAsStringAsync();
        Assert.DoesNotContain("mcap_", approvalBody);
        Assert.DoesNotContain("caller-must-not-control-this", approvalBody);

        _clock.Advance(TimeSpan.FromMinutes(16));
        using var poll = new HttpRequestMessage(
            HttpMethod.Get, $"/capture/v1/pairing-requests/{requestId}");
        poll.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pollingToken);
        using var delivered = await runtime.SendAsync(poll);
        Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
        JsonElement result = await delivered.Content.ReadFromJsonAsync<JsonElement>();
        string credential = result.GetProperty("credential").GetString()!;
        Assert.StartsWith("mcap_", credential);

        using var replay = new HttpRequestMessage(
            HttpMethod.Get, $"/capture/v1/pairing-requests/{requestId}");
        replay.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pollingToken);
        using var replayed = await runtime.SendAsync(replay);
        Assert.Equal(HttpStatusCode.Gone, replayed.StatusCode);

        using var ingest = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        ingest.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", credential);
        using var repositoryImport = await ingest.PostAsJsonAsync(
            "/capture/v1/observations", RoutedObservation(
                "https://github.com/example/project.git", "/workspace/project"));
        Assert.Equal(HttpStatusCode.OK, repositoryImport.StatusCode);
        JsonElement repositoryReceipt = await repositoryImport.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("repo/example/project", repositoryReceipt.GetProperty("effectiveNamespace").GetString());
        Assert.Contains("repo/example/project", await RunMemCtlAsync(
            "capture", "receipt", repositoryReceipt.GetProperty("observationUuid").GetGuid().ToString()));

        using var specialImport = await ingest.PostAsJsonAsync(
            "/capture/v1/observations", RoutedObservation(
                null, "/workspace/notes/item", "memory-system"));
        Assert.Equal(HttpStatusCode.OK, specialImport.StatusCode);
        JsonElement specialReceipt = await specialImport.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("homelab", specialReceipt.GetProperty("effectiveNamespace").GetString());
        Assert.Equal("directory_mapping", specialReceipt.GetProperty("routeBasis").GetString());
        Assert.Contains("homelab", await RunMemCtlAsync(
            "capture", "receipt", specialReceipt.GetProperty("observationUuid").GetGuid().ToString()));

        using var unauthorizedImport = await ingest.PostAsJsonAsync(
            "/capture/v1/observations", RoutedObservation(
                "https://github.com/other/private.git", "/outside", "homelab"));
        Assert.Equal(HttpStatusCode.OK, unauthorizedImport.StatusCode);
        JsonElement unauthorizedReceipt = await unauthorizedImport.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("capture/unscoped", unauthorizedReceipt.GetProperty("effectiveNamespace").GetString());
        Assert.Equal("fallback", unauthorizedReceipt.GetProperty("routeBasis").GetString());

        using var mcpAttempt = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        mcpAttempt.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", credential);
        mcpAttempt.Content = JsonContent.Create(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { }
        });
        using var deniedMcp = await ingest.SendAsync(mcpAttempt);
        Assert.Equal(HttpStatusCode.Unauthorized, deniedMcp.StatusCode);

        ConfigureStaticOidcChallenge();
        using var deniedConsoleClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        deniedConsoleClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", credential);
        using var deniedConsole = await deniedConsoleClient.GetAsync(
            $"{_baseUrl}/capture/console/api/pairing/{requestId}");
        Assert.Equal(HttpStatusCode.Redirect, deniedConsole.StatusCode);
        Assert.Equal("authentik.test", deniedConsole.Headers.Location!.Host);

        JsonElement other = await CreatePairingAsync(
            runtime, "other-machine", $"other-installation-{Guid.NewGuid():N}");
        using var deniedCrossBinding = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/capture/v1/pairing-requests/{other.GetProperty("requestId").GetString()}");
        deniedCrossBinding.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        using var deniedCrossBindingResponse = await runtime.SendAsync(deniedCrossBinding);
        Assert.Equal(HttpStatusCode.Gone, deniedCrossBindingResponse.StatusCode);

        using var unavailableCapturedRead = await ingest.GetAsync(
            $"/capture/v1/observations/{repositoryReceipt.GetProperty("observationUuid").GetGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, unavailableCapturedRead.StatusCode);
    }

    [Fact]
    public async Task DisplayedPairingUrlRequiresOidcOperatorPolicy()
    {
        ConfigureStaticOidcChallenge();
        using var runtime = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        using var created = await runtime.PostAsJsonAsync("/capture/v1/pairing-requests", new
        {
            machineName = "policy-machine",
            codexInstallationId = $"policy-installation-{Guid.NewGuid():N}"
        });
        JsonElement request = await created.Content.ReadFromJsonAsync<JsonElement>();
        string verificationUri = request.GetProperty("verificationUri").GetString()!;

        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var open = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(_baseUrl), verificationUri));
        open.Headers.Add("X-Forwarded-Proto", "https");
        using var challenged = await browser.SendAsync(open);

        Assert.Equal(HttpStatusCode.Redirect, challenged.StatusCode);
        Assert.Equal("authentik.test", challenged.Headers.Location!.Host);
    }

    [Fact]
    public async Task DuplicatePairingApprovalsCreateOnlyOneInstallationBinding()
    {
        using var provider = ConfigureFakeOidcProvider("authentik|duplicate-operator");
        OidcSignIn signIn = await CompleteOidcSignInAsync(provider);
        string installation = $"duplicate-installation-{Guid.NewGuid():N}";
        using var runtime = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        JsonElement first = await CreatePairingAsync(runtime, "same-machine", installation);
        JsonElement second = await CreatePairingAsync(runtime, "same-machine", installation);
        using var console = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        console.DefaultRequestHeaders.Add("Cookie", signIn.CookieHeader);

        Task<HttpResponseMessage>[] approvals = new[] { first, second }
            .Select(pairing => console.PostAsJsonAsync(
                $"/capture/console/api/pairing/{pairing.GetProperty("requestId").GetString()}/approve",
                new
                {
                    label = $"Duplicate {Guid.NewGuid():N}",
                    allowedRepositoryPatterns = new[] { "github.com/example/*" },
                    specialNamespaces = Array.Empty<object>()
                }))
            .ToArray();
        HttpResponseMessage[] results = await Task.WhenAll(approvals);
        try
        {
            Assert.Single(results, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(results, response => response.StatusCode == HttpStatusCode.Conflict);
        }
        finally
        {
            foreach (HttpResponseMessage response in results) response.Dispose();
        }
    }

    [Fact]
    public async Task PollingCapabilityCanCancelOnlyItsOwnLiveRequestAndCannotReplay()
    {
        using var runtime = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        JsonElement created = await CreatePairingAsync(
            runtime, "cancel-machine", $"cancel-installation-{Guid.NewGuid():N}");
        JsonElement other = await CreatePairingAsync(
            runtime, "other-machine", $"other-installation-{Guid.NewGuid():N}");
        string requestId = created.GetProperty("requestId").GetString()!;
        string pollingToken = created.GetProperty("pollingToken").GetString()!;

        using var wrongPoll = AuthorizedRequest(
            HttpMethod.Get, $"/capture/v1/pairing-requests/{requestId}", "wrong-token");
        using var wrongPolled = await runtime.SendAsync(wrongPoll);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPolled.StatusCode);
        using var crossPoll = AuthorizedRequest(
            HttpMethod.Get,
            $"/capture/v1/pairing-requests/{other.GetProperty("requestId").GetString()}",
            pollingToken);
        using var crossPolled = await runtime.SendAsync(crossPoll);
        Assert.Equal(HttpStatusCode.Unauthorized, crossPolled.StatusCode);
        using var cancel = AuthorizedRequest(
            HttpMethod.Delete, $"/capture/v1/pairing-requests/{requestId}", pollingToken);
        using var cancelled = await runtime.SendAsync(cancel);
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        using var replay = AuthorizedRequest(
            HttpMethod.Delete, $"/capture/v1/pairing-requests/{requestId}", pollingToken);
        using var replayed = await runtime.SendAsync(replay);
        Assert.Equal(HttpStatusCode.Gone, replayed.StatusCode);
        using var poll = AuthorizedRequest(
            HttpMethod.Get, $"/capture/v1/pairing-requests/{requestId}", pollingToken);
        using var gone = await runtime.SendAsync(poll);
        Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);
    }

    [Fact]
    public async Task ExpiredPairingCannotBeCancelledOrApproved()
    {
        using var provider = ConfigureFakeOidcProvider("authentik|expiry-operator");
        OidcSignIn signIn = await CompleteOidcSignInAsync(provider);
        using var runtime = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        JsonElement created = await CreatePairingAsync(
            runtime, "expired-machine", $"expired-installation-{Guid.NewGuid():N}");
        string requestId = created.GetProperty("requestId").GetString()!;
        string pollingToken = created.GetProperty("pollingToken").GetString()!;
        _clock.Advance(TimeSpan.FromMinutes(16));

        using var cancel = AuthorizedRequest(
            HttpMethod.Delete, $"/capture/v1/pairing-requests/{requestId}", pollingToken);
        using var cancelled = await runtime.SendAsync(cancel);
        Assert.Equal(HttpStatusCode.Gone, cancelled.StatusCode);
        using var console = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        console.DefaultRequestHeaders.Add("Cookie", signIn.CookieHeader);
        using var approved = await console.PostAsJsonAsync(
            $"/capture/console/api/pairing/{requestId}/approve",
            new
            {
                label = "Expired",
                allowedRepositoryPatterns = new[] { "github.com/example/*" },
                specialNamespaces = Array.Empty<object>()
            });
        Assert.Equal(HttpStatusCode.Conflict, approved.StatusCode);
        using var poll = AuthorizedRequest(
            HttpMethod.Get, $"/capture/v1/pairing-requests/{requestId}", pollingToken);
        using var gone = await runtime.SendAsync(poll);
        Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);
    }

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
            await AssertOidcAuthorityUnavailableAsync(refused);

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
            await AssertOidcAuthorityUnavailableAsync(expiredSession);
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

    private static async Task AssertOidcAuthorityUnavailableAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

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

    private static async Task<JsonElement> CreatePairingAsync(
        HttpClient runtime, string machineName, string installationId)
    {
        using var response = await runtime.PostAsJsonAsync("/capture/v1/pairing-requests", new
        {
            machineName,
            codexInstallationId = installationId
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }

    private static HttpRequestMessage AuthorizedRequest(
        HttpMethod method, string uri, string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static string HtmlInputValue(string html, string name)
    {
        Match match = Regex.Match(
            html,
            $"name=\"{Regex.Escape(name)}\" value=\"(?<value>[^\"]+)\"",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"Input '{name}' was not present in the HTML form.");
        return WebUtility.HtmlDecode(match.Groups["value"].Value);
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

    private static object RoutedObservation(
        string? origin, string workingDirectory, string? payloadNamespace = null)
    {
        string marker = Guid.NewGuid().ToString("N");
        return new
        {
            contractVersion = 1,
            sourceSessionId = $"paired-route-session-{marker}",
            sourcePosition = 0,
            @namespace = payloadNamespace,
            locator = new { kind = "native_id", nativeId = $"record-{marker}" },
            routeEvidence = new
            {
                workingDirectory,
                remotes = origin is null
                    ? Array.Empty<object>()
                    : new object[] { new { name = "origin", url = origin } }
            },
            source = new { harness = "codex", harnessVersion = "synthetic", recordType = "turn" },
            adapter = new { name = "codex-synthetic", version = "1" },
            sourcePayload = new { message = "paired routing", @namespace = payloadNamespace },
            events = new[]
            {
                new
                {
                    partKey = "message/0",
                    partOrder = 0,
                    kind = "message",
                    actor = "user",
                    payload = new { text = "paired routing", @namespace = payloadNamespace },
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

    private static async Task WritePrivateCredentialAsync(string path, string credential)
    {
        var options = new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(credential);
    }

    private sealed record OidcSignIn(string CookieHeader, string ProtectedTicket);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

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

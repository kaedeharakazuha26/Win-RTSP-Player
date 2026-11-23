using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using WinRtspPlayer.Core.Auth;
using WinRtspPlayer.Core.Config;
using Xunit;

namespace WinRtspPlayer.Tests;

public sealed class OAuthEndpointsTests : IDisposable
{
    private readonly string _tempDir;
    private DateTimeOffset _now = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    public OAuthEndpointsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "WinRtspPlayerOAuthTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task WellKnownEndpoints_ReturnHttpsMetadataUsingForwardedHeadersOrConfiguredBaseUrl()
    {
        var store = new OAuthAuthStore(_tempDir, new DpapiSecretProtector(), () => _now);
        string? configuredBaseUrl = null;
        await using TestApp host = await StartTestHostAsync(store, () => configuredBaseUrl);

        // 1. Default request scheme/host fallback
        HttpResponseMessage defaultRes = await host.Client.GetAsync("/.well-known/oauth-protected-resource");
        Assert.Equal(HttpStatusCode.OK, defaultRes.StatusCode);
        JsonObject? defaultJson = JsonNode.Parse(await defaultRes.Content.ReadAsStringAsync())?.AsObject();
        Assert.NotNull(defaultJson);
        Assert.Equal("http://localhost/mcp", defaultJson["resource"]?.GetValue<string>());
        Assert.Equal("http://localhost", defaultJson["authorization_servers"]?[0]?.GetValue<string>());
        Assert.Equal("header", defaultJson["bearer_methods_supported"]?[0]?.GetValue<string>());

        // 2. X-Forwarded-Proto + X-Forwarded-Host (comma-separated proxy chain)
        using var forwardedReq = new HttpRequestMessage(HttpMethod.Get, "/.well-known/oauth-protected-resource/mcp");
        forwardedReq.Headers.Add("X-Forwarded-Proto", "https, http");
        forwardedReq.Headers.Add("X-Forwarded-Host", "cam-mcp.example.com, internal.local:8765");
        HttpResponseMessage forwardedRes = await host.Client.SendAsync(forwardedReq);
        Assert.Equal(HttpStatusCode.OK, forwardedRes.StatusCode);
        JsonObject? forwardedJson = JsonNode.Parse(await forwardedRes.Content.ReadAsStringAsync())?.AsObject();
        Assert.NotNull(forwardedJson);
        Assert.Equal("https://cam-mcp.example.com/mcp", forwardedJson["resource"]?.GetValue<string>());
        Assert.Equal("https://cam-mcp.example.com", forwardedJson["authorization_servers"]?[0]?.GetValue<string>());

        // 3. Configured public base URL overrides forwarded headers and trims trailing slash
        configuredBaseUrl = "https://configured.trycloudflare.com/";
        using var asReq = new HttpRequestMessage(HttpMethod.Get, "/.well-known/oauth-authorization-server");
        asReq.Headers.Add("X-Forwarded-Proto", "http");
        asReq.Headers.Add("X-Forwarded-Host", "ignored.example.com");
        HttpResponseMessage asRes = await host.Client.SendAsync(asReq);
        Assert.Equal(HttpStatusCode.OK, asRes.StatusCode);
        JsonObject? asJson = JsonNode.Parse(await asRes.Content.ReadAsStringAsync())?.AsObject();
        Assert.NotNull(asJson);
        Assert.Equal("https://configured.trycloudflare.com", asJson["issuer"]?.GetValue<string>());
        Assert.Equal("https://configured.trycloudflare.com/oauth/authorize", asJson["authorization_endpoint"]?.GetValue<string>());
        Assert.Equal("https://configured.trycloudflare.com/oauth/token", asJson["token_endpoint"]?.GetValue<string>());
        Assert.Equal(["code"], asJson["response_types_supported"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Equal(["authorization_code", "refresh_token"], asJson["grant_types_supported"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Equal(["S256"], asJson["code_challenge_methods_supported"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Equal(["client_secret_basic", "client_secret_post"], asJson["token_endpoint_auth_methods_supported"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task FullAuthorizationCodeFlow_WithPkceAndBasicAuth_SucceedsAndPersistsAcrossStoreReload()
    {
        var protector = new DpapiSecretProtector();
        var store = new OAuthAuthStore(_tempDir, protector, () => _now);
        OAuthCredentialsView creds = store.GetCredentials();
        await using TestApp host = await StartTestHostAsync(store, () => "https://mcp.example.com");

        string redirectUri = "https://oauth-redirect.googleusercontent.com/r/test?foo=1&bar=2";
        string longState = new string('S', 1500) + "+/=&?%end";
        string codeVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk-0123456789";
        string codeChallenge = ComputeS256Challenge(codeVerifier);

        string authorizeQuery =
            $"/oauth/authorize?response_type=code" +
            $"&client_id={Uri.EscapeDataString(creds.ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&state={Uri.EscapeDataString(longState)}" +
            $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
            $"&code_challenge_method=S256" +
            $"&resource={Uri.EscapeDataString("https://mcp.example.com/mcp")}";

        HttpResponseMessage getAuth = await host.Client.GetAsync(authorizeQuery);
        Assert.Equal(HttpStatusCode.OK, getAuth.StatusCode);
        Assert.Equal("text/html", getAuth.Content.Headers.ContentType?.MediaType);
        string html = await getAuth.Content.ReadAsStringAsync();
        Assert.Contains("name=\"passcode\"", html, StringComparison.Ordinal);
        Assert.Contains("type=\"password\"", html, StringComparison.Ordinal);

        HttpResponseMessage postAuth = await host.Client.PostAsync("/oauth/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = creds.ClientId,
            ["redirect_uri"] = redirectUri,
            ["state"] = longState,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["resource"] = "https://mcp.example.com/mcp",
            ["passcode"] = creds.ConsentPasscode
        }));

        Assert.Equal(HttpStatusCode.Redirect, postAuth.StatusCode);
        string location = postAuth.Headers.Location!.OriginalString;
        Assert.StartsWith("https://oauth-redirect.googleusercontent.com/r/test?foo=1&bar=2&code=", location, StringComparison.Ordinal);

        Dictionary<string, string> redirectQuery = ParseQueryString(new Uri(location).Query);
        Assert.Equal("1", redirectQuery["foo"]);
        Assert.Equal("2", redirectQuery["bar"]);
        Assert.Equal(longState, redirectQuery["state"]);
        string authCode = redirectQuery["code"];
        Assert.False(string.IsNullOrWhiteSpace(authCode));

        // Exchange code with Basic Auth (URL-encoded per RFC 6749 §2.3.1)
        using var tokenReq = new HttpRequestMessage(HttpMethod.Post, "/oauth/token");
        string encodedBasic = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(creds.ClientId)}:{Uri.EscapeDataString(creds.ClientSecret)}"));
        tokenReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", encodedBasic);
        tokenReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authCode,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier
        });

        HttpResponseMessage tokenRes = await host.Client.SendAsync(tokenReq);
        Assert.Equal(HttpStatusCode.OK, tokenRes.StatusCode);
        Assert.True(tokenRes.Headers.CacheControl?.NoStore);
        Assert.Contains("no-cache", tokenRes.Headers.Pragma.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

        JsonObject? tokenJson = JsonNode.Parse(await tokenRes.Content.ReadAsStringAsync())?.AsObject();
        Assert.NotNull(tokenJson);
        string accessToken = tokenJson["access_token"]!.GetValue<string>();
        string refreshToken = tokenJson["refresh_token"]!.GetValue<string>();
        Assert.Equal("Bearer", tokenJson["token_type"]?.GetValue<string>());
        Assert.Equal(3600, tokenJson["expires_in"]?.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(accessToken));
        Assert.False(string.IsNullOrWhiteSpace(refreshToken));

        // Verify disk file never stores plaintext secrets or raw tokens
        string authFile = Path.Combine(_tempDir, "config", "mcp-auth.json");
        Assert.True(File.Exists(authFile));
        string rawDisk = await File.ReadAllTextAsync(authFile);
        Assert.DoesNotContain(creds.ClientSecret, rawDisk, StringComparison.Ordinal);
        Assert.DoesNotContain(creds.ConsentPasscode, rawDisk, StringComparison.Ordinal);
        Assert.DoesNotContain(accessToken, rawDisk, StringComparison.Ordinal);
        Assert.DoesNotContain(refreshToken, rawDisk, StringComparison.Ordinal);

        // Reload fresh store from disk and validate token persistence
        var reloaded = new OAuthAuthStore(_tempDir, new DpapiSecretProtector(), () => _now.AddMinutes(30));
        Assert.True(reloaded.ValidateAccessToken(accessToken));

        var expiredClockStore = new OAuthAuthStore(_tempDir, new DpapiSecretProtector(), () => _now.AddMinutes(61));
        Assert.False(expiredClockStore.ValidateAccessToken(accessToken));
    }

    [Fact]
    public async Task TokenEndpoint_SupportsClientSecretPostAndRefreshTokenGrant()
    {
        var store = new OAuthAuthStore(_tempDir, new DpapiSecretProtector(), () => _now);
        OAuthCredentialsView creds = store.GetCredentials();
        await using TestApp host = await StartTestHostAsync(store, () => null);

        string redirectUri = "http://127.0.0.1:9090/callback";
        string verifier = "verifier-for-client-secret-post-flow-0123456789";
        string challenge = ComputeS256Challenge(verifier);
        string code = store.CreateAuthCode(creds.ClientId, redirectUri, challenge, null);

        HttpResponseMessage tokenRes = await host.Client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = creds.ClientId,
            ["client_secret"] = creds.ClientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier
        }));
        Assert.Equal(HttpStatusCode.OK, tokenRes.StatusCode);
        JsonObject? tokenJson = JsonNode.Parse(await tokenRes.Content.ReadAsStringAsync())?.AsObject();
        string access1 = tokenJson!["access_token"]!.GetValue<string>();
        string refresh1 = tokenJson["refresh_token"]!.GetValue<string>();

        // Refresh token grant via client_secret_post rotates refresh token
        HttpResponseMessage refreshRes = await host.Client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = creds.ClientId,
            ["client_secret"] = creds.ClientSecret,
            ["refresh_token"] = refresh1
        }));
        Assert.Equal(HttpStatusCode.OK, refreshRes.StatusCode);
        JsonObject? refreshJson = JsonNode.Parse(await refreshRes.Content.ReadAsStringAsync())?.AsObject();
        string access2 = refreshJson!["access_token"]!.GetValue<string>();
        string refresh2 = refreshJson["refresh_token"]!.GetValue<string>();
        Assert.NotEqual(access1, access2);
        Assert.NotEqual(refresh1, refresh2);
        Assert.True(store.ValidateAccessToken(access2));

        // Old refresh token cannot be replayed after rotation
        HttpResponseMessage replayRefreshRes = await host.Client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = creds.ClientId,
            ["client_secret"] = creds.ClientSecret,
            ["refresh_token"] = refresh1
        }));
        Assert.Equal(HttpStatusCode.BadRequest, replayRefreshRes.StatusCode);
        JsonObject? errJson = JsonNode.Parse(await replayRefreshRes.Content.ReadAsStringAsync())?.AsObject();
        Assert.Equal("invalid_grant", errJson!["error"]?.GetValue<string>());
    }

    [Fact]
    public async Task TokenEndpoint_UrlDecodesRfc6749BasicAuthCredentialsAndRejectsConflicts()
    {
        // Seed store with special characters needing RFC 6749 §2.3.1 URL encoding in Basic auth
        var protector = new DpapiSecretProtector();
        string configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        string seededJson = JsonSerializer.Serialize(new
        {
            EncryptedClientId = protector.Protect("a:b"),
            EncryptedClientSecret = protector.Protect("p@ss+1"),
            EncryptedConsentPasscode = protector.Protect("pass-1234")
        });
        await File.WriteAllTextAsync(Path.Combine(configDir, "mcp-auth.json"), seededJson, Encoding.UTF8);

        var store = new OAuthAuthStore(_tempDir, protector, () => _now);
        await using TestApp host = await StartTestHostAsync(store, () => null);

        string redirectUri = "https://gemini.google.com/oauth";
        string verifier = "verifier-basic-url-encoding-test-0123456789";
        string code = store.CreateAuthCode("a:b", redirectUri, ComputeS256Challenge(verifier), null);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/oauth/token");
        string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("a%3Ab:p%40ss%2B1"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier
        });

        HttpResponseMessage res = await host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // Conflicting Basic + form credentials must return 401 invalid_client
        string code2 = store.CreateAuthCode("a:b", redirectUri, ComputeS256Challenge(verifier), null);
        using var conflictReq = new HttpRequestMessage(HttpMethod.Post, "/oauth/token");
        conflictReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        conflictReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = "other-client",
            ["client_secret"] = "p@ss+1",
            ["code"] = code2,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier
        });

        HttpResponseMessage conflictRes = await host.Client.SendAsync(conflictReq);
        Assert.Equal(HttpStatusCode.Unauthorized, conflictRes.StatusCode);
        JsonObject? conflictJson = JsonNode.Parse(await conflictRes.Content.ReadAsStringAsync())?.AsObject();
        Assert.Equal("invalid_client", conflictJson!["error"]?.GetValue<string>());
    }

    [Fact]
    public async Task Authorize_RejectsDisallowedRedirectUri()
    {
        var store = new OAuthAuthStore(_tempDir, new DpapiSecretProtector(), () => _now);
        OAuthCredentialsView creds = store.GetCredentials();
        await using TestApp host = await StartTestHostAsync(store, () => null);

        string challenge = ComputeS256Challenge("verifier-for-redirect-test-0123456789");
        string query =
            $"/oauth/authorize?response_type=code" +
            $"&client_id={Uri.EscapeDataString(creds.ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString("https://evil.example.com/cb")}" +
            $"&code_challenge={Uri.EscapeDataString(challenge)}" +
            $"&code_challenge_method=S256";

        HttpResponseMessage getRes = await host.Client.GetAsync(query);
        Assert.Equal(HttpStatusCode.BadRequest, getRes.StatusCode);
        Assert.Null(getRes.Headers.Location);

        HttpResponseMessage postRes = await host.Client.PostAsync("/oauth/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = creds.ClientId,
            ["redirect_uri"] = "https://evil.example.com/cb",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["passcode"] = creds.ConsentPasscode
        }));
        Assert.Equal(HttpStatusCode.BadRequest, postRes.StatusCode);
        Assert.Null(postRes.Headers.Location);

        Assert.True(OAuthEndpoints.IsAllowedRedirectUri("https://oauth-redirect.googleusercontent.com/r/abc"));
        Assert.True(OAuthEndpoints.IsAllowedRedirectUri("https://gemini.google.com/cb"));
        Assert.True(OAuthEndpoints.IsAllowedRedirectUri("https://sub.googleusercontent.com/cb"));
        Assert.True(OAuthEndpoints.IsAllowedRedirectUri("https://sub.google.com/cb"));
        Assert.True(OAuthEndpoints.IsAllowedRedirectUri("http://localhost:8080/cb"));
        Assert.True(OAuthEndpoints.IsAllowedRedirectUri("http://127.0.0.1:8080/cb"));

        Assert.False(OAuthEndpoints.IsAllowedRedirectUri("https://evil.example.com/cb"));
        Assert.False(OAuthEndpoints.IsAllowedRedirectUri("https://notgoogle.com/cb"));
        Assert.False(OAuthEndpoints.IsAllowedRedirectUri("https://user:pass@gemini.google.com/cb"));
        Assert.False(OAuthEndpoints.IsAllowedRedirectUri("http://gemini.google.com/cb"));
    }

    [Fact]
    public async Task Authorize_RateLimitsAfterFiveFailedPasscodes()
    {
        var store = new OAuthAuthStore(_tempDir, new DpapiSecretProtector(), () => _now);
        OAuthCredentialsView creds = store.GetCredentials();
        await using TestApp host = await StartTestHostAsync(store, () => null);

        string redirectUri = "https://oauth-redirect.googleusercontent.com/r/test";
        string challenge = ComputeS256Challenge("verifier-for-rate-limit-test-0123456789");

        for (int i = 0; i < 5; i++)
        {
            HttpResponseMessage failRes = await host.Client.PostAsync("/oauth/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["response_type"] = "code",
                ["client_id"] = creds.ClientId,
                ["redirect_uri"] = redirectUri,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["passcode"] = "wrong-passcode"
            }));
            Assert.Equal(HttpStatusCode.Unauthorized, failRes.StatusCode);
        }

        // 6th attempt within 60s window must return 429 even with valid passcode
        HttpResponseMessage rateLimitedRes = await host.Client.PostAsync("/oauth/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = creds.ClientId,
            ["redirect_uri"] = redirectUri,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["passcode"] = creds.ConsentPasscode
        }));
        Assert.Equal(HttpStatusCode.TooManyRequests, rateLimitedRes.StatusCode);

        // After 61 seconds, window expires and valid passcode succeeds
        _now = _now.AddSeconds(61);
        HttpResponseMessage recoveredRes = await host.Client.PostAsync("/oauth/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = creds.ClientId,
            ["redirect_uri"] = redirectUri,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["passcode"] = creds.ConsentPasscode
        }));
        Assert.Equal(HttpStatusCode.Redirect, recoveredRes.StatusCode);
    }

    [Fact]
    public async Task TokenEndpoint_RejectsBadPkceVerifierAndReplayedAuthCode()
    {
        var store = new OAuthAuthStore(_tempDir, new DpapiSecretProtector(), () => _now);
        OAuthCredentialsView creds = store.GetCredentials();
        await using TestApp host = await StartTestHostAsync(store, () => null);

        string redirectUri = "https://oauth-redirect.googleusercontent.com/r/test";
        string goodVerifier = "good-pkce-verifier-012345678901234567890123456";
        string challenge = ComputeS256Challenge(goodVerifier);

        // 1. Bad verifier fails and invalidates that auth code
        string code1 = store.CreateAuthCode(creds.ClientId, redirectUri, challenge, null);
        HttpResponseMessage badVerifierRes = await PostTokenWithBasicAsync(host.Client, creds, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code1,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = "wrong-verifier-012345678901234567890123456"
        });
        Assert.Equal(HttpStatusCode.BadRequest, badVerifierRes.StatusCode);
        JsonObject? badJson = JsonNode.Parse(await badVerifierRes.Content.ReadAsStringAsync())?.AsObject();
        Assert.Equal("invalid_grant", badJson!["error"]?.GetValue<string>());

        // Trying code1 again even with good verifier fails because code was consumed
        HttpResponseMessage burnedRes = await PostTokenWithBasicAsync(host.Client, creds, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code1,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = goodVerifier
        });
        Assert.Equal(HttpStatusCode.BadRequest, burnedRes.StatusCode);

        // 2. Fresh code succeeds once, then replay is rejected
        string code2 = store.CreateAuthCode(creds.ClientId, redirectUri, challenge, null);
        HttpResponseMessage okRes = await PostTokenWithBasicAsync(host.Client, creds, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code2,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = goodVerifier
        });
        Assert.Equal(HttpStatusCode.OK, okRes.StatusCode);

        HttpResponseMessage replayRes = await PostTokenWithBasicAsync(host.Client, creds, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code2,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = goodVerifier
        });
        Assert.Equal(HttpStatusCode.BadRequest, replayRes.StatusCode);
        JsonObject? replayJson = JsonNode.Parse(await replayRes.Content.ReadAsStringAsync())?.AsObject();
        Assert.Equal("invalid_grant", replayJson!["error"]?.GetValue<string>());
    }

    [Fact]
    public async Task TokenEndpoint_RejectsExpiredCodesExpiredRefreshInvalidClientAndUnsupportedGrant()
    {
        var store = new OAuthAuthStore(_tempDir, new DpapiSecretProtector(), () => _now);
        OAuthCredentialsView creds = store.GetCredentials();
        await using TestApp host = await StartTestHostAsync(store, () => null);

        string redirectUri = "http://localhost:8080/cb";
        string verifier = "verifier-for-expiry-and-grant-checks-0123456789";
        string challenge = ComputeS256Challenge(verifier);

        // 1. Auth code expires after 5 minutes
        string expiringCode = store.CreateAuthCode(creds.ClientId, redirectUri, challenge, null);
        _now = _now.AddMinutes(6);
        HttpResponseMessage expiredCodeRes = await PostTokenWithBasicAsync(host.Client, creds, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = expiringCode,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier
        });
        Assert.Equal(HttpStatusCode.BadRequest, expiredCodeRes.StatusCode);
        JsonObject? expiredCodeJson = JsonNode.Parse(await expiredCodeRes.Content.ReadAsStringAsync())?.AsObject();
        Assert.Equal("invalid_grant", expiredCodeJson!["error"]?.GetValue<string>());

        // 2. Refresh token expires after 30 days
        string freshCode = store.CreateAuthCode(creds.ClientId, redirectUri, challenge, null);
        TokenPair? pair = store.ExchangeAuthCode(freshCode, creds.ClientId, redirectUri, verifier);
        Assert.NotNull(pair);

        _now = _now.AddDays(31);
        HttpResponseMessage expiredRefreshRes = await PostTokenWithBasicAsync(host.Client, creds, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = pair.RefreshToken
        });
        Assert.Equal(HttpStatusCode.BadRequest, expiredRefreshRes.StatusCode);

        // 3. Unsupported grant type returns 400 unsupported_grant_type
        HttpResponseMessage unsupportedRes = await PostTokenWithBasicAsync(host.Client, creds, new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials"
        });
        Assert.Equal(HttpStatusCode.BadRequest, unsupportedRes.StatusCode);
        JsonObject? unsupportedJson = JsonNode.Parse(await unsupportedRes.Content.ReadAsStringAsync())?.AsObject();
        Assert.Equal("unsupported_grant_type", unsupportedJson!["error"]?.GetValue<string>());

        // 4. Invalid client secret returns 401 invalid_client
        HttpResponseMessage badClientRes = await host.Client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = creds.ClientId,
            ["client_secret"] = "wrong-secret",
            ["code"] = "anything",
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier
        }));
        Assert.Equal(HttpStatusCode.Unauthorized, badClientRes.StatusCode);
        JsonObject? badClientJson = JsonNode.Parse(await badClientRes.Content.ReadAsStringAsync())?.AsObject();
        Assert.Equal("invalid_client", badClientJson!["error"]?.GetValue<string>());
    }

    [Fact]
    public async Task RegenerateSecrets_RevokesExistingAccessAndRefreshTokens()
    {
        var store = new OAuthAuthStore(_tempDir, new DpapiSecretProtector(), () => _now);
        OAuthCredentialsView oldCreds = store.GetCredentials();
        string redirectUri = "http://localhost:3000/cb";
        string verifier = "verifier-for-regenerate-secrets-test-0123456789";
        string code = store.CreateAuthCode(oldCreds.ClientId, redirectUri, ComputeS256Challenge(verifier), null);
        TokenPair? pair = store.ExchangeAuthCode(code, oldCreds.ClientId, redirectUri, verifier);
        Assert.NotNull(pair);
        Assert.True(store.ValidateAccessToken(pair.AccessToken));

        OAuthCredentialsView newCreds = store.RegenerateSecrets();
        Assert.NotEqual(oldCreds.ClientId, newCreds.ClientId);
        Assert.NotEqual(oldCreds.ClientSecret, newCreds.ClientSecret);
        Assert.NotEqual(oldCreds.ConsentPasscode, newCreds.ConsentPasscode);

        Assert.False(store.ValidateAccessToken(pair.AccessToken));
        Assert.Null(store.RefreshAccessToken(pair.RefreshToken, oldCreds.ClientId));
        Assert.Null(store.RefreshAccessToken(pair.RefreshToken, newCreds.ClientId));
        Assert.False(store.ValidateClientSecret(oldCreds.ClientId, oldCreds.ClientSecret));
        Assert.True(store.ValidateClientSecret(newCreds.ClientId, newCreds.ClientSecret));

        await using TestApp host = await StartTestHostAsync(store, () => null);
        HttpResponseMessage oldRefreshRes = await PostTokenWithBasicAsync(host.Client, newCreds, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = pair.RefreshToken
        });
        Assert.Equal(HttpStatusCode.BadRequest, oldRefreshRes.StatusCode);
    }

    [Fact]
    public void CorruptMcpAuthJson_IsBackedUpAndRegeneratedCleanly()
    {
        string configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        string authPath = Path.Combine(configDir, "mcp-auth.json");
        File.WriteAllText(authPath, "{ \"EncryptedClientId\": ", Encoding.UTF8);

        var store = new OAuthAuthStore(_tempDir, new DpapiSecretProtector(), () => _now);
        OAuthCredentialsView creds = store.GetCredentials();

        Assert.False(string.IsNullOrWhiteSpace(creds.ClientId));
        Assert.False(string.IsNullOrWhiteSpace(creds.ClientSecret));
        Assert.False(string.IsNullOrWhiteSpace(creds.ConsentPasscode));

        string[] backups = Directory.GetFiles(configDir, "mcp-auth.json.corrupt.*");
        Assert.Single(backups);
        Assert.True(File.Exists(authPath));
    }

    [Fact]
    public async Task AuthorizeGet_HtmlEncodesUntrustedInputs()
    {
        var store = new OAuthAuthStore(_tempDir, new DpapiSecretProtector(), () => _now);
        OAuthCredentialsView creds = store.GetCredentials();
        await using TestApp host = await StartTestHostAsync(store, () => null);

        string xssState = "\"><script>alert(1)</script>";
        string xssScope = "cam\"onfocus=\"alert(2)";
        string url =
            $"/oauth/authorize?response_type=code" +
            $"&client_id={Uri.EscapeDataString(creds.ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString("https://gemini.google.com/cb")}" +
            $"&state={Uri.EscapeDataString(xssState)}" +
            $"&scope={Uri.EscapeDataString(xssScope)}" +
            $"&code_challenge={Uri.EscapeDataString(ComputeS256Challenge("verifier-xss-check-01234567890123456789"))}" +
            $"&code_challenge_method=S256";

        HttpResponseMessage res = await host.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        string html = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> PostTokenWithBasicAsync(
        HttpClient client,
        OAuthCredentialsView creds,
        Dictionary<string, string> formFields)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/oauth/token");
        string basic = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(creds.ClientId)}:{Uri.EscapeDataString(creds.ClientSecret)}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        req.Content = new FormUrlEncodedContent(formFields);
        return await client.SendAsync(req);
    }

    private static string ComputeS256Challenge(string codeVerifier)
    {
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int idx = part.IndexOf('=');
            string key = idx >= 0 ? Uri.UnescapeDataString(part[..idx]) : Uri.UnescapeDataString(part);
            string value = idx >= 0 ? Uri.UnescapeDataString(part[(idx + 1)..]) : string.Empty;
            result[key] = value;
        }
        return result;
    }

    private static async Task<TestApp> StartTestHostAsync(OAuthAuthStore store, Func<string?> getConfiguredBaseUrl)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        WebApplication app = builder.Build();
        OAuthEndpoints.MapOAuthEndpoints(app, store, getConfiguredBaseUrl);
        await app.StartAsync();
        return new TestApp(app, app.GetTestClient());
    }

    private sealed class TestApp : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public HttpClient Client { get; }

        public TestApp(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }
}

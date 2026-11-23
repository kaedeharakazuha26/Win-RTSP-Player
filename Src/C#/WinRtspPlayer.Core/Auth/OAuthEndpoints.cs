using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace WinRtspPlayer.Core.Auth;

/// <summary>
/// Built-in OAuth 2.1 authorization and metadata endpoints for MCP clients (such as Google Gemini).
/// </summary>
public static class OAuthEndpoints
{
    public static string ResolvePublicBaseUrl(HttpRequest req, string? configuredPublicBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (!string.IsNullOrWhiteSpace(configuredPublicBaseUrl))
        {
            return configuredPublicBaseUrl.Trim().TrimEnd('/');
        }

        string? forwardedProto = ExtractFirstHeaderValue(req.Headers, "X-Forwarded-Proto");
        string? scheme = forwardedProto is not null &&
            (forwardedProto.Equals("https", StringComparison.OrdinalIgnoreCase) ||
             forwardedProto.Equals("http", StringComparison.OrdinalIgnoreCase))
            ? forwardedProto.ToLowerInvariant()
            : null;

        string? forwardedHost = ExtractFirstHeaderValue(req.Headers, "X-Forwarded-Host");
        if (forwardedHost is not null && !IsValidHeaderHost(forwardedHost))
        {
            forwardedHost = null;
        }

        string effectiveScheme = scheme ?? (string.IsNullOrEmpty(req.Scheme) ? "http" : req.Scheme);
        string effectiveHost = forwardedHost ?? (req.Host.HasValue ? req.Host.Value : "127.0.0.1");
        return $"{effectiveScheme}://{effectiveHost}".TrimEnd('/');
    }

    public static bool IsAllowedRedirectUri(string redirectUri)
    {
        if (string.IsNullOrWhiteSpace(redirectUri) ||
            !Uri.TryCreate(redirectUri, UriKind.Absolute, out Uri? uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            string host = uri.Host;
            return host.Equals("oauth-redirect.googleusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                   host.Equals("gemini.google.com", StringComparison.OrdinalIgnoreCase) ||
                   (host.Length > ".googleusercontent.com".Length &&
                    host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase)) ||
                   (host.Length > ".google.com".Length &&
                    host.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase));
        }

        if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                   uri.Host.Equals("127.0.0.1", StringComparison.Ordinal);
        }

        return false;
    }

    public static void MapOAuthEndpoints(
        IEndpointRouteBuilder app,
        OAuthAuthStore authStore,
        Func<string?> getConfiguredBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(authStore);
        ArgumentNullException.ThrowIfNull(getConfiguredBaseUrl);

        IResult HandleProtectedResourceMetadata(HttpContext ctx)
        {
            string baseUrl = ResolvePublicBaseUrl(ctx.Request, getConfiguredBaseUrl());
            return Results.Ok(new ProtectedResourceMetadata(
                Resource: $"{baseUrl}/mcp",
                AuthorizationServers: [baseUrl],
                BearerMethodsSupported: ["header"]));
        }

        app.MapGet("/.well-known/oauth-protected-resource", HandleProtectedResourceMetadata);
        app.MapGet("/.well-known/oauth-protected-resource/mcp", HandleProtectedResourceMetadata);

        app.MapGet("/.well-known/oauth-authorization-server", (HttpContext ctx) =>
        {
            string baseUrl = ResolvePublicBaseUrl(ctx.Request, getConfiguredBaseUrl());
            return Results.Ok(new AuthorizationServerMetadata(
                Issuer: baseUrl,
                AuthorizationEndpoint: $"{baseUrl}/oauth/authorize",
                TokenEndpoint: $"{baseUrl}/oauth/token",
                ResponseTypesSupported: ["code"],
                GrantTypesSupported: ["authorization_code", "refresh_token"],
                CodeChallengeMethodsSupported: ["S256"],
                TokenEndpointAuthMethodsSupported: ["client_secret_basic", "client_secret_post"]));
        });

        app.MapGet("/oauth/authorize", (HttpContext ctx) =>
        {
            IQueryCollection q = ctx.Request.Query;
            AuthorizeParams requestParams = ReadAuthorizeParams(k => (q.TryGetValue(k, out StringValues v), v));

            if (!TryValidateAuthorizeParams(requestParams, authStore, out string? validationError))
            {
                return Results.BadRequest(new OAuthErrorResponse("invalid_request", validationError));
            }

            return Results.Content(RenderConsentHtml(requestParams, errorBanner: null), "text/html; charset=utf-8", Encoding.UTF8);
        });

        app.MapPost("/oauth/authorize", async (HttpContext ctx) =>
        {
            if (!ctx.Request.HasFormContentType)
            {
                return Results.BadRequest(new OAuthErrorResponse("invalid_request", "Expected form submission."));
            }

            IFormCollection form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            AuthorizeParams requestParams = ReadAuthorizeParams(k => (form.TryGetValue(k, out StringValues v), v));

            if (!TryValidateAuthorizeParams(requestParams, authStore, out string? validationError))
            {
                return Results.BadRequest(new OAuthErrorResponse("invalid_request", validationError));
            }

            string passcode = form["passcode"].ToString();
            if (!authStore.ValidateConsentPasscode(passcode, out bool isRateLimited))
            {
                if (isRateLimited)
                {
                    ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    return Results.Content(
                        RenderConsentHtml(requestParams, "Too many failed passcode attempts. Please wait 60 seconds and try again."),
                        "text/html; charset=utf-8",
                        Encoding.UTF8);
                }

                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Results.Content(
                    RenderConsentHtml(requestParams, "Invalid consent passcode. Check Win RTSP Player MCP Settings and try again."),
                    "text/html; charset=utf-8",
                    Encoding.UTF8);
            }

            string code = authStore.CreateAuthCode(
                requestParams.ClientId,
                requestParams.RedirectUri,
                requestParams.CodeChallenge,
                requestParams.Resource);

            string separator = requestParams.RedirectUri.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            string location = $"{requestParams.RedirectUri}{separator}code={Uri.EscapeDataString(code)}";
            if (!string.IsNullOrEmpty(requestParams.State))
            {
                location += $"&state={Uri.EscapeDataString(requestParams.State)}";
            }

            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Redirect(location);
        });

        app.MapPost("/oauth/token", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers.Pragma = "no-cache";

            if (!ctx.Request.HasFormContentType)
            {
                return Results.BadRequest(new OAuthErrorResponse("invalid_request", "Expected application/x-www-form-urlencoded body."));
            }

            IFormCollection form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            if (!TryExtractClientCredentials(ctx.Request, form, out string clientId, out string clientSecret) ||
                !authStore.ValidateClientSecret(clientId, clientSecret))
            {
                return Results.Json(
                    new OAuthErrorResponse("invalid_client", "Client authentication failed."),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            string grantType = form["grant_type"].ToString();
            if (string.Equals(grantType, "authorization_code", StringComparison.Ordinal))
            {
                string code = form["code"].ToString();
                string redirectUri = form["redirect_uri"].ToString();
                string codeVerifier = form["code_verifier"].ToString();

                TokenPair? pair = authStore.ExchangeAuthCode(code, clientId, redirectUri, codeVerifier);
                if (pair is null)
                {
                    return Results.BadRequest(new OAuthErrorResponse("invalid_grant", "Invalid, expired, or mismatched authorization code."));
                }

                return Results.Ok(new TokenResponse(pair.AccessToken, "Bearer", pair.ExpiresInSeconds, pair.RefreshToken));
            }

            if (string.Equals(grantType, "refresh_token", StringComparison.Ordinal))
            {
                string refreshToken = form["refresh_token"].ToString();
                TokenPair? pair = authStore.RefreshAccessToken(refreshToken, clientId);
                if (pair is null)
                {
                    return Results.BadRequest(new OAuthErrorResponse("invalid_grant", "Invalid or expired refresh token."));
                }

                return Results.Ok(new TokenResponse(pair.AccessToken, "Bearer", pair.ExpiresInSeconds, pair.RefreshToken));
            }

            return Results.BadRequest(new OAuthErrorResponse("unsupported_grant_type", "Supported grant types are authorization_code and refresh_token."));
        });
    }

    private static AuthorizeParams ReadAuthorizeParams(Func<string, (bool Found, StringValues Value)> lookup)
    {
        string Get(string key) => lookup(key).Value.ToString();
        string? GetOptional(string key) => lookup(key) is (true, var val) ? val.ToString() : null;

        return new AuthorizeParams(
            ResponseType: Get("response_type"),
            ClientId: Get("client_id"),
            RedirectUri: Get("redirect_uri"),
            State: GetOptional("state"),
            CodeChallenge: Get("code_challenge"),
            CodeChallengeMethod: Get("code_challenge_method"),
            Scope: GetOptional("scope"),
            Resource: GetOptional("resource"));
    }

    private static bool TryValidateAuthorizeParams(AuthorizeParams p, OAuthAuthStore authStore, out string? error)
    {
        if (!string.Equals(p.ResponseType, "code", StringComparison.Ordinal))
        {
            error = "response_type must be 'code'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(p.ClientId) || !authStore.ValidateClientId(p.ClientId))
        {
            error = "Unknown or invalid client_id.";
            return false;
        }

        if (!IsAllowedRedirectUri(p.RedirectUri))
        {
            error = "redirect_uri is missing or not allowlisted.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(p.CodeChallenge) ||
            !string.Equals(p.CodeChallengeMethod, "S256", StringComparison.Ordinal))
        {
            error = "PKCE S256 code_challenge is required.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryExtractClientCredentials(
        HttpRequest req,
        IFormCollection form,
        out string clientId,
        out string clientSecret)
    {
        clientId = string.Empty;
        clientSecret = string.Empty;

        bool hasBasic = false;
        string basicId = string.Empty;
        string basicSecret = string.Empty;

        if (req.Headers.TryGetValue("Authorization", out StringValues authHeaders) && authHeaders.Count > 0)
        {
            if (authHeaders.Count != 1)
            {
                return false;
            }

            string header = authHeaders[0] ?? string.Empty;
            if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string encoded = header["Basic ".Length..].Trim();
            string decoded;
            try
            {
                byte[] bytes = Convert.FromBase64String(encoded);
                decoded = Encoding.UTF8.GetString(bytes);
            }
            catch (FormatException)
            {
                return false;
            }

            int colonIndex = decoded.IndexOf(':');
            if (colonIndex < 0)
            {
                return false;
            }

            // RFC 6749 §2.3.1: HTTP Basic client_id and client_secret are application/x-www-form-urlencoded.
            basicId = WebUtility.UrlDecode(decoded[..colonIndex]);
            basicSecret = WebUtility.UrlDecode(decoded[(colonIndex + 1)..]);
            hasBasic = true;
        }

        bool hasFormId = form.TryGetValue("client_id", out StringValues formIdValues) &&
                         !string.IsNullOrEmpty(formIdValues.ToString());
        bool hasFormSecret = form.TryGetValue("client_secret", out StringValues formSecretValues) &&
                             !string.IsNullOrEmpty(formSecretValues.ToString());

        string formId = hasFormId ? formIdValues.ToString() : string.Empty;
        string formSecret = hasFormSecret ? formSecretValues.ToString() : string.Empty;

        if (hasBasic)
        {
            if ((hasFormId && !string.Equals(formId, basicId, StringComparison.Ordinal)) ||
                (hasFormSecret && !string.Equals(formSecret, basicSecret, StringComparison.Ordinal)))
            {
                return false;
            }

            clientId = basicId;
            clientSecret = basicSecret;
            return !string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret);
        }

        if (!hasFormId || !hasFormSecret)
        {
            return false;
        }

        clientId = formId;
        clientSecret = formSecret;
        return true;
    }

    private static string? ExtractFirstHeaderValue(IHeaderDictionary headers, string name)
    {
        if (!headers.TryGetValue(name, out StringValues values) || values.Count == 0)
        {
            return null;
        }

        string? raw = values[0];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        int commaIndex = raw.IndexOf(',');
        string first = (commaIndex >= 0 ? raw[..commaIndex] : raw).Trim();
        return string.IsNullOrEmpty(first) ? null : first;
    }

    private static bool IsValidHeaderHost(string host)
    {
        foreach (char ch in host)
        {
            if (char.IsWhiteSpace(ch) || ch is '/' or '\\' or '?' or '#' or '@')
            {
                return false;
            }
        }

        return true;
    }

    private static string RenderConsentHtml(AuthorizeParams p, string? errorBanner)
    {
        string redirectHost = new Uri(p.RedirectUri, UriKind.Absolute).Host;
        var sb = new StringBuilder();
        sb.Append("""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>Win RTSP Player - Authorize MCP Access</title>
              <style>
                body { font-family: system-ui, -apple-system, Segoe UI, sans-serif; background: #11161d; color: #e6edf3; display: flex; align-items: center; justify-content: center; min-height: 100vh; margin: 0; }
                .card { background: #1b222c; border: 1px solid #303a48; border-radius: 10px; padding: 24px; max-width: 420px; width: 100%; box-shadow: 0 12px 32px rgba(0,0,0,0.4); }
                h1 { font-size: 1.2rem; margin: 0 0 12px; }
                p { font-size: 0.92rem; color: #b5c0cd; line-height: 1.45; }
                .error { background: #4c1d24; border: 1px solid #993241; color: #ffd7dc; padding: 10px 12px; border-radius: 6px; margin-bottom: 14px; font-size: 0.9rem; }
                label { display: block; font-size: 0.85rem; margin: 14px 0 6px; color: #cdd6e0; }
                input[type=password] { width: 100%; box-sizing: border-box; padding: 10px; border-radius: 6px; border: 1px solid #3b4656; background: #0f141a; color: #fff; font-size: 1rem; }
                button { margin-top: 16px; width: 100%; padding: 10px; border: 0; border-radius: 6px; background: #2f81f7; color: #fff; font-size: 0.98rem; font-weight: 600; cursor: pointer; }
              </style>
            </head>
            <body>
              <div class="card">
                <h1>Authorize Win RTSP Player MCP</h1>
            """);

        if (!string.IsNullOrEmpty(errorBanner))
        {
            sb.Append($"<div class=\"error\">{WebUtility.HtmlEncode(errorBanner)}</div>");
        }

        sb.Append($"<p>Client <code>{WebUtility.HtmlEncode(p.ClientId)}</code> is requesting access and will redirect to <code>{WebUtility.HtmlEncode(redirectHost)}</code>.</p>");
        sb.Append("""
                <form method="post" action="/oauth/authorize">
            """);

        AppendHiddenInput(sb, "response_type", p.ResponseType);
        AppendHiddenInput(sb, "client_id", p.ClientId);
        AppendHiddenInput(sb, "redirect_uri", p.RedirectUri);
        AppendHiddenInput(sb, "code_challenge", p.CodeChallenge);
        AppendHiddenInput(sb, "code_challenge_method", p.CodeChallengeMethod);
        if (p.State is not null)
        {
            AppendHiddenInput(sb, "state", p.State);
        }

        if (p.Scope is not null)
        {
            AppendHiddenInput(sb, "scope", p.Scope);
        }

        if (p.Resource is not null)
        {
            AppendHiddenInput(sb, "resource", p.Resource);
        }

        sb.Append("""
                  <label for="passcode">Consent Passcode</label>
                  <input id="passcode" name="passcode" type="password" autocomplete="off" required autofocus />
                  <button type="submit">Approve &amp; Connect</button>
                </form>
              </div>
            </body>
            </html>
            """);

        return sb.ToString();
    }

    private static void AppendHiddenInput(StringBuilder sb, string name, string value)
    {
        sb.Append($"<input type=\"hidden\" name=\"{WebUtility.HtmlEncode(name)}\" value=\"{WebUtility.HtmlEncode(value)}\" />");
    }

    private sealed record AuthorizeParams(
        string ResponseType,
        string ClientId,
        string RedirectUri,
        string? State,
        string CodeChallenge,
        string CodeChallengeMethod,
        string? Scope,
        string? Resource);

    private sealed record ProtectedResourceMetadata(
        [property: JsonPropertyName("resource")] string Resource,
        [property: JsonPropertyName("authorization_servers")] string[] AuthorizationServers,
        [property: JsonPropertyName("bearer_methods_supported")] string[] BearerMethodsSupported);

    private sealed record AuthorizationServerMetadata(
        [property: JsonPropertyName("issuer")] string Issuer,
        [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
        [property: JsonPropertyName("token_endpoint")] string TokenEndpoint,
        [property: JsonPropertyName("response_types_supported")] string[] ResponseTypesSupported,
        [property: JsonPropertyName("grant_types_supported")] string[] GrantTypesSupported,
        [property: JsonPropertyName("code_challenge_methods_supported")] string[] CodeChallengeMethodsSupported,
        [property: JsonPropertyName("token_endpoint_auth_methods_supported")] string[] TokenEndpointAuthMethodsSupported);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("refresh_token")] string RefreshToken);

    private sealed record OAuthErrorResponse(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);
}

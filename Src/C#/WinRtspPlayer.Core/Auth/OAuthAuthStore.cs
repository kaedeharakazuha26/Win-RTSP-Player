using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinRtspPlayer.Core.Config;

namespace WinRtspPlayer.Core.Auth;

public sealed record OAuthCredentialsView(
    string ClientId,
    string ClientSecret,
    string ConsentPasscode)
{
    public override string ToString() =>
        $"OAuthCredentialsView {{ ClientId = {ClientId}, HasClientSecret = {!string.IsNullOrEmpty(ClientSecret)}, HasConsentPasscode = {!string.IsNullOrEmpty(ConsentPasscode)} }}";
}

public sealed record TokenPair(
    string AccessToken,
    string RefreshToken,
    int ExpiresInSeconds)
{
    public override string ToString() =>
        $"TokenPair {{ ExpiresInSeconds = {ExpiresInSeconds} }}";
}

/// <summary>
/// Thread-safe, DPAPI-backed OAuth 2.1 credential and token store persisted to config/mcp-auth.json.
/// Secrets and token hashes are protected with <see cref="ISecretProtector"/>; raw tokens are never persisted.
/// </summary>
public sealed class OAuthAuthStore
{
    private const int AccessTokenTtlSeconds = 3600;
    private static readonly TimeSpan RefreshTokenTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan AuthCodeTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromSeconds(60);
    private const int MaxPasscodeFailures = 5;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _authPath;
    private readonly ISecretProtector _protector;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _lock = new();

    private string _clientId = string.Empty;
    private string _clientSecret = string.Empty;
    private string _consentPasscode = string.Empty;
    private readonly List<AuthCodeEntry> _authCodes = new();
    private readonly List<TokenRecord> _accessTokens = new();
    private readonly List<TokenRecord> _refreshTokens = new();
    private readonly List<DateTimeOffset> _failedPasscodeAttempts = new();

    public OAuthAuthStore(string baseDirectory, ISecretProtector protector, Func<DateTimeOffset>? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("Base directory must not be empty.", nameof(baseDirectory));
        }

        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _authPath = Path.Combine(Path.GetFullPath(baseDirectory), "config", "mcp-auth.json");

        LoadOrInitialize();
    }

    public OAuthCredentialsView GetCredentials()
    {
        lock (_lock)
        {
            return new OAuthCredentialsView(_clientId, _clientSecret, _consentPasscode);
        }
    }

    public OAuthCredentialsView RegenerateSecrets()
    {
        lock (_lock)
        {
            GenerateFreshCredentialsLocked();
            _authCodes.Clear();
            _accessTokens.Clear();
            _refreshTokens.Clear();
            _failedPasscodeAttempts.Clear();
            SaveAtomicLocked();
            return new OAuthCredentialsView(_clientId, _clientSecret, _consentPasscode);
        }
    }

    public bool ValidateClientId(string clientId)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            return false;
        }

        lock (_lock)
        {
            return FixedTimeEqualsUtf8(clientId, _clientId);
        }
    }

    public bool ValidateClientSecret(string clientId, string clientSecret)
    {
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            return false;
        }

        lock (_lock)
        {
            bool idMatch = FixedTimeEqualsUtf8(clientId, _clientId);
            bool secretMatch = FixedTimeEqualsUtf8(clientSecret, _clientSecret);
            return idMatch & secretMatch;
        }
    }

    public bool ValidateConsentPasscode(string passcode, out bool isRateLimited)
    {
        lock (_lock)
        {
            DateTimeOffset now = _utcNow();
            _failedPasscodeAttempts.RemoveAll(t => now - t >= RateLimitWindow);

            if (_failedPasscodeAttempts.Count >= MaxPasscodeFailures)
            {
                isRateLimited = true;
                return false;
            }

            isRateLimited = false;
            if (!string.IsNullOrEmpty(passcode) && FixedTimeEqualsUtf8(passcode, _consentPasscode))
            {
                _failedPasscodeAttempts.Clear();
                return true;
            }

            _failedPasscodeAttempts.Add(now);
            return false;
        }
    }

    public string CreateAuthCode(string clientId, string redirectUri, string codeChallenge, string? resource)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            throw new ArgumentException("Client ID must not be empty.", nameof(clientId));
        }

        if (string.IsNullOrEmpty(redirectUri))
        {
            throw new ArgumentException("Redirect URI must not be empty.", nameof(redirectUri));
        }

        if (string.IsNullOrEmpty(codeChallenge))
        {
            throw new ArgumentException("Code challenge must not be empty.", nameof(codeChallenge));
        }

        string code = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        byte[] codeHash = SHA256.HashData(Encoding.UTF8.GetBytes(code));

        lock (_lock)
        {
            DateTimeOffset now = _utcNow();
            _authCodes.RemoveAll(c => c.ExpiresAtUtc <= now);
            _authCodes.Add(new AuthCodeEntry(
                CodeHash: codeHash,
                ClientId: clientId,
                RedirectUri: redirectUri,
                CodeChallenge: codeChallenge,
                Resource: resource,
                ExpiresAtUtc: now.Add(AuthCodeTtl)));
        }

        return code;
    }

    public TokenPair? ExchangeAuthCode(string code, string clientId, string redirectUri, string codeVerifier)
    {
        if (string.IsNullOrEmpty(code) ||
            string.IsNullOrEmpty(clientId) ||
            string.IsNullOrEmpty(redirectUri) ||
            string.IsNullOrEmpty(codeVerifier) ||
            !Ascii.IsValid(codeVerifier))
        {
            return null;
        }

        byte[] codeHash = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        string computedChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

        lock (_lock)
        {
            DateTimeOffset now = _utcNow();
            int matchedIndex = FindTokenHashIndex(_authCodes, static entry => entry.CodeHash, codeHash);
            if (matchedIndex < 0)
            {
                return null;
            }

            // Consume immediately so bad verifiers or mismatches cannot retry the same code.
            AuthCodeEntry entry = _authCodes[matchedIndex];
            _authCodes.RemoveAt(matchedIndex);
            _authCodes.RemoveAll(c => c.ExpiresAtUtc <= now);

            bool valid =
                entry.ExpiresAtUtc > now &
                FixedTimeEqualsUtf8(clientId, _clientId) &
                FixedTimeEqualsUtf8(clientId, entry.ClientId) &
                FixedTimeEqualsUtf8(redirectUri, entry.RedirectUri) &
                FixedTimeEqualsUtf8(computedChallenge, entry.CodeChallenge);

            if (!valid)
            {
                return null;
            }

            return IssueAndPersistTokenPairLocked(clientId, now);
        }
    }

    public TokenPair? RefreshAccessToken(string refreshToken, string clientId)
    {
        if (string.IsNullOrEmpty(refreshToken) || string.IsNullOrEmpty(clientId))
        {
            return null;
        }

        byte[] refreshHash = SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken));

        lock (_lock)
        {
            DateTimeOffset now = _utcNow();
            PruneExpiredTokensLocked(now);

            int matchedIndex = FindTokenHashIndex(_refreshTokens, static record => record.TokenHash, refreshHash);
            if (matchedIndex < 0)
            {
                return null;
            }

            TokenRecord existing = _refreshTokens[matchedIndex];
            bool valid =
                existing.ExpiresAtUtc > now &
                FixedTimeEqualsUtf8(clientId, _clientId) &
                FixedTimeEqualsUtf8(clientId, existing.ClientId);

            if (!valid)
            {
                return null;
            }

            // Rotate refresh token: invalidate old one before issuing the replacement pair.
            _refreshTokens.RemoveAt(matchedIndex);
            return IssueAndPersistTokenPairLocked(clientId, now);
        }
    }

    public bool ValidateAccessToken(string bearerToken)
    {
        if (string.IsNullOrEmpty(bearerToken))
        {
            return false;
        }

        byte[] tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(bearerToken));

        lock (_lock)
        {
            DateTimeOffset now = _utcNow();
            int matchedIndex = FindTokenHashIndex(_accessTokens, static record => record.TokenHash, tokenHash);
            if (matchedIndex < 0)
            {
                return false;
            }

            return _accessTokens[matchedIndex].ExpiresAtUtc > now;
        }
    }

    private void LoadOrInitialize()
    {
        lock (_lock)
        {
            if (File.Exists(_authPath))
            {
                if (TryLoadExistingLocked())
                {
                    return;
                }

                BackupCorruptAuthFileLocked();
            }

            GenerateFreshCredentialsLocked();
            _accessTokens.Clear();
            _refreshTokens.Clear();
            SaveAtomicLocked();
        }
    }

    private bool TryLoadExistingLocked()
    {
        try
        {
            string json = File.ReadAllText(_authPath, Encoding.UTF8);
            PersistedAuthState? loaded = JsonSerializer.Deserialize<PersistedAuthState>(json, JsonOptions);
            if (loaded is null ||
                string.IsNullOrWhiteSpace(loaded.EncryptedClientId) ||
                string.IsNullOrWhiteSpace(loaded.EncryptedClientSecret) ||
                string.IsNullOrWhiteSpace(loaded.EncryptedConsentPasscode))
            {
                return false;
            }

            string clientId = _protector.Unprotect(loaded.EncryptedClientId);
            string clientSecret = _protector.Unprotect(loaded.EncryptedClientSecret);
            string consentPasscode = _protector.Unprotect(loaded.EncryptedConsentPasscode);
            if (string.IsNullOrWhiteSpace(clientId) ||
                string.IsNullOrWhiteSpace(clientSecret) ||
                string.IsNullOrWhiteSpace(consentPasscode))
            {
                return false;
            }

            DateTimeOffset now = _utcNow();
            List<TokenRecord> accessTokens = UnprotectTokenList(loaded.AccessTokens, now);
            List<TokenRecord> refreshTokens = UnprotectTokenList(loaded.RefreshTokens, now);

            _clientId = clientId;
            _clientSecret = clientSecret;
            _consentPasscode = consentPasscode;
            _accessTokens.Clear();
            _accessTokens.AddRange(accessTokens);
            _refreshTokens.Clear();
            _refreshTokens.AddRange(refreshTokens);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException)
        {
            return false;
        }
    }

    private List<TokenRecord> UnprotectTokenList(List<PersistedTokenRecord>? records, DateTimeOffset now)
    {
        var result = new List<TokenRecord>();
        if (records is null)
        {
            return result;
        }

        foreach (PersistedTokenRecord record in records)
        {
            if (record.ExpiresAtUtc <= now ||
                string.IsNullOrWhiteSpace(record.EncryptedTokenHash) ||
                string.IsNullOrWhiteSpace(record.EncryptedClientId))
            {
                continue;
            }

            string hashHex = _protector.Unprotect(record.EncryptedTokenHash);
            string clientId = _protector.Unprotect(record.EncryptedClientId);
            byte[] hashBytes = Convert.FromHexString(hashHex);
            result.Add(new TokenRecord(hashBytes, clientId, record.ExpiresAtUtc));
        }

        return result;
    }

    private TokenPair IssueAndPersistTokenPairLocked(string clientId, DateTimeOffset now)
    {
        PruneExpiredTokensLocked(now);

        string accessToken = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        string refreshToken = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        _accessTokens.Add(new TokenRecord(
            TokenHash: SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)),
            ClientId: clientId,
            ExpiresAtUtc: now.AddSeconds(AccessTokenTtlSeconds)));

        _refreshTokens.Add(new TokenRecord(
            TokenHash: SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)),
            ClientId: clientId,
            ExpiresAtUtc: now.Add(RefreshTokenTtl)));

        SaveAtomicLocked();
        return new TokenPair(accessToken, refreshToken, AccessTokenTtlSeconds);
    }

    private void PruneExpiredTokensLocked(DateTimeOffset now)
    {
        _accessTokens.RemoveAll(t => t.ExpiresAtUtc <= now);
        _refreshTokens.RemoveAll(t => t.ExpiresAtUtc <= now);
    }

    private void GenerateFreshCredentialsLocked()
    {
        _clientId = "winrtsp-" + Base64UrlEncode(RandomNumberGenerator.GetBytes(12));
        _clientSecret = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        _consentPasscode = Base64UrlEncode(RandomNumberGenerator.GetBytes(12));
    }

    private void BackupCorruptAuthFileLocked()
    {
        if (!File.Exists(_authPath))
        {
            return;
        }

        string stamp = _utcNow().UtcDateTime.ToString("yyyyMMddHHmmssfff");
        string backupPath = $"{_authPath}.corrupt.{stamp}";
        int suffix = 1;
        while (File.Exists(backupPath))
        {
            backupPath = $"{_authPath}.corrupt.{stamp}.{suffix++}";
        }

        File.Move(_authPath, backupPath);
    }

    private void SaveAtomicLocked()
    {
        string? dir = Path.GetDirectoryName(_authPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var state = new PersistedAuthState
        {
            EncryptedClientId = _protector.Protect(_clientId),
            EncryptedClientSecret = _protector.Protect(_clientSecret),
            EncryptedConsentPasscode = _protector.Protect(_consentPasscode),
            AccessTokens = _accessTokens.Select(ProtectTokenRecord).ToList(),
            RefreshTokens = _refreshTokens.Select(ProtectTokenRecord).ToList()
        };

        string tempPath = _authPath + ".tmp";
        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(fs, state, JsonOptions);
            fs.Flush(flushToDisk: true);
        }

        File.Move(tempPath, _authPath, overwrite: true);
    }

    private PersistedTokenRecord ProtectTokenRecord(TokenRecord record) => new()
    {
        EncryptedTokenHash = _protector.Protect(Convert.ToHexString(record.TokenHash)),
        EncryptedClientId = _protector.Protect(record.ClientId),
        ExpiresAtUtc = record.ExpiresAtUtc
    };

    private static int FindTokenHashIndex<T>(IReadOnlyList<T> items, Func<T, byte[]> getHash, byte[] targetHash)
    {
        int matchedIndex = -1;
        for (int i = 0; i < items.Count; i++)
        {
            if (CryptographicOperations.FixedTimeEquals(getHash(items[i]), targetHash))
            {
                matchedIndex = i;
            }
        }

        return matchedIndex;
    }

    private static bool FixedTimeEqualsUtf8(string left, string right)
    {
        byte[] leftBytes = Encoding.UTF8.GetBytes(left);
        byte[] rightBytes = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    private sealed record AuthCodeEntry(
        byte[] CodeHash,
        string ClientId,
        string RedirectUri,
        string CodeChallenge,
        string? Resource,
        DateTimeOffset ExpiresAtUtc);

    private sealed record TokenRecord(
        byte[] TokenHash,
        string ClientId,
        DateTimeOffset ExpiresAtUtc);

    private sealed class PersistedAuthState
    {
        public string EncryptedClientId { get; set; } = string.Empty;
        public string EncryptedClientSecret { get; set; } = string.Empty;
        public string EncryptedConsentPasscode { get; set; } = string.Empty;
        public List<PersistedTokenRecord> AccessTokens { get; set; } = new();
        public List<PersistedTokenRecord> RefreshTokens { get; set; } = new();
    }

    private sealed class PersistedTokenRecord
    {
        public string EncryptedTokenHash { get; set; } = string.Empty;
        public string EncryptedClientId { get; set; } = string.Empty;
        public DateTimeOffset ExpiresAtUtc { get; set; }
    }
}

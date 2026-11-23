using System.Security.Cryptography;
using System.Text;

namespace WinRtspPlayer.Core.Config;

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}

/// <summary>
/// Protects secrets at rest using Windows DPAPI scoped to the current user.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WinRtspPlayer.v1");

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] protectedBytes = ProtectedData.Protect(plaintextBytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public string Unprotect(string ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext))
        {
            return string.Empty;
        }

        byte[] protectedBytes = Convert.FromBase64String(ciphertext);
        byte[] plaintextBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plaintextBytes);
    }
}

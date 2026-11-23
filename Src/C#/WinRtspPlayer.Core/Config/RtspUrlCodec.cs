using System.Net;
using System.Net.Sockets;

namespace WinRtspPlayer.Core.Config;

/// <summary>
/// Validates camera connection parameters and builds/parses RTSP URLs with percent-encoded credentials.
/// </summary>
public static class RtspUrlCodec
{
    private static readonly char[] ForbiddenHostChars = ['@', '/', '?', '#'];

    public static string? ValidateCameraInput(string ip, int port, string path, string transport)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return "Camera host/IP must not be empty.";
        }

        int colonCount = 0;
        foreach (char c in ip)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || Array.IndexOf(ForbiddenHostChars, c) >= 0)
            {
                return "Camera host/IP contains invalid characters.";
            }

            if (c == ':')
            {
                colonCount++;
            }
        }

        if (colonCount == 1)
        {
            return "Camera host/IP must not include a port (use the Port field instead).";
        }

        if (colonCount >= 2 &&
            (!IPAddress.TryParse(ip, out IPAddress? parsedIp) || parsedIp.AddressFamily != AddressFamily.InterNetworkV6))
        {
            return "Camera IPv6 address is invalid.";
        }

        if (Uri.CheckHostName(ip) == UriHostNameType.Unknown)
        {
            return "Camera host/IP is not a valid hostname or IP address.";
        }

        if (port is < 1 or > 65535)
        {
            return $"Port must be between 1 and 65535 (got {port}).";
        }

        if (!string.IsNullOrEmpty(path) && !path.StartsWith('/'))
        {
            return "Stream path must be empty or start with '/'.";
        }

        if (!string.Equals(transport, "tcp", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(transport, "udp", StringComparison.OrdinalIgnoreCase))
        {
            return $"Transport must be 'tcp' or 'udp' (got '{transport}').";
        }

        return null;
    }

    public static string BuildUrl(CameraConfig cam, string? plaintextPassword)
    {
        ArgumentNullException.ThrowIfNull(cam);

        string host = cam.Ip.Trim();
        if (host.Contains(':') && !host.StartsWith('[') && !host.EndsWith(']'))
        {
            host = $"[{host}]";
        }

        int port = cam.Port is >= 1 and <= 65535 ? cam.Port : 554;
        string path = cam.Path ?? string.Empty;
        if (!string.IsNullOrEmpty(path) && !path.StartsWith('/'))
        {
            path = "/" + path;
        }

        string username = cam.Username ?? string.Empty;
        string password = plaintextPassword ?? string.Empty;

        string userInfo;
        if (username.Length == 0 && password.Length == 0)
        {
            userInfo = string.Empty;
        }
        else if (password.Length == 0)
        {
            userInfo = $"{Uri.EscapeDataString(username)}@";
        }
        else
        {
            userInfo = $"{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}@";
        }

        return $"rtsp://{userInfo}{host}:{port}{path}";
    }

    public static bool TryParseUrl(string rawUrl, out ParsedRtspUrl? parsed, out string? error)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            error = "RTSP URL must not be empty.";
            return false;
        }

        string trimmed = rawUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
        {
            error = "Invalid RTSP URL format.";
            return false;
        }

        if (!string.Equals(uri.Scheme, "rtsp", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Unsupported scheme '{uri.Scheme}'. Expected 'rtsp'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "RTSP URL is missing a host.";
            return false;
        }

        if (uri.Port == 0 || uri.Port > 65535)
        {
            error = $"RTSP URL port is out of range ({uri.Port}).";
            return false;
        }

        int port = uri.Port < 0 ? 554 : uri.Port;
        string path = uri.PathAndQuery == "/" ? string.Empty : uri.PathAndQuery;

        string username = string.Empty;
        string password = string.Empty;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            int colonIndex = uri.UserInfo.IndexOf(':');
            if (colonIndex < 0)
            {
                username = Uri.UnescapeDataString(uri.UserInfo);
            }
            else
            {
                // Split on the first ':' before unescaping so percent-encoded or raw ':' in passwords stay intact.
                username = Uri.UnescapeDataString(uri.UserInfo[..colonIndex]);
                password = Uri.UnescapeDataString(uri.UserInfo[(colonIndex + 1)..]);
            }
        }

        parsed = new ParsedRtspUrl(uri.Host, port, path, username, password);
        error = null;
        return true;
    }
}

using WinRtspPlayer.Core.Config;
using Xunit;

namespace WinRtspPlayer.Tests;

public class RtspUrlCodecTests
{
    [Fact]
    public void BuildUrl_PercentEncodesSpecialCharactersInCredentials()
    {
        var cam = new CameraConfig
        {
            Id = "cam1",
            Slot = 1,
            Name = "Front",
            Ip = "192.168.1.10",
            Port = 554,
            Path = "/stream1",
            Username = "admin@1"
        };

        string url = RtspUrlCodec.BuildUrl(cam, "p@ss:w/ord#?%");

        Assert.Equal("rtsp://admin%401:p%40ss%3Aw%2Ford%23%3F%25@192.168.1.10:554/stream1", url);

        bool ok = RtspUrlCodec.TryParseUrl(url, out ParsedRtspUrl? parsed, out string? error);

        Assert.True(ok, error);
        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal("192.168.1.10", parsed.Ip);
        Assert.Equal(554, parsed.Port);
        Assert.Equal("/stream1", parsed.Path);
        Assert.Equal("admin@1", parsed.Username);
        Assert.Equal("p@ss:w/ord#?%", parsed.Password);
    }

    [Fact]
    public void BuildUrl_OmitsCredentialsWhenEmpty()
    {
        var cam = new CameraConfig
        {
            Id = "cam1",
            Slot = 1,
            Name = "Lobby",
            Ip = "192.168.1.10",
            Port = 554,
            Path = "/stream1",
            Username = string.Empty
        };

        string url = RtspUrlCodec.BuildUrl(cam, string.Empty);

        Assert.Equal("rtsp://192.168.1.10:554/stream1", url);
    }

    [Theory]
    [InlineData("", 554, "/stream1", "tcp")]
    [InlineData("   ", 554, "/stream1", "tcp")]
    [InlineData("192.168.1.10 1", 554, "/stream1", "tcp")]
    [InlineData("user@192.168.1.10", 554, "/stream1", "tcp")]
    [InlineData("192.168.1.10/stream", 554, "/stream1", "tcp")]
    [InlineData("192.168.1.10?a=1", 554, "/stream1", "tcp")]
    [InlineData("192.168.1.10#frag", 554, "/stream1", "tcp")]
    [InlineData("192.168.1.10\n", 554, "/stream1", "tcp")]
    [InlineData("192.168.1.10:8554", 554, "/stream1", "tcp")]
    [InlineData("cam.local:554", 554, "/stream1", "tcp")]
    [InlineData("[::1]:8554", 554, "/stream1", "tcp")]
    [InlineData(":::", 554, "/stream1", "tcp")]
    [InlineData("192.168.1.10", 0, "/stream1", "tcp")]
    [InlineData("192.168.1.10", 70000, "/stream1", "tcp")]
    [InlineData("192.168.1.10", 554, "stream1", "tcp")]
    [InlineData("192.168.1.10", 554, "/stream1", "http")]
    public void ValidateCameraInput_RejectsInvalidHostPortPathAndTransport(
        string ip, int port, string path, string transport)
    {
        string? error = RtspUrlCodec.ValidateCameraInput(ip, port, path, transport);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("192.168.1.10", 554, "", "tcp")]
    [InlineData("cam.local", 8554, "/live/ch0?sub=1", "UDP")]
    [InlineData("[::1]", 554, "/stream1", "Tcp")]
    [InlineData("::1", 554, "/stream1", "tcp")]
    [InlineData("fe80::1", 554, "/stream1", "tcp")]
    public void ValidateCameraInput_AcceptsValidInputs(
        string ip, int port, string path, string transport)
    {
        string? error = RtspUrlCodec.ValidateCameraInput(ip, port, path, transport);
        Assert.Null(error);
    }

    [Fact]
    public void TryParseUrl_SupportsIpv6DefaultPortAndRootPath()
    {
        bool ok = RtspUrlCodec.TryParseUrl("rtsp://[::1]/", out ParsedRtspUrl? parsed, out string? error);

        Assert.True(ok, error);
        Assert.NotNull(parsed);
        Assert.Equal("[::1]", parsed.Ip);
        Assert.Equal(554, parsed.Port);
        Assert.Equal(string.Empty, parsed.Path);
        Assert.Equal(string.Empty, parsed.Username);
        Assert.Equal(string.Empty, parsed.Password);
    }

    [Fact]
    public void TryParseUrl_PreservesColonsInPassword()
    {
        bool ok = RtspUrlCodec.TryParseUrl(
            "rtsp://admin:pass:123:xyz@10.0.0.5:8554/ch1",
            out ParsedRtspUrl? parsed,
            out string? error);

        Assert.True(ok, error);
        Assert.NotNull(parsed);
        Assert.Equal("admin", parsed.Username);
        Assert.Equal("pass:123:xyz", parsed.Password);
        Assert.Equal("10.0.0.5", parsed.Ip);
        Assert.Equal(8554, parsed.Port);
        Assert.Equal("/ch1", parsed.Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://192.168.1.10/stream1")]
    [InlineData("rtsp://")]
    public void TryParseUrl_RejectsInvalidOrNonRtspUrls(string rawUrl)
    {
        bool ok = RtspUrlCodec.TryParseUrl(rawUrl, out ParsedRtspUrl? parsed, out string? error);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}

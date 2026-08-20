using System;
using System.IO;
using LibVLCSharp.Shared;
using MissionPlannerAvalonia.Services;
using Xunit;

namespace MissionPlannerAvalonia.Tests;

public class VideoSourceResolverTests {
  [Theory]
  [InlineData("udp://:5600", "udp://@:5600")]
  [InlineData("rtp://:5600", "rtp://@:5600")]
  [InlineData("rtsp://192.168.1.2/live", "rtsp://192.168.1.2/live")]
  [InlineData("/dev/video0", "v4l2:///dev/video0")]
  public void NormalizesCommonStreamSources(string input, string expected) {
    var resolved = VideoSourceResolver.Resolve(input);
    Assert.Equal(expected, resolved.Mrl);
    Assert.Equal(FromType.FromLocation, resolved.FromType);
  }

  [Fact]
  public void ConvertsRtpGstreamerPipelineToSdp() {
    var resolved = VideoSourceResolver.Resolve(
        "udpsrc port=5600 ! application/x-rtp,encoding-name=H265,payload=97 ! rtph265depay");
    try {
      Assert.Equal(FromType.FromPath, resolved.FromType);
      Assert.True(File.Exists(resolved.Mrl));
      string sdp = File.ReadAllText(resolved.Mrl);
      Assert.Contains("m=video 5600 RTP/AVP 97", sdp);
      Assert.Contains("a=rtpmap:97 H265/90000", sdp);
    } finally {
      if (resolved.TemporaryFile != null && File.Exists(resolved.TemporaryFile)) {
        File.Delete(resolved.TemporaryFile);
      }
    }
  }

  [Fact]
  public void ConvertsTypedH265GstreamerCapsToSdp() {
    var resolved = VideoSourceResolver.Resolve(
        "udpsrc port=(int)5601 ! application/x-rtp,encoding-name=(string)H265,"
        + "payload=(int)98 ! rtph265depay");
    try {
      string sdp = File.ReadAllText(resolved.Mrl);
      Assert.Contains("m=video 5601 RTP/AVP 98", sdp);
      Assert.Contains("a=rtpmap:98 H265/90000", sdp);
    } finally {
      if (resolved.TemporaryFile != null && File.Exists(resolved.TemporaryFile)) {
        File.Delete(resolved.TemporaryFile);
      }
    }
  }

  [Fact]
  public void RejectsPipelineWithoutUdpPort() {
    Assert.Throws<FormatException>(() =>
        VideoSourceResolver.Resolve("udpsrc ! rtph264depay ! avdec_h264"));
  }

  [Fact]
  public void RejectsNonRtpGstreamerPipelineWithActionableError() {
    var error = Assert.Throws<FormatException>(() =>
        VideoSourceResolver.Resolve("udpsrc port=5600 ! video/x-h264 ! avdec_h264"));
    Assert.Contains("Only RTP", error.Message);
  }

  [Fact]
  public void LinuxBackendInitializesWhenRuntimeIsInstalled() {
    if (!OperatingSystem.IsLinux()
        || (!File.Exists("/usr/lib/x86_64-linux-gnu/libvlc.so.5")
            && !File.Exists("/usr/lib/aarch64-linux-gnu/libvlc.so.5"))) {
      return;
    }

    LibVlcBootstrap.Initialize();
    using var libVlc = new LibVLCSharp.Shared.LibVLC("--no-video-title-show", "--quiet");
    Assert.NotEmpty(libVlc.Version);
  }
}

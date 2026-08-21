using System.Text;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Tests;

public class MavlinkVideoStreamsTests {
  [Fact]
  public void Built_in_video_presets_are_valid_cross_platform_mrls() {
    Assert.StartsWith("http://", FlightDataViewModel.DefaultVideoSource("mjpeg"));
    Assert.StartsWith("rtsp://", FlightDataViewModel.DefaultVideoSource("gstreamer"));
    Assert.StartsWith("rtsp://", FlightDataViewModel.DefaultVideoSource("herelink"));
    Assert.False(string.IsNullOrWhiteSpace(FlightDataViewModel.DefaultVideoSource("camera")));
  }

  [Fact]
  public void Rtsp_announcements_become_direct_libvlc_sources() {
    var stream = Stream(MAVLink.VIDEO_STREAM_TYPE.RTSP, "camera.local:8554/main", "Front");

    Assert.True(MavlinkVideoStreams.TryCreate((1, 100, 2), stream, out var option));
    Assert.NotNull(option);
    Assert.Equal("Front", option.Name);
    Assert.Equal("rtsp://camera.local:8554/main", option.Source);
  }

  [Fact]
  public void Rtp_announcements_reuse_the_upstream_pipeline_and_local_sdp_adapter() {
    var stream = Stream(MAVLink.VIDEO_STREAM_TYPE.RTPUDP, "5600", "RTP");
    stream.encoding = (byte)MAVLink.VIDEO_STREAM_ENCODING.H265;

    Assert.True(MavlinkVideoStreams.TryCreate((1, 100, 1), stream, out var option));
    Assert.Contains("udpsrc port=5600", option!.Source);
    Assert.Contains("encoding-name=(string)H265", option.Source);

    var resolved = VideoSourceResolver.Resolve(option.Source);
    try {
      Assert.Equal(LibVLCSharp.Shared.FromType.FromPath, resolved.FromType);
      Assert.True(File.Exists(resolved.TemporaryFile));
    } finally {
      if (resolved.TemporaryFile != null) {
        File.Delete(resolved.TemporaryFile);
      }
    }
  }

  [Theory]
  [InlineData("5601", "udp://@:5601")]
  [InlineData("udp://127.0.0.1:5000", "udp://@:5000")]
  public void Mpeg_ts_announcements_become_libvlc_udp_listeners(string uri, string expected) {
    var stream = Stream(MAVLink.VIDEO_STREAM_TYPE.MPEG_TS, uri, "Transport stream");

    Assert.True(MavlinkVideoStreams.TryCreate((2, 101, 3), stream, out var option));
    Assert.Equal(expected, option!.Source);
  }

  [Fact]
  public void Unsupported_or_invalid_announcements_are_not_offered() {
    var invalid = Stream(MAVLink.VIDEO_STREAM_TYPE.MPEG_TS, "not-a-port", "Broken");
    var unsupported = Stream((MAVLink.VIDEO_STREAM_TYPE)99, "udp://host:5600", "Unknown");

    Assert.False(MavlinkVideoStreams.TryCreate((1, 1, 1), invalid, out _));
    Assert.False(MavlinkVideoStreams.TryCreate((1, 1, 2), unsupported, out _));
  }

  private static MAVLink.mavlink_video_stream_information_t Stream(
      MAVLink.VIDEO_STREAM_TYPE type, string uri, string name) => new() {
        type = (byte)type,
        uri = Encoding.UTF8.GetBytes(uri + "\0"),
        name = Encoding.UTF8.GetBytes(name + "\0"),
        stream_id = 1,
        resolution_h = 1080,
        resolution_v = 1920,
        framerate = 30,
      };
}

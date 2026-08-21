using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Tests;

public class DeveloperToolParsersTests {
  [Fact]
  public void ParseBytes_accepts_compact_hex() {
    Assert.Equal(new byte[] { 0xfd, 0x05, 0x00, 0xa1 },
        DeveloperToolParsers.ParseBytes("0xfd0500a1"));
  }

  [Fact]
  public void ParseBytes_accepts_decimal_and_explicit_hex_tokens() {
    Assert.Equal(new byte[] { 253, 5, 0, 161 },
        DeveloperToolParsers.ParseBytes("253, 5 0 0xA1"));
  }

  [Theory]
  [InlineData("")]
  [InlineData("abc")]
  [InlineData("256 0")]
  [InlineData("0xGG 1")]
  public void ParseBytes_rejects_invalid_input(string input) {
    Assert.Throws<FormatException>(() => DeveloperToolParsers.ParseBytes(input));
  }

  [Fact]
  public void DecodeHardwareId_reports_bus_fields() {
    const uint id = 2u | (3u << 3) | (42u << 8) | (7u << 16);

    var result = DeveloperToolParsers.DecodeHardwareId(id.ToString(), "COMPASS_DEV_ID");

    Assert.Contains("COMPASS_DEV_ID", result);
    Assert.Contains("bus type SPI", result);
    Assert.Contains("bus 3", result);
    Assert.Contains("address 42", result);
  }
}

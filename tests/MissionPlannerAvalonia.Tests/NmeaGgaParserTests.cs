using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Tests;

public class NmeaGgaParserTests {
  [Fact]
  public void Parses_standard_gps_gga_fix() {
    const string sentence =
        "$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47";

    Assert.True(NmeaGgaParser.TryParse(sentence, out var fix, out string error), error);
    Assert.Equal(48.1173, fix.Latitude, 6);
    Assert.Equal(11.5166667, fix.Longitude, 6);
    Assert.Equal(545.4, fix.AltitudeM, 3);
    Assert.Equal(8, fix.Satellites);
    Assert.Equal(0.9, fix.Hdop, 3);
  }

  [Fact]
  public void Parses_gn_talker_and_southern_western_coordinates() {
    string sentence = WithChecksum(
        "$GNGGA,092750.000,5321.6802,S,00630.3372,W,2,10,0.8,61.7,M,55.2,M,,");

    Assert.True(NmeaGgaParser.TryParse(sentence, out var fix, out string error), error);
    Assert.Equal(-53.3613367, fix.Latitude, 6);
    Assert.Equal(-6.50562, fix.Longitude, 6);
    Assert.Equal(61.7, fix.AltitudeM, 3);
  }

  [Fact]
  public void Rejects_bad_checksum_and_no_fix() {
    Assert.False(NmeaGgaParser.TryParse(
        "$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*00",
        out _, out string checksumError));
    Assert.Contains("checksum", checksumError, StringComparison.OrdinalIgnoreCase);

    string noFix = WithChecksum(
        "$GPGGA,123519,4807.038,N,01131.000,E,0,00,99.9,545.4,M,46.9,M,,");
    Assert.False(NmeaGgaParser.TryParse(noFix, out _, out string fixError));
    Assert.Contains("no position fix", fixError, StringComparison.OrdinalIgnoreCase);
  }

  private static string WithChecksum(string body) =>
      body + "*" + NmeaGgaParser.Checksum(body);
}

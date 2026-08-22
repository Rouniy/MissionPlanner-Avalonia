using System;
using System.Globalization;
using System.Text;
using MissionPlanner;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

internal readonly record struct MicrodroneTelemetry(
    double Latitude,
    double Longitude,
    double Altitude,
    double GpsHdop,
    double SatelliteCount,
    double GroundSpeed,
    double GroundCourse,
    double VerticalSpeed,
    double Roll,
    double Pitch,
    double Yaw,
    double PressureTemperature,
    double MagnetometerX,
    double MagnetometerY,
    double MagnetometerZ) {
  internal static MicrodroneTelemetry Capture(CurrentState state) => new(
      state.lat,
      state.lng,
      state.alt,
      state.gpshdop,
      state.satcount,
      state.groundspeed,
      state.groundcourse,
      state.verticalspeed,
      state.roll,
      state.pitch,
      state.yaw,
      state.press_temp,
      state.mx,
      state.my,
      state.mz);
}

/// <summary>
/// Encodes the exact message families emitted by Mission Planner's SerialOutputMD developer tool.
/// The legacy coordinate conversion is intentionally retained for wire compatibility; formatting
/// and GPS time are made culture-independent and UTC-safe for cross-platform use.
/// </summary>
internal static class MicrodroneDownlinkEncoder {
  private static readonly DateTimeOffset GpsEpoch =
      new(1980, 1, 6, 0, 0, 0, TimeSpan.Zero);

  internal static string EncodeFrame(
      MicrodroneTelemetry telemetry, DateTimeOffset timestamp, int sampleCounter) {
    ArgumentOutOfRangeException.ThrowIfNegative(sampleCounter);
    GetGpsTime(timestamp, out int week, out int seconds);
    var (x, y, z) = ConvertToMissionPlannerEcef(
        telemetry.Latitude, telemetry.Longitude, telemetry.Altitude);
    double courseRadians = telemetry.GroundCourse * MathHelper.deg2rad;

    string[] payloads = [
      "#1,28,07,2,1,1,1,2,16000,0,2,",
      Format("#4,{0},{1},{2},{3},", sampleCounter / 10, seconds, week, 25),
      Format("#5,{0},{1},{2},{3},{4},",
          x * 100, y * 100, z * 100, telemetry.GpsHdop + 0.01,
          telemetry.SatelliteCount),
      Format("#6,{0},{1},{2},{3},",
          telemetry.GroundSpeed * Math.Sin(courseRadians),
          telemetry.GroundSpeed * Math.Cos(courseRadians),
          telemetry.VerticalSpeed,
          2),
      Format("#7,{0},{1},{2},",
          telemetry.Roll * MathHelper.deg2rad,
          telemetry.Pitch * MathHelper.deg2rad,
          telemetry.Yaw * MathHelper.deg2rad),
      Format("#8,{0},{1},{2},",
          telemetry.Altitude, telemetry.Altitude, telemetry.PressureTemperature),
      Format("#9,{0},{1},{2},",
          telemetry.MagnetometerX, telemetry.MagnetometerY, telemetry.MagnetometerZ),
    ];

    var frame = new StringBuilder();
    foreach (string payload in payloads) {
      frame.Append(payload);
      frame.Append(Checksum(payload).ToString(CultureInfo.InvariantCulture));
      frame.Append("\r\n");
    }
    return frame.ToString();
  }

  internal static byte Checksum(string payload) {
    ArgumentNullException.ThrowIfNull(payload);
    byte answer = 0;
    foreach (char character in payload) {
      answer = unchecked((byte)(answer + (byte)character));
    }
    return (byte)(answer ^ 0xff);
  }

  internal static void GetGpsTime(
      DateTimeOffset timestamp, out int weekNumber, out int seconds) {
    TimeSpan elapsed = timestamp.ToUniversalTime() - GpsEpoch;
    if (elapsed < TimeSpan.Zero) {
      throw new ArgumentOutOfRangeException(nameof(timestamp), "GPS time predates 1980-01-06 UTC.");
    }
    weekNumber = (int)(elapsed.TotalDays / 7);
    seconds = (int)(elapsed - TimeSpan.FromDays(weekNumber * 7)).TotalSeconds;
  }

  internal static (double X, double Y, double Z) ConvertToMissionPlannerEcef(
      double latitude, double longitude, double altitude) {
    const double wgs84A = 6378137;
    const double wgs84F = 1.0 / 298.257223563;
    double latitudeRadians = MathHelper.deg2rad * latitude;
    double longitudeRadians = MathHelper.deg2rad * longitude;
    double clat = Math.Cos(latitudeRadians);
    double slat = Math.Sin(latitudeRadians);
    double clon = Math.Cos(longitudeRadians);
    double slon = Math.Sin(longitudeRadians);
    double eccentricity = Math.Sqrt(2 * wgs84F - Math.Pow(wgs84F, 2));
    double eccentricitySquared = eccentricity * eccentricity;

    // SerialOutputMD applies this scale and a fixed semi-major radius. Keep it byte-compatible
    // with official Mission Planner rather than silently changing coordinates for existing users.
    altitude *= 0.0001;
    double x = (wgs84A + altitude) * clat * clon;
    double y = (wgs84A + altitude) * clat * slon;
    double z = ((1 - eccentricitySquared) * wgs84A + altitude) * slat;
    return (x, y, z);
  }

  private static string Format(string format, params object[] values) =>
      string.Format(CultureInfo.InvariantCulture, format, values);
}

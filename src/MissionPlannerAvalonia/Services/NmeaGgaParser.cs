using System;
using System.Globalization;

namespace MissionPlannerAvalonia.Services;

internal readonly record struct NmeaGgaFix(
    double Latitude,
    double Longitude,
    double AltitudeM,
    int Satellites,
    double Hdop,
    int FixQuality,
    double? GeoidSeparationM) {
  /// <summary>
  /// GGA altitude is mean sea level. Open Drone ID requires WGS84/geodetic altitude,
  /// which is MSL plus the GGA geoid separation. -1000 is MAVLink's explicit unknown value.
  /// </summary>
  internal double GeodeticAltitudeM => GeoidSeparationM is { } separation
      ? AltitudeM + separation
      : -1000;
}

internal static class NmeaGgaParser {
  internal static bool TryParse(string? sentence, out NmeaGgaFix fix, out string error) {
    fix = default;
    error = "Invalid GGA sentence.";
    if (string.IsNullOrWhiteSpace(sentence)) {
      return false;
    }

    string value = sentence.Trim();
    int checksumSeparator = value.IndexOf('*');
    if (checksumSeparator <= 1 || checksumSeparator + 2 >= value.Length) {
      error = "NMEA checksum is missing.";
      return false;
    }

    string kind = value.Length >= 6 ? value[..6] : value;
    if (!kind.Equals("$GPGGA", StringComparison.OrdinalIgnoreCase)
        && !kind.Equals("$GNGGA", StringComparison.OrdinalIgnoreCase)) {
      error = "Not a GGA sentence.";
      return false;
    }

    string expected = value.Substring(checksumSeparator + 1, 2);
    if (!expected.Equals(Checksum(value), StringComparison.OrdinalIgnoreCase)) {
      error = "NMEA checksum mismatch.";
      return false;
    }

    string[] fields = value[..checksumSeparator].Split(',');
    if (fields.Length < 10) {
      error = "GGA sentence has too few fields.";
      return false;
    }

    if (!int.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int quality)
        || quality <= 0) {
      error = "GPS has no position fix.";
      return false;
    }

    if (!TryCoordinate(fields[2], fields[3], latitude: true, out double latitude)
        || !TryCoordinate(fields[4], fields[5], latitude: false, out double longitude)) {
      error = "GGA latitude or longitude is invalid.";
      return false;
    }

    if (!double.TryParse(fields[9], NumberStyles.Float, CultureInfo.InvariantCulture,
            out double altitudeM)
        || !double.IsFinite(altitudeM)) {
      error = "GGA altitude is invalid.";
      return false;
    }

    _ = int.TryParse(fields[7], NumberStyles.Integer, CultureInfo.InvariantCulture,
        out int satellites);
    _ = double.TryParse(fields[8], NumberStyles.Float, CultureInfo.InvariantCulture,
        out double hdop);
    double? geoidSeparationM = null;
    if (fields.Length > 11 && !string.IsNullOrWhiteSpace(fields[11])) {
      if (!double.TryParse(fields[11], NumberStyles.Float, CultureInfo.InvariantCulture,
              out double separation)
          || !double.IsFinite(separation)) {
        error = "GGA geoid separation is invalid.";
        return false;
      }
      geoidSeparationM = separation;
    }
    fix = new NmeaGgaFix(
        latitude, longitude, altitudeM, satellites, hdop, quality, geoidSeparationM);
    error = "";
    return true;
  }

  internal static string Checksum(string sentence) {
    int checksum = 0;
    foreach (char character in sentence) {
      if (character == '$') {
        continue;
      }
      if (character == '*') {
        break;
      }
      checksum ^= Convert.ToByte(character);
    }
    return checksum.ToString("X2", CultureInfo.InvariantCulture);
  }

  private static bool TryCoordinate(
      string value, string hemisphere, bool latitude, out double coordinate) {
    coordinate = 0;
    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double raw)
        || !double.IsFinite(raw) || raw < 0) {
      return false;
    }

    double degrees = Math.Truncate(raw / 100);
    double minutes = raw - degrees * 100;
    if (minutes is < 0 or >= 60) {
      return false;
    }

    coordinate = degrees + minutes / 60;
    if (hemisphere.Equals(latitude ? "S" : "W", StringComparison.OrdinalIgnoreCase)) {
      coordinate *= -1;
    } else if (!hemisphere.Equals(latitude ? "N" : "E", StringComparison.OrdinalIgnoreCase)) {
      return false;
    }

    double limit = latitude ? 90 : 180;
    return coordinate >= -limit && coordinate <= limit;
  }
}

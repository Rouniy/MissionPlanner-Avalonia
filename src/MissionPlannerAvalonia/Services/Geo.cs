using System.Globalization;
using GeoUtility.GeoSystem;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

public static class Geo {
  public static (int Zone, double Easting, double Northing) ToUtm(double lat, double lng) {
    var p = new PointLatLngAlt(lat, lng);
    int zone = p.GetUTMZone();
    var e = p.ToUTM();
    return (zone, e[0], e[1]);
  }

  public static (double Lat, double Lng) FromUtm(double easting, double northing, int zone) {
    var lla = new utmpos(easting, northing, zone).ToLLA();
    return (lla.Lat, lla.Lng);
  }

  public static bool TryParseUtmZone(string? text, out int zone) {
    zone = 0;
    if (string.IsNullOrWhiteSpace(text)) {
      return false;
    }

    string value = text.Trim().ToUpperInvariant();
    int hemisphere = 1;
    if (value.EndsWith('S')) {
      hemisphere = -1;
      value = value[..^1].Trim();
    } else if (value.EndsWith('N')) {
      value = value[..^1].Trim();
    }

    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)) {
      return false;
    }
    if (number < 0) {
      hemisphere = -1;
      number = System.Math.Abs(number);
    }
    if (number is < 1 or > 60) {
      return false;
    }

    zone = number * hemisphere;
    return true;
  }

  public static string ToMgrs(double lat, double lng) =>
      new Geographic(lng, lat).ConvertTo<MGRS>().ToString();

  public static (double Lat, double Lng) FromMgrs(string mgrs) {
    var g = new MGRS(mgrs).ConvertTo<Geographic>();
    return (g.Latitude, g.Longitude);
  }
}

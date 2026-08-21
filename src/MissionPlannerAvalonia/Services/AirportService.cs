using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

internal readonly record struct AirportMapItem(
    double Lat, double Lng, string Name, int RadiusMeters);

internal readonly record struct AirportLoadResult(
    bool Available, int Count, string? Error);

internal static class AirportService {
  internal const int DefaultRadiusMeters = 9000;
  internal const int AustraliaRadiusMeters = 5559;
  internal const int HoverSizePixels = 50;
  internal const double MinimumZoomExclusive = 3;

  private static readonly Lazy<Task<AirportLoadResult>> _loadTask = new(
      () => Task.Run(LoadDatabase));
  private static readonly object _queryLock = new();

  internal static string DatabasePath => Path.Combine(AppPaths.InstallRoot, "airports.csv");

  internal static double MaximumVisibleResolution =>
      156543.03392804097 / Math.Pow(2, MinimumZoomExclusive);

  internal static Task<AirportLoadResult> EnsureLoadedAsync() => _loadTask.Value;

  internal static IReadOnlyList<AirportMapItem> GetNearby(double lat, double lng) {
    Task<AirportLoadResult> load = EnsureLoadedAsync();
    if (!load.IsCompletedSuccessfully || !load.Result.Available) {
      return Array.Empty<AirportMapItem>();
    }

    try {
      // Upstream returns its mutable static cache after releasing its private lock. Serialize
      // our callers and copy immediately so neither map retains that shared list.
      PointLatLngAlt[] nearby;
      lock (_queryLock) {
        nearby = Airports.getAirports(new PointLatLngAlt(lat, lng)).ToArray();
      }
      return nearby
          .Where(item => double.IsFinite(item.Lat) && double.IsFinite(item.Lng)
                         && item.Lat is >= -90 and <= 90
                         && item.Lng is >= -180 and <= 180)
          .Select(item => new AirportMapItem(
              item.Lat, item.Lng, item.Tag, RadiusFor(item.Lat, item.Lng)))
          .ToArray();
    } catch (Exception ex) {
      log4net.LogManager.GetLogger(typeof(AirportService))
          .Warn("Could not query the Mission Planner airport database", ex);
      return Array.Empty<AirportMapItem>();
    }
  }

  internal static int RadiusFor(double lat, double lng) =>
      lat < -10 && lng > 109 && lng < 180
          ? AustraliaRadiusMeters
          : DefaultRadiusMeters;

  internal static double DistanceMeters(double lat1, double lng1, double lat2, double lng2) =>
      new PointLatLngAlt(lat1, lng1).GetDistance(new PointLatLngAlt(lat2, lng2));

  private static AirportLoadResult LoadDatabase() {
    var log = log4net.LogManager.GetLogger(typeof(AirportService));
    try {
      if (!File.Exists(DatabasePath)) {
        string message = $"Airport database is missing: {DatabasePath}";
        log.Warn(message);
        return new AirportLoadResult(false, 0, message);
      }

      Airports.ReadOurairports(DatabasePath);
      // Match MainV2.BGLoadAirports: duplicate checks are enabled only after the bulk import,
      // otherwise the official 55k-line data load degenerates into an O(n^2) scan.
      Airports.checkdups = true;
      int count = Airports.GetAirportCount;
      log.Info($"Loaded {count} airports from {DatabasePath}");
      return new AirportLoadResult(count > 0, count,
          count > 0 ? null : "Airport database did not contain any supported airports.");
    } catch (Exception ex) {
      log.Warn($"Could not load airport database {DatabasePath}", ex);
      return new AirportLoadResult(false, Airports.GetAirportCount, ex.Message);
    }
  }
}

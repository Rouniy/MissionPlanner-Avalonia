using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MissionPlannerAvalonia.Services;

internal sealed record HongKongNoFlyZone(
    string Name,
    string Description,
    IReadOnlyList<(double Lat, double Lng)> Outer,
    IReadOnlyList<IReadOnlyList<(double Lat, double Lng)>> Holes);

internal sealed record HongKongNoFlyResult(
    IReadOnlyList<HongKongNoFlyZone> Zones,
    bool FromCache,
    bool Stale);

/// <summary>
/// Cancellable, bounded port of Mission Planner's official Hong Kong CAD eSUA no-fly feed.
/// Unlike the upstream implementation, this service never contacts Cloudflare to geolocate the
/// operator. The UI calls it only after the separate Hong Kong feed option is enabled.
/// </summary>
internal sealed class HongKongNoFlyService {
  internal const int MaxResponseBytes = 16 * 1024 * 1024;
  internal const int MaxFeatures = 5_000;
  internal const int MaxCoordinates = 500_000;
  internal static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);

  // This is the public browser API URL embedded by the pinned official Mission Planner source,
  // not an operator credential. Keep it isolated so it is never included in status/error text.
  internal static readonly Uri OfficialFeed = new(
      "https://esua.cad.gov.hk/web/droneMap/api/nfz?apiKey=a04e6ffec803f6c08126423c32316712");

  private static readonly HttpClient _sharedHttp = CreateSharedHttp();

  internal static HongKongNoFlyService Shared { get; } = new(
      _sharedHttp,
      Path.Combine(AppPaths.CacheRoot, "nofly", "hknfz.json"),
      TimeProvider.System);

  private readonly HttpClient _http;
  private readonly string _cachePath;
  private readonly TimeProvider _time;
  private readonly SemaphoreSlim _gate = new(1, 1);

  internal HongKongNoFlyService(HttpClient http, string cachePath, TimeProvider time) {
    _http = http ?? throw new ArgumentNullException(nameof(http));
    _cachePath = Path.GetFullPath(cachePath ?? throw new ArgumentNullException(nameof(cachePath)));
    _time = time ?? throw new ArgumentNullException(nameof(time));
  }

  internal async Task<HongKongNoFlyResult> LoadAsync(CancellationToken cancellationToken) {
    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      cancellationToken.ThrowIfCancellationRequested();
      IReadOnlyList<HongKongNoFlyZone>? cached = null;
      bool fresh = false;
      if (File.Exists(_cachePath)) {
        try {
          byte[] bytes = await ReadBoundedFileAsync(_cachePath, cancellationToken)
              .ConfigureAwait(false);
          cached = Parse(bytes);
          DateTimeOffset modified = new(File.GetLastWriteTimeUtc(_cachePath), TimeSpan.Zero);
          fresh = modified + CacheLifetime >= _time.GetUtcNow();
          if (fresh) {
            cancellationToken.ThrowIfCancellationRequested();
            return new HongKongNoFlyResult(cached, FromCache: true, Stale: false);
          }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
          throw;
        } catch {
          cached = null;
        }
      }

      try {
        byte[] downloaded = await DownloadAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<HongKongNoFlyZone> parsed = Parse(downloaded);
        cancellationToken.ThrowIfCancellationRequested();
        await WriteCacheAtomicAsync(downloaded, cancellationToken).ConfigureAwait(false);
        return new HongKongNoFlyResult(parsed, FromCache: false, Stale: false);
      } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
        throw;
      } catch when (cached != null) {
        return new HongKongNoFlyResult(cached, FromCache: true, Stale: true);
      }
    } finally {
      _gate.Release();
    }
  }

  internal static IReadOnlyList<HongKongNoFlyZone> Parse(ReadOnlyMemory<byte> json) {
    if (json.Length == 0 || json.Length > MaxResponseBytes) {
      throw new InvalidDataException(
          $"Hong Kong no-fly response must contain 1 to {MaxResponseBytes} bytes.");
    }

    using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions {
      AllowTrailingCommas = false,
      CommentHandling = JsonCommentHandling.Disallow,
      MaxDepth = 64,
    });
    JsonElement root = document.RootElement;
    if (root.ValueKind != JsonValueKind.Object
        || !root.TryGetProperty("type", out JsonElement type)
        || type.GetString() != "FeatureCollection"
        || !root.TryGetProperty("features", out JsonElement features)
        || features.ValueKind != JsonValueKind.Array) {
      throw new InvalidDataException("Hong Kong no-fly response is not a GeoJSON FeatureCollection.");
    }
    if (features.GetArrayLength() > MaxFeatures) {
      throw new InvalidDataException(
          $"Hong Kong no-fly response exceeds the {MaxFeatures}-feature safety limit.");
    }

    var zones = new List<HongKongNoFlyZone>();
    int coordinateCount = 0;
    int featureNumber = 0;
    foreach (JsonElement feature in features.EnumerateArray()) {
      featureNumber++;
      if (feature.ValueKind != JsonValueKind.Object
          || !feature.TryGetProperty("geometry", out JsonElement geometry)
          || geometry.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) {
        continue;
      }
      string name = ReadProperty(feature, "name", $"Hong Kong zone {featureNumber}");
      string description = ReadProperty(feature, "description", "");
      ParseGeometry(geometry, name, description, zones, ref coordinateCount);
    }
    if (zones.Count == 0) {
      throw new InvalidDataException("Hong Kong no-fly response contains no polygon geometry.");
    }
    return zones;
  }

  private static void ParseGeometry(
      JsonElement geometry,
      string name,
      string description,
      List<HongKongNoFlyZone> zones,
      ref int coordinateCount) {
    if (!geometry.TryGetProperty("type", out JsonElement type)
        || type.ValueKind != JsonValueKind.String
        || !geometry.TryGetProperty("coordinates", out JsonElement coordinates)
        || coordinates.ValueKind != JsonValueKind.Array) {
      throw new InvalidDataException("Hong Kong no-fly feature has invalid GeoJSON geometry.");
    }

    switch (type.GetString()) {
      case "Polygon":
        zones.Add(ParsePolygon(coordinates, name, description, ref coordinateCount));
        break;
      case "MultiPolygon":
        int part = 0;
        foreach (JsonElement polygon in coordinates.EnumerateArray()) {
          part++;
          zones.Add(ParsePolygon(
              polygon,
              part == 1 ? name : $"{name} ({part})",
              description,
              ref coordinateCount));
        }
        break;
      default:
        // The official UI renders polygons only. Ignore future point/line metadata without
        // rejecting otherwise valid safety zones.
        break;
    }
  }

  private static HongKongNoFlyZone ParsePolygon(
      JsonElement coordinates,
      string name,
      string description,
      ref int coordinateCount) {
    if (coordinates.ValueKind != JsonValueKind.Array || coordinates.GetArrayLength() == 0) {
      throw new InvalidDataException("Hong Kong no-fly polygon has no rings.");
    }
    var rings = new List<IReadOnlyList<(double Lat, double Lng)>>();
    foreach (JsonElement ringElement in coordinates.EnumerateArray()) {
      if (ringElement.ValueKind != JsonValueKind.Array) {
        throw new InvalidDataException("Hong Kong no-fly polygon ring is not an array.");
      }
      var ring = new List<(double Lat, double Lng)>();
      foreach (JsonElement point in ringElement.EnumerateArray()) {
        coordinateCount++;
        if (coordinateCount > MaxCoordinates) {
          throw new InvalidDataException(
              $"Hong Kong no-fly response exceeds the {MaxCoordinates}-coordinate safety limit.");
        }
        if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 2) {
          throw new InvalidDataException("Hong Kong no-fly coordinate is not a [longitude, latitude] array.");
        }
        JsonElement.ArrayEnumerator values = point.EnumerateArray();
        values.MoveNext();
        double longitude = ReadFiniteNumber(values.Current, "longitude");
        values.MoveNext();
        double latitude = ReadFiniteNumber(values.Current, "latitude");
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180) {
          throw new InvalidDataException("Hong Kong no-fly coordinate is outside WGS84 bounds.");
        }
        ring.Add((latitude, longitude));
      }
      RemoveDuplicateClosure(ring);
      if (ring.Count < 3 || ring.Distinct().Count() < 3) {
        throw new InvalidDataException("Hong Kong no-fly polygon ring has fewer than three points.");
      }
      rings.Add(ring);
    }
    return new HongKongNoFlyZone(name, description, rings[0], rings.GetRange(1, rings.Count - 1));
  }

  private static void RemoveDuplicateClosure(List<(double Lat, double Lng)> ring) {
    while (ring.Count > 1 && ring[0] == ring[^1]) {
      ring.RemoveAt(ring.Count - 1);
    }
  }

  private static double ReadFiniteNumber(JsonElement value, string name) {
    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double result)
        || !double.IsFinite(result)) {
      throw new InvalidDataException($"Hong Kong no-fly {name} is not a finite number.");
    }
    return result;
  }

  private static string ReadProperty(JsonElement feature, string property, string fallback) {
    if (!feature.TryGetProperty("properties", out JsonElement properties)
        || properties.ValueKind != JsonValueKind.Object
        || !properties.TryGetProperty(property, out JsonElement value)
        || value.ValueKind != JsonValueKind.String
        || string.IsNullOrWhiteSpace(value.GetString())) {
      return fallback;
    }
    string text = value.GetString()!.Replace('\r', ' ').Replace('\n', ' ').Trim();
    return text.Length <= 1_024 ? text : text[..1_024];
  }

  private async Task<byte[]> DownloadAsync(CancellationToken cancellationToken) {
    using var request = new HttpRequestMessage(HttpMethod.Get, OfficialFeed);
    using HttpResponseMessage response = await _http.SendAsync(
        request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    response.EnsureSuccessStatusCode();
    if (response.Content.Headers.ContentLength is long declared && declared > MaxResponseBytes) {
      throw new InvalidDataException(
          $"Hong Kong no-fly response exceeds the {MaxResponseBytes}-byte safety limit.");
    }
    await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken)
        .ConfigureAwait(false);
    using var destination = new MemoryStream();
    var buffer = new byte[32 * 1024];
    while (true) {
      int count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
      if (count == 0) {
        break;
      }
      if (destination.Length + count > MaxResponseBytes) {
        throw new InvalidDataException(
            $"Hong Kong no-fly response exceeds the {MaxResponseBytes}-byte safety limit.");
      }
      await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken)
          .ConfigureAwait(false);
    }
    return destination.ToArray();
  }

  private static async Task<byte[]> ReadBoundedFileAsync(
      string path, CancellationToken cancellationToken) {
    var info = new FileInfo(path);
    if (info.Length <= 0 || info.Length > MaxResponseBytes) {
      throw new InvalidDataException("Hong Kong no-fly cache has an invalid size.");
    }
    byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    if (bytes.Length == 0 || bytes.Length > MaxResponseBytes) {
      throw new InvalidDataException("Hong Kong no-fly cache has an invalid size.");
    }
    return bytes;
  }

  private async Task WriteCacheAtomicAsync(byte[] bytes, CancellationToken cancellationToken) {
    string? directory = Path.GetDirectoryName(_cachePath);
    if (directory == null) {
      throw new InvalidOperationException("Hong Kong no-fly cache has no parent directory.");
    }
    Directory.CreateDirectory(directory);
    string temporary = _cachePath + ".partial-" + Guid.NewGuid().ToString("N");
    try {
      await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
      cancellationToken.ThrowIfCancellationRequested();
      File.Move(temporary, _cachePath, overwrite: true);
    } finally {
      try {
        if (File.Exists(temporary)) {
          File.Delete(temporary);
        }
      } catch {
      }
    }
  }

  private static HttpClient CreateSharedHttp() {
    var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("MissionPlannerAvalonia/HongKongNoFly");
    return http;
  }
}

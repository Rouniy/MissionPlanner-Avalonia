using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using DotSpatial.Projections;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace MissionPlannerAvalonia.Services;

internal static class GeoPackageOverlayReader {
  private static readonly ProjectionInfo _wgs84 =
      KnownCoordinateSystems.Geographic.World.WGS1984;

  internal static ImportedMapOverlay Read(string path) {
    if (!File.Exists(path)) {
      throw new FileNotFoundException("GeoPackage not found.", path);
    }

    var connectionString = new SqliteConnectionStringBuilder {
      DataSource = Path.GetFullPath(path),
      Mode = SqliteOpenMode.ReadOnly,
      Cache = SqliteCacheMode.Private,
      Pooling = false,
    }.ToString();
    using var connection = new SqliteConnection(connectionString);
    connection.Open();

    IReadOnlyList<GeoPackageLayer> layers = ReadLayers(connection);
    if (layers.Count == 0) {
      return ImportedMapOverlay.Empty;
    }

    var routes = new List<ImportedOverlayRoute>();
    var markers = new List<ImportedOverlayMarker>();
    var projections = new Dictionary<int, ProjectionInfo?>();
    foreach (GeoPackageLayer layer in layers) {
      projections[layer.SrsId] = ResolveProjection(layer);
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = $"SELECT {Quote(layer.GeometryColumn)} FROM {Quote(layer.Table)} " +
                            $"WHERE {Quote(layer.GeometryColumn)} IS NOT NULL";
      using SqliteDataReader reader = command.ExecuteReader();
      int feature = 0;
      while (reader.Read()) {
        feature++;
        byte[] blob = (byte[])reader.GetValue(0);
        GeoPackageGeometry item;
        try {
          item = ReadGeometry(blob);
        } catch (Exception ex) when (ex is InvalidDataException or ArgumentException) {
          throw new InvalidDataException(
              $"Invalid geometry in GeoPackage layer '{layer.Table}', feature {feature}: {ex.Message}", ex);
        }
        if (item.Empty) {
          continue;
        }

        if (!projections.TryGetValue(item.SrsId, out ProjectionInfo? projection)) {
          GeoPackageLayer srs = ReadSrs(connection, item.SrsId);
          projection = ResolveProjection(srs);
          projections[item.SrsId] = projection;
        }
        ShapefileImportService.AddGeometry(
            item.Geometry,
            $"{layer.Table} {feature}",
            projection,
            routes,
            markers);
      }
    }
    return new ImportedMapOverlay(routes, markers);
  }

  internal static GeoPackageGeometry ReadGeometry(byte[] blob) {
    ArgumentNullException.ThrowIfNull(blob);
    if (blob.Length < 8 || blob[0] != (byte)'G' || blob[1] != (byte)'P') {
      throw new InvalidDataException("GeoPackage geometry header is missing the GP magic bytes.");
    }
    if (blob[2] != 0) {
      throw new InvalidDataException($"Unsupported GeoPackage geometry version {blob[2]}.");
    }

    byte flags = blob[3];
    if ((flags & 0xc0) != 0) {
      throw new InvalidDataException("GeoPackage geometry uses non-zero reserved flag bits.");
    }
    if ((flags & 0x20) != 0) {
      throw new InvalidDataException("Extended GeoPackage geometry is not supported.");
    }
    bool littleEndian = (flags & 0x01) != 0;
    bool empty = (flags & 0x10) != 0;
    int envelopeIndicator = (flags >> 1) & 0x07;
    int envelopeValues = envelopeIndicator switch {
      0 => 0,
      1 => 4,
      2 or 3 => 6,
      4 => 8,
      _ => throw new InvalidDataException(
          $"Invalid GeoPackage envelope indicator {envelopeIndicator}."),
    };
    if (empty && envelopeIndicator != 0) {
      throw new InvalidDataException("An empty GeoPackage geometry cannot contain an envelope.");
    }

    int headerLength = checked(8 + envelopeValues * sizeof(double));
    if (blob.Length <= headerLength) {
      throw new InvalidDataException("GeoPackage geometry is truncated before its WKB payload.");
    }
    int srsId = littleEndian
        ? BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(4, 4))
        : BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(4, 4));
    if (empty) {
      return new GeoPackageGeometry(
          new GeometryFactory().CreateGeometryCollection(), srsId, Empty: true);
    }

    Geometry geometry;
    try {
      geometry = new WKBReader().Read(blob.AsSpan(headerLength).ToArray());
    } catch (Exception ex) {
      throw new InvalidDataException("GeoPackage WKB payload could not be decoded.", ex);
    }
    geometry.SRID = srsId;
    return new GeoPackageGeometry(geometry, srsId, geometry.IsEmpty);
  }

  private static IReadOnlyList<GeoPackageLayer> ReadLayers(SqliteConnection connection) {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
        SELECT gc.table_name, gc.column_name, gc.srs_id,
               srs.organization, srs.organization_coordsys_id, srs.definition
        FROM gpkg_geometry_columns AS gc
        JOIN gpkg_contents AS contents
          ON contents.table_name = gc.table_name
         AND lower(contents.data_type) = 'features'
        LEFT JOIN gpkg_spatial_ref_sys AS srs ON srs.srs_id = gc.srs_id
        ORDER BY gc.table_name, gc.column_name
        """;
    try {
      using SqliteDataReader reader = command.ExecuteReader();
      var result = new List<GeoPackageLayer>();
      while (reader.Read()) {
        result.Add(ReadLayer(reader));
      }
      return result;
    } catch (SqliteException ex) {
      throw new InvalidDataException(
          "The file is not a valid feature GeoPackage (required metadata tables are missing).", ex);
    }
  }

  private static GeoPackageLayer ReadSrs(SqliteConnection connection, int srsId) {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
        SELECT '', '', srs_id, organization, organization_coordsys_id, definition
        FROM gpkg_spatial_ref_sys WHERE srs_id = $id
        """;
    command.Parameters.AddWithValue("$id", srsId);
    using SqliteDataReader reader = command.ExecuteReader();
    if (!reader.Read()) {
      throw new InvalidDataException($"GeoPackage SRS {srsId} is not declared.");
    }
    return ReadLayer(reader);
  }

  private static GeoPackageLayer ReadLayer(SqliteDataReader reader) => new(
      reader.GetString(0),
      reader.GetString(1),
      reader.GetInt32(2),
      reader.IsDBNull(3) ? null : reader.GetString(3),
      reader.IsDBNull(4) ? null : reader.GetInt32(4),
      reader.IsDBNull(5) ? null : reader.GetString(5));

  private static ProjectionInfo? ResolveProjection(GeoPackageLayer layer) {
    int epsg = string.Equals(layer.Organization, "EPSG", StringComparison.OrdinalIgnoreCase)
        ? layer.OrganizationCoordinateSystemId ?? layer.SrsId
        : layer.SrsId;
    if (epsg == 4326 || layer.SrsId is 0 or -1) {
      return null;
    }
    try {
      return ProjectionInfo.FromEpsgCode(epsg);
    } catch {
      // Fall through to the WKT definition stored by the GeoPackage.
    }

    if (!string.IsNullOrWhiteSpace(layer.Definition) &&
        !string.Equals(layer.Definition, "undefined", StringComparison.OrdinalIgnoreCase)) {
      try {
        var projection = new ProjectionInfo();
        projection.ParseEsriString(layer.Definition);
        // Verify that the parsed definition can actually transform a point.
        double[] xy = { 0, 0 };
        Reproject.ReprojectPoints(xy, null, projection, _wgs84, 0, 1);
        return projection;
      } catch {
      }
    }
    throw new NotSupportedException(
        $"GeoPackage SRS {layer.SrsId} cannot be transformed to WGS84 safely.");
  }

  private static string Quote(string identifier) =>
      '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

  private sealed record GeoPackageLayer(
      string Table,
      string GeometryColumn,
      int SrsId,
      string? Organization,
      int? OrganizationCoordinateSystemId,
      string? Definition);
}

internal sealed record GeoPackageGeometry(Geometry Geometry, int SrsId, bool Empty);

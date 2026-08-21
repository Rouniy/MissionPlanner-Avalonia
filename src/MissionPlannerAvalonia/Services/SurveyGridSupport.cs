using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jpeg;
using MissionPlanner.Grid;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

internal sealed record SurveyCameraProfile(
    string Name,
    float FocalLength,
    float SensorWidth,
    float SensorHeight,
    float ImageWidth,
    float ImageHeight);

internal sealed record SurveyPhotoMetadata(int Width, int Height, double? FocalLength);

internal readonly record struct SurveyTerrainStats(double Minimum, double Maximum, int SampleCount) {
  internal static SurveyTerrainStats Empty => new(double.NaN, double.NaN, 0);
}

internal static class SurveyGridSupport {
  private static readonly CultureInfo _cameraCulture = CultureInfo.GetCultureInfo("en-US");

  internal static Dictionary<string, SurveyCameraProfile> ReadCameraFiles(
      params string[] filenames) {
    var result = new Dictionary<string, SurveyCameraProfile>(StringComparer.Ordinal);
    foreach (string filename in filenames) {
      foreach (var profile in ReadCameraFile(filename)) {
        result[profile.Name] = profile;
      }
    }
    return result;
  }

  internal static IReadOnlyList<SurveyCameraProfile> ReadCameraFile(string filename) {
    var result = new List<SurveyCameraProfile>();
    if (!File.Exists(filename)) {
      return result;
    }

    try {
      var document = new XmlDocument();
      var readerSettings = new XmlReaderSettings {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
      };
      using (var reader = XmlReader.Create(filename, readerSettings)) {
        document.Load(reader);
      }
      var cameraNodes = document.SelectNodes("/Cameras/Camera");
      if (cameraNodes == null) {
        return result;
      }
      foreach (XmlNode node in cameraNodes) {
        string name = node["name"]?.InnerText.Trim() ?? "";
        if (name.Length == 0 ||
            !TryFloat(node, "flen", out float focalLength) ||
            !TryFloat(node, "senw", out float sensorWidth) ||
            !TryFloat(node, "senh", out float sensorHeight) ||
            !TryFloat(node, "imgw", out float imageWidth) ||
            !TryFloat(node, "imgh", out float imageHeight)) {
          continue;
        }
        result.Add(new SurveyCameraProfile(name, focalLength, sensorWidth, sensorHeight,
            imageWidth, imageHeight));
      }
    } catch (XmlException) {
      // Match Mission Planner's soft failure for a damaged optional camera file.
    } catch (IOException) {
    } catch (UnauthorizedAccessException) {
    }
    return result;
  }

  internal static void WriteCameraFile(string filename,
      IEnumerable<SurveyCameraProfile> profiles) {
    string? directory = Path.GetDirectoryName(filename);
    if (!string.IsNullOrEmpty(directory)) {
      System.IO.Directory.CreateDirectory(directory);
    }

    var settings = new XmlWriterSettings {
      Encoding = Encoding.ASCII,
      Indent = true,
    };
    using var writer = XmlWriter.Create(filename, settings);
    writer.WriteStartDocument();
    writer.WriteStartElement("Cameras");
    foreach (var profile in profiles.OrderBy(profile => profile.Name, StringComparer.Ordinal)) {
      if (string.IsNullOrWhiteSpace(profile.Name)) {
        continue;
      }
      writer.WriteStartElement("Camera");
      writer.WriteElementString("name", profile.Name);
      writer.WriteElementString("flen", profile.FocalLength.ToString(_cameraCulture));
      writer.WriteElementString("imgh", profile.ImageHeight.ToString(_cameraCulture));
      writer.WriteElementString("imgw", profile.ImageWidth.ToString(_cameraCulture));
      writer.WriteElementString("senh", profile.SensorHeight.ToString(_cameraCulture));
      writer.WriteElementString("senw", profile.SensorWidth.ToString(_cameraCulture));
      writer.WriteEndElement();
    }
    writer.WriteEndElement();
    writer.WriteEndDocument();
  }

  internal static SurveyPhotoMetadata ReadSamplePhoto(string filename) {
    var directories = ImageMetadataReader.ReadMetadata(filename);
    int width = 0;
    int height = 0;
    double? focalLength = null;

    foreach (var directory in directories) {
      if (directory is ExifSubIfdDirectory exif) {
        if (exif.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out int exifWidth)) {
          width = exifWidth;
        }
        if (exif.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out int exifHeight)) {
          height = exifHeight;
        }
        if (exif.TryGetDouble(ExifDirectoryBase.TagFocalLength, out double focal) && focal > 0) {
          focalLength = focal;
        }
      } else if (directory is JpegDirectory jpeg) {
        // Upstream visits the JPEG directory after EXIF and therefore lets the physical
        // frame dimensions be the final fallback/authority.
        if (jpeg.TryGetInt32(JpegDirectory.TagImageWidth, out int jpegWidth)) {
          width = jpegWidth;
        }
        if (jpeg.TryGetInt32(JpegDirectory.TagImageHeight, out int jpegHeight)) {
          height = jpegHeight;
        }
      }
    }

    if (width <= 0 || height <= 0) {
      throw new InvalidDataException("The selected JPEG does not contain valid image dimensions.");
    }
    return new SurveyPhotoMetadata(width, height, focalLength);
  }

  internal static SurveyTerrainStats ComputeTerrainStats(IReadOnlyList<PointLatLngAlt> grid,
      Func<PointLatLngAlt, double?> elevationProvider) {
    double minimum = double.PositiveInfinity;
    double maximum = double.NegativeInfinity;
    int count = 0;
    foreach (var point in grid) {
      double? elevation = elevationProvider(point);
      if (!elevation.HasValue || !double.IsFinite(elevation.Value)) {
        continue;
      }
      minimum = Math.Min(minimum, elevation.Value);
      maximum = Math.Max(maximum, elevation.Value);
      count++;
    }
    return count == 0 ? SurveyTerrainStats.Empty : new SurveyTerrainStats(minimum, maximum, count);
  }

  internal static void SaveGrid(string filename, GridData data) {
    using var stream = File.Create(filename);
    new XmlSerializer(typeof(GridData)).Serialize(stream, data);
  }

  internal static GridData LoadGrid(string filename) {
    using var stream = File.OpenRead(filename);
    return (GridData)(new XmlSerializer(typeof(GridData)).Deserialize(stream)
                      ?? throw new InvalidDataException("The grid file is empty."));
  }

  private static bool TryFloat(XmlNode node, string element, out float value) =>
      float.TryParse(node[element]?.InnerText, NumberStyles.Float, _cameraCulture, out value);
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

/// <summary>
/// XML contract used by the official FaceMap plug-in. Field names deliberately remain lower-case
/// so files can be exchanged in both directions without a conversion step.
/// </summary>
[XmlRoot("FaceMapData")]
public sealed class FaceMapFileData {
  public List<PointLatLngAlt> poly = new();
  public string camera = "";
  public decimal benchheight;
  public decimal angle;
  public bool facedirection;
  public decimal speed;
  public bool usespeed;
  public bool autotakeoff;
  public bool autotakeoff_RTL;
  public bool extraimages;
  public decimal height_test;
  public decimal toepoint_runs;
  public decimal splitmission;
  public decimal bermdepth;
  public decimal numbenches;
  public decimal camerapitch;
  public decimal toeheight;
  public bool campitchunlock;
  public decimal dist;
  public string startfrom = "";
  public decimal overlap;
  public decimal sidelap;
  public decimal spacing;
  public bool crossgrid;
  public decimal copter_delay;
  public bool trigdist;
  public bool digicam;
  public bool repeatservo;
  public bool breaktrigdist;
  public decimal repeatservo_no;
  public decimal repeatservo_pwm;
  public decimal repeatservo_cycle;
  public decimal setservo_no;
  public decimal setservo_low;
  public decimal setservo_high;

  // Native extension fields are ignored by old Mission Planner builds, while retaining options
  // that the official serializer accidentally omitted from SaveFaceMapData.
  public bool followpathhome = true;
  public decimal radialpitchoffset;
}

internal static class FaceMapSupport {
  private const long _maximumFileBytes = 8 * 1024 * 1024;
  private static readonly XmlSerializer _serializer = new(typeof(FaceMapFileData));

  internal static FaceMapFileData Load(string filename) {
    var info = new FileInfo(filename);
    if (!info.Exists) {
      throw new FileNotFoundException("The Face Map file does not exist.", filename);
    }
    if (info.Length > _maximumFileBytes) {
      throw new InvalidDataException("The Face Map file is larger than 8 MiB.");
    }

    using FileStream stream = File.OpenRead(filename);
    var settings = new XmlReaderSettings {
      DtdProcessing = DtdProcessing.Prohibit,
      XmlResolver = null,
      MaxCharactersInDocument = _maximumFileBytes * 4,
      IgnoreComments = true,
    };
    using XmlReader reader = XmlReader.Create(stream, settings);
    var value = (FaceMapFileData?)_serializer.Deserialize(reader)
                ?? throw new InvalidDataException("The Face Map file is empty.");
    value.poly ??= new List<PointLatLngAlt>();
    value.camera ??= "";
    value.startfrom ??= "";
    return value;
  }

  internal static void Save(string filename, FaceMapFileData data) {
    ArgumentNullException.ThrowIfNull(data);
    string destination = Path.GetFullPath(filename);
    string directory = Path.GetDirectoryName(destination)
                       ?? throw new InvalidOperationException("Face Map path has no directory.");
    Directory.CreateDirectory(directory);
    string temporary = Path.Combine(directory,
        $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
    var settings = new XmlWriterSettings {
      Indent = true,
      Encoding = new System.Text.UTF8Encoding(false),
    };
    try {
      using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write,
                 FileShare.None)) {
        using (XmlWriter writer = XmlWriter.Create(stream, settings)) {
          _serializer.Serialize(writer, data);
        }
        stream.Flush(true);
      }
      File.Move(temporary, destination, overwrite: true);
    } finally {
      try {
        File.Delete(temporary);
      } catch {
        // A completed destination is preferable to turning temporary-file cleanup into a failure.
      }
    }
  }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MissionPlanner.Utilities;

using Directory = System.IO.Directory;

namespace MissionPlannerAvalonia.ViewModels;

public partial class GeoRefViewModel : ViewModelBase {

  public sealed class GeoTagResult {
    public string Photo { get; init; } = "";
    public double Lat { get; init; }
    public double Lng { get; init; }
    public double Alt { get; init; }
    public string MatchedTime { get; init; } = "";
  }

  private sealed class VehicleLoc {
    public DateTime Time;
    public double Lat;
    public double Lon;
    public double AltAMSL;
    public double RelAlt;
    public double GPSAlt;
    public float Roll;
    public float Pitch;
    public float Yaw;
  }

  [ObservableProperty] private string _logPath = "";
  [ObservableProperty] private string _photoDir = "";

  [ObservableProperty] private double _timeOffsetSeconds;

  [ObservableProperty] private bool _useCamMessages = true;

  [ObservableProperty] private bool _useTrigMessages;

  [ObservableProperty] private bool _useGps2;
  [ObservableProperty] private int _shutterLagMilliseconds;
  [ObservableProperty] private bool _useAmslAltitude;
  [ObservableProperty] private bool _useGpsAltitude;
  [ObservableProperty] private double _baseAltitudeAdjustmentMeters;
  [ObservableProperty] private bool _busy;
  [ObservableProperty] private string _status = "Pick a log and a photo folder, then Geo Tag.";
  [ObservableProperty] private string _outputLog = "";

  public ObservableCollection<GeoTagResult> Results { get; } = new();

  private string GpsMsg => UseGps2 ? "GPS2" : "GPS";

  private void Append(string text) {
    var line = text.EndsWith("\n") ? text : text + "\n";
    if (Dispatcher.UIThread.CheckAccess()) {
      OutputLog += line;
    } else {
      Dispatcher.UIThread.Post(() => OutputLog += line);
    }
  }

  public async Task GeoTagAsync() {
    if (!File.Exists(LogPath)) {
      Status = "Log file not found.";
      return;
    }
    if (!Directory.Exists(PhotoDir)) {
      Status = "Photo directory not found.";
      return;
    }
    if (!double.IsFinite(BaseAltitudeAdjustmentMeters)) {
      Status = "Altitude adjustment must be a finite number of metres.";
      return;
    }

    Busy = true;
    OutputLog = "";
    Status = "Geo tagging…";
    var results = new List<GeoTagResult>();
    int tagged = 0;
    int failed = 0;
    double altitudeAdjustment = BaseAltitudeAdjustmentMeters;

    try {
      await Task.Run(() => {
        var pics = UseCamMessages
            ? DoWorkCam()
            : UseTrigMessages
                ? DoWorkTrig()
                : DoWorkGpsOffset();
        if ((UseCamMessages || UseTrigMessages) && pics.Count > 0) {
          ApplyCameraCorrections(pics);
        }
        if (pics.Count == 0) {
          Append("No valid matches. Aborting.");
          return;
        }
        ApplyBaseAltitudeAdjustment(pics, altitudeAdjustment);

        WriteReports(pics);

        // GeoRefImageBase swallows its own exceptions, so success is judged by whether the
        // geotagged copy actually appeared on disk. A stale copy from a previous run must not
        // count, so it is removed before each write.
        var georef = new MissionPlanner.GeoRef.GeoRefImageBase();
        foreach (var p in pics) {
          var output = Path.Combine(PhotoDir, "geotagged",
              Path.GetFileNameWithoutExtension(p.Path) + "_geotag" + Path.GetExtension(p.Path));
          bool staleBlocked = false;
          try {
            if (File.Exists(output)) {
              File.Delete(output);
            }
          } catch (Exception ex) {
            staleBlocked = true;
            Append($"Could not remove stale {Path.GetFileName(output)}: {ex.Message}\n");
          }
          try {
            georef.WriteCoordinatesToImage(p.Path, p.Lat, p.Lon, p.AltAMSL, PhotoDir, Append);
          } catch (Exception ex) {
            Append($"EXIF write failed for {Path.GetFileName(p.Path)}: {ex.Message}\n");
          }
          if (!staleBlocked && File.Exists(output)) {
            tagged++;
          } else {
            failed++;
            Append(staleBlocked
                ? $"Cannot verify {Path.GetFileName(p.Path)}: a stale geotagged copy is in " +
                  "the way and could not be removed.\n"
                : $"No geotagged copy was produced for {Path.GetFileName(p.Path)}.\n");
          }
          results.Add(new GeoTagResult {
            Photo = Path.GetFileName(p.Path),
            Lat = p.Lat,
            Lng = p.Lon,
            Alt = p.AltAMSL,
            MatchedTime = p.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
          });
        }

        Append(failed == 0
            ? $"GPS EXIF written for all {tagged} photo(s) in the 'geotagged' folder, " +
              "plus location.txt and location.kml.\n"
            : $"GPS EXIF written for {tagged} photo(s); {failed} failed — see messages above. " +
              "location.txt and location.kml were still written.\n");
      });

      Results.Clear();
      foreach (var r in results) {
        Results.Add(r);
      }
      if (results.Count == 0) {
        Status = "Geo tagging found no photo/log matches — nothing was tagged.";
      } else if (failed == 0) {
        Status = $"Geo tagging done — {results.Count} photos matched, {tagged} tagged. " +
                 "Reports under 'geotagged'.";
      } else {
        Status = $"Geo tagging finished with errors — {tagged} tagged, {failed} failed of " +
                 $"{results.Count} matched. See the log above.";
      }
    } catch (Exception ex) {
      Append("Error: " + ex);
      Status = "Geo tagging failed: " + ex.Message;
    } finally {
      Busy = false;
    }
  }

  public async Task EstimateOffsetAsync() {
    if (!File.Exists(LogPath)) {
      Status = "Log file not found.";
      return;
    }
    if (!Directory.Exists(PhotoDir)) {
      Status = "Photo directory not found.";
      return;
    }

    Busy = true;
    OutputLog = "";
    Status = "Estimating camera/log time offset…";
    try {
      double? estimate = await Task.Run(() => {
        var photos = ListPhotos().Select(GetPhotoTime)
            .Where(time => time != DateTime.MinValue)
            .Select(time => time.ToUniversalTime()).OrderBy(time => time).ToList();
        var locations = UseCamMessages
            ? ReadCamMsgInLog(LogPath)
            : ReadGpsMsgInLog(LogPath, GpsMsg);
        var logTimes = locations.Values.Select(location => location.Time.ToUniversalTime())
            .Where(time => time != DateTime.MinValue).OrderBy(time => time).ToList();
        return EstimateOffset(photos, logTimes);
      });
      if (!estimate.HasValue) {
        Status = "Could not estimate an offset: valid photo or log timestamps are missing.";
        return;
      }
      TimeOffsetSeconds = Math.Round(estimate.Value, 3);
      Append($"Estimated camera minus log offset: {TimeOffsetSeconds:0.000} s");
      Status = $"Estimated offset: {TimeOffsetSeconds:0.000} s. Review it, then Geo Tag.";
    } catch (Exception ex) {
      Status = "Offset estimation failed: " + ex.Message;
    } finally {
      Busy = false;
    }
  }

  internal static double? EstimateOffset(
      IReadOnlyList<DateTime> photoTimes, IReadOnlyList<DateTime> logTimes) {
    int count = Math.Min(photoTimes.Count, logTimes.Count);
    if (count == 0) {
      return null;
    }
    var indices = Enumerable.Range(0, Math.Min(4, count))
        .Concat(Enumerable.Range(Math.Max(0, count - 3), Math.Min(3, count)))
        .Distinct().OrderBy(index => index);
    var offsets = indices.Select(index =>
        (photoTimes[index].ToUniversalTime() - logTimes[index].ToUniversalTime()).TotalSeconds)
        .OrderBy(value => value).ToArray();
    int middle = offsets.Length / 2;
    return offsets.Length % 2 == 1
        ? offsets[middle]
        : (offsets[middle - 1] + offsets[middle]) / 2.0;
  }

  private sealed class PictureInfo {
    public string Path = "";
    public DateTime Time;
    public double Lat;
    public double Lon;
    public double AltAMSL;
    public double RelAlt;
    public double GPSAlt;
    public float Roll;
    public float Pitch;
    public float Yaw;
  }

  private List<string> ListPhotos() {
    var files = new List<string>();
    foreach (var pat in new[] { "*.jpg", "*.jpeg", "*.tif", "*.tiff" }) {
      files.AddRange(Directory.GetFiles(PhotoDir, pat));
    }

    return files.Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(f => GetPhotoTime(f)).ThenBy(f => f, StringComparer.Ordinal).ToList();
  }

  private readonly Dictionary<string, DateTime> _photoTimeCache = new();

  private DateTime GetPhotoTime(string fn) {
    if (_photoTimeCache.TryGetValue(fn, out var cached)) {
      return cached;
    }
    var dt = DateTime.MinValue;
    try {
      var dirs = ImageMetadataReader.ReadMetadata(fn);
      var exif = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
      if (exif != null) {
        if (exif.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var t) ||
            exif.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out t)) {
          dt = t;
        }
      }
    } catch {

    }
    _photoTimeCache[fn] = dt;
    return dt;
  }

  private List<PictureInfo> DoWorkCam() {
    Append("Reading log for CAM messages…");
    var cam = ReadCamMsgInLog(LogPath);
    Append($"CAM messages found: {cam.Count}");

    var files = ListPhotos();
    Append($"Photos found: {files.Count}");

    if (files.Count != cam.Count) {
      Append($"WARNING: CAM/photo count mismatch — photos: {files.Count} vs CAM: {cam.Count}");
    }

    var camList = cam.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
    var outp = new List<PictureInfo>();
    for (int i = 0; i < files.Count && i < camList.Count; i++) {
      var c = camList[i];
      outp.Add(new PictureInfo {
        Path = files[i],
        Time = c.Time,
        Lat = c.Lat,
        Lon = c.Lon,
        AltAMSL = c.AltAMSL,
        RelAlt = c.RelAlt,
        GPSAlt = c.GPSAlt,
        Roll = c.Roll,
        Pitch = c.Pitch,
        Yaw = c.Yaw,
      });
      Append($"Photo {Path.GetFileNameWithoutExtension(files[i])} matched to CAM msg.");
    }
    return outp;
  }

  private List<PictureInfo> DoWorkTrig() {
    Append("Reading log for TRIG messages…");
    var triggers = ReadTrigMsgInLog(LogPath);
    Append($"TRIG messages found: {triggers.Count}");

    var files = ListPhotos();
    Append($"Photos found: {files.Count}");
    if (files.Count != triggers.Count) {
      Append($"TRIG/photo count mismatch — photos: {files.Count} vs TRIG: {triggers.Count}. " +
             "The order-only TRIG mode cannot safely guess missing captures, so it stopped.");
      return new List<PictureInfo>();
    }

    var triggerList = triggers.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
    var output = new List<PictureInfo>(files.Count);
    for (int i = 0; i < files.Count; i++) {
      var trigger = triggerList[i];
      output.Add(new PictureInfo {
        Path = files[i],
        Time = trigger.Time,
        Lat = trigger.Lat,
        Lon = trigger.Lon,
        AltAMSL = trigger.AltAMSL,
        RelAlt = trigger.RelAlt,
        GPSAlt = trigger.GPSAlt,
        Roll = trigger.Roll,
        Pitch = trigger.Pitch,
        Yaw = trigger.Yaw,
      });
      Append($"Photo {Path.GetFileNameWithoutExtension(files[i])} matched to TRIG msg.");
    }
    return output;
  }

  private List<PictureInfo> DoWorkGpsOffset() {
    Append($"Reading log for {GpsMsg} messages…");
    var gps = ReadGpsMsgInLog(LogPath, GpsMsg);
    Append($"{GpsMsg} positions found: {gps.Count}");

    var files = ListPhotos();
    Append($"Photos found: {files.Count}");

    var outp = new List<PictureInfo>();
    foreach (var file in files) {
      var shot = GetPhotoTime(file);
      if (shot == DateTime.MinValue) {
        Append($"Photo {Path.GetFileNameWithoutExtension(file)} has no EXIF time — skipped.");
        continue;
      }
      var corrected = shot.AddSeconds(-TimeOffsetSeconds).ToUniversalTime();
      var loc = LookForLocation(corrected, gps, 5000);
      if (loc == null) {
        Append($"Photo {Path.GetFileNameWithoutExtension(file)} — no GPS match in log.");
        continue;
      }
      outp.Add(new PictureInfo {
        Path = file,
        Time = loc.Time,
        Lat = loc.Lat,
        Lon = loc.Lon,
        AltAMSL = loc.AltAMSL,
        RelAlt = loc.RelAlt,
        GPSAlt = loc.GPSAlt,
        Roll = loc.Roll,
        Pitch = loc.Pitch,
        Yaw = loc.Yaw,
      });
      Append($"Photo {Path.GetFileNameWithoutExtension(file)} matched {(loc.Time - corrected).TotalMilliseconds:0} ms away.");
    }
    return outp;
  }

  private void ApplyCameraCorrections(List<PictureInfo> pictures) {
    if (ShutterLagMilliseconds == 0 && !UseAmslAltitude && !UseGpsAltitude) {
      return;
    }
    Append($"Applying shutter lag {ShutterLagMilliseconds} ms; "
           + $"AMSL={UseAmslAltitude}; GPS altitude={UseGpsAltitude}.");
    var gps = ReadGpsMsgInLog(LogPath, GpsMsg);
    int window = Math.Max(5000, Math.Abs(ShutterLagMilliseconds) + 2000);
    foreach (var picture in pictures) {
      DateTime target = picture.Time.AddMilliseconds(ShutterLagMilliseconds);
      var location = LookForLocation(target, gps, window);
      if (location == null) {
        Append($"{Path.GetFileName(picture.Path)}: no {GpsMsg} correction near {target:O}.");
        continue;
      }
      if (ShutterLagMilliseconds != 0) {
        picture.Time = location.Time;
        picture.Lat = location.Lat;
        picture.Lon = location.Lon;
        picture.Roll = location.Roll;
        picture.Pitch = location.Pitch;
        picture.Yaw = location.Yaw;
      }
      if (UseGpsAltitude && location.GPSAlt != 0) {
        picture.AltAMSL = location.GPSAlt;
      } else if (UseAmslAltitude) {
        picture.AltAMSL = location.AltAMSL;
      }
    }
  }

  private static VehicleLoc? LookForLocation(DateTime t, Dictionary<long, VehicleLoc> locs,
      int windowMs) {
    long time = ToMillis(t);
    for (int i = 0; i <= windowMs; i++) {
      if (locs.TryGetValue(time + i, out var a)) {
        return a;
      }
      if (locs.TryGetValue(time - i, out var b)) {
        return b;
      }
    }
    return null;
  }

  private void ApplyBaseAltitudeAdjustment(
      IEnumerable<PictureInfo> pictures, double adjustmentMeters) {
    if (adjustmentMeters == 0) {
      return;
    }
    foreach (var picture in pictures) {
      picture.AltAMSL = AdjustAltitude(picture.AltAMSL, adjustmentMeters);
    }
    Append($"Applied base-altitude adjustment {adjustmentMeters:+0.###;-0.###;0} m.");
  }

  internal static double AdjustAltitude(double altitudeMeters, double adjustmentMeters) =>
      double.IsFinite(altitudeMeters) && double.IsFinite(adjustmentMeters)
          ? altitudeMeters + adjustmentMeters
          : altitudeMeters;

  private static Dictionary<long, VehicleLoc> ReadGpsMsgInLog(string fn, string gpsToUse) {
    var list = new Dictionary<long, VehicleLoc>();
    float roll = 0, pitch = 0, yaw = 0;

    using var sr = new DFLogBuffer(fn);
    foreach (var item in sr.GetEnumeratorType(new[] { gpsToUse, "ATT" })) {
      if (item.msgtype == "ATT") {
        roll = ParseF(item["Roll"], roll);
        pitch = ParseF(item["Pitch"], pitch);
        yaw = ParseF(item["Yaw"], yaw);
        continue;
      }

      var statusRaw = item["Status"];
      if (statusRaw != null && TryD(statusRaw, out var status) && status < 3) {
        continue;
      }
      if (!TryD(item["Lat"], out var lat) || !TryD(item["Lng"], out var lng)) {
        continue;
      }
      if (lat == 0 && lng == 0) {
        continue;
      }
      var loc = new VehicleLoc {
        Time = item.time.ToUniversalTime(),
        Lat = lat,
        Lon = lng,
        Roll = roll,
        Pitch = pitch,
        Yaw = yaw,
      };
      if (TryD(item["Alt"], out var alt)) {
        loc.AltAMSL = alt;
        loc.GPSAlt = alt;
      }
      if (TryD(item["RAlt"], out var ralt) || TryD(item["RelAlt"], out ralt)) {
        loc.RelAlt = ralt;
      }
      long ms = ToMillis(loc.Time);
      if (loc.Time != DateTime.MinValue && !list.ContainsKey(ms)) {
        list[ms] = loc;
      }
    }
    return list;
  }

  private static Dictionary<long, VehicleLoc> ReadCamMsgInLog(string fn) {
    var list = new Dictionary<long, VehicleLoc>();

    using var sr = new DFLogBuffer(fn);
    foreach (var item in sr.GetEnumeratorType(new[] { "CAM" })) {
      var p = new VehicleLoc();

      if (int.TryParse(item["GPSWeek"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var week) &&
          int.TryParse(item["GPSTime"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gtime)) {
        p.Time = GetTimeFromGps(week, gtime);
      } else {
        p.Time = item.time.ToUniversalTime();
      }

      TryD(item["Lat"], out var lat);
      TryD(item["Lng"], out var lng);
      p.Lat = lat;
      p.Lon = lng;
      if (TryD(item["Alt"], out var alt)) {
        p.AltAMSL = alt;
      }
      if (TryD(item["RelAlt"], out var ralt)) {
        p.RelAlt = ralt;
      }
      if (TryD(item["GPSAlt"], out var galt)) {
        p.GPSAlt = galt;
      }
      p.Roll = ParseF(item["Roll"] ?? item["R"], 0);
      p.Pitch = ParseF(item["Pitch"] ?? item["P"], 0);
      p.Yaw = ParseF(item["Yaw"] ?? item["Y"], 0);

      list[ToMillis(p.Time)] = p;
    }
    return list;
  }

  private static Dictionary<long, VehicleLoc> ReadTrigMsgInLog(string fn) {
    var list = new Dictionary<long, VehicleLoc>();

    using var sr = new DFLogBuffer(fn);
    foreach (var item in sr.GetEnumeratorType(new[] { "TRIG" })) {
      if (!TryD(item["Lat"], out var lat) || !TryD(item["Lng"], out var lng) ||
          (lat == 0 && lng == 0)) {
        continue;
      }

      DateTime time;
      if (int.TryParse(item["GPSWeek"], NumberStyles.Integer, CultureInfo.InvariantCulture,
              out var week) &&
          int.TryParse(item["GPSTime"], NumberStyles.Integer, CultureInfo.InvariantCulture,
              out var gpsTime)) {
        time = GetTimeFromGps(week, gpsTime);
      } else {
        time = item.time.ToUniversalTime();
      }
      if (time == DateTime.MinValue) {
        continue;
      }

      var point = new VehicleLoc {
        Time = time,
        Lat = lat,
        Lon = lng,
        Roll = ParseF(item["Roll"] ?? item["R"], 0),
        Pitch = ParseF(item["Pitch"] ?? item["P"], 0),
        Yaw = ParseF(item["Yaw"] ?? item["Y"], 0),
      };
      if (TryD(item["Alt"], out var alt)) {
        point.AltAMSL = alt;
      }
      if (TryD(item["RelAlt"], out var relativeAltitude)) {
        point.RelAlt = relativeAltitude;
      }
      if (TryD(item["GPSAlt"], out var gpsAltitude)) {
        point.GPSAlt = gpsAltitude;
      }
      list[ToMillis(point.Time)] = point;
    }
    return list;
  }

  private void WriteReports(List<PictureInfo> pics) {
    string outDir = Path.Combine(PhotoDir, "geotagged");
    Directory.CreateDirectory(outDir);

    string txtPath = Path.Combine(outDir, "location.txt");
    using (var sw = new StreamWriter(txtPath)) {
      sw.WriteLine("#name latitude/Y longitude/X height/Z yaw pitch roll SAlt");
      foreach (var p in pics) {
        sw.WriteLine(string.Join(" ", new[] {
          Path.GetFileName(p.Path),
          Inv(p.Lat), Inv(p.Lon), Inv(p.AltAMSL),
          Inv(p.Yaw), Inv(p.Pitch), Inv(p.Roll), Inv(0.0),
        }));
      }
    }
    Append("Wrote " + txtPath);

    WriteKml(Path.Combine(outDir, "location.kml"), pics);
    Append("Wrote " + Path.Combine(outDir, "location.kml"));
  }

  private static void WriteKml(string path, List<PictureInfo> pics) {
    var settings = new XmlWriterSettings { Indent = true };
    using var w = XmlWriter.Create(path, settings);
    w.WriteStartDocument();
    w.WriteStartElement("kml", "http://www.opengis.net/kml/2.2");
    w.WriteStartElement("Document");
    w.WriteElementString("name", "GeoRef");

    var coords = new StringBuilder();
    foreach (var p in pics) {
      w.WriteStartElement("Placemark");
      w.WriteElementString("name", Path.GetFileNameWithoutExtension(p.Path));
      w.WriteStartElement("TimeStamp");
      w.WriteElementString("when", p.Time.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
      w.WriteEndElement();
      w.WriteStartElement("Point");
      w.WriteElementString("altitudeMode", "absolute");
      w.WriteElementString("coordinates",
          $"{Inv(p.Lon)},{Inv(p.Lat)},{Inv(p.AltAMSL)}");
      w.WriteEndElement();
      w.WriteEndElement();

      coords.Append($"{Inv(p.Lon)},{Inv(p.Lat)},{Inv(p.AltAMSL)} ");
    }

    w.WriteStartElement("Placemark");
    w.WriteElementString("name", "path");
    w.WriteStartElement("LineString");
    w.WriteElementString("altitudeMode", "absolute");
    w.WriteElementString("coordinates", coords.ToString().Trim());
    w.WriteEndElement();
    w.WriteEndElement();

    w.WriteEndElement();
    w.WriteEndElement();
    w.WriteEndDocument();
  }

  private static string Inv(double v) => v.ToString(CultureInfo.InvariantCulture);

  private static bool TryD(string? s, out double v) =>
      double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v);

  private static float ParseF(string? s, float fallback) =>
      float.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;

  private static long ToMillis(DateTime date) {
    var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    return Convert.ToInt64((date.ToUniversalTime() - epoch).TotalMilliseconds);
  }

  private static DateTime GetTimeFromGps(int week, int milliseconds) {
    const int leapSeconds = 17;
    var datum = new DateTime(1980, 1, 6, 0, 0, 0, DateTimeKind.Utc);
    return datum.AddDays(week * 7).AddMilliseconds(milliseconds).AddSeconds(-leapSeconds);
  }
}

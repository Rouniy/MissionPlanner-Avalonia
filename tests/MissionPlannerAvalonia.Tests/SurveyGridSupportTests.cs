using System.Globalization;
using MissionPlanner.Grid;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Tests;

public class SurveyGridSupportTests {
  [Fact]
  public void Camera_profiles_use_upstream_xml_and_user_file_overrides_builtins() {
    string root = MakeTempDirectory();
    try {
      string builtins = Path.Combine(root, "builtin.xml");
      string user = Path.Combine(root, "user.xml");
      File.WriteAllText(builtins, CameraXml("Same", "5.5", "6.1"));
      File.WriteAllText(user, CameraXml("Same", "8.25", "7.2"));
      var previous = CultureInfo.CurrentCulture;
      CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
      try {
        var profiles = SurveyGridSupport.ReadCameraFiles(builtins, user);
        Assert.Equal(8.25f, profiles["Same"].FocalLength);
        Assert.Equal(7.2f, profiles["Same"].SensorWidth);

        string roundTrip = Path.Combine(root, "roundtrip.xml");
        SurveyGridSupport.WriteCameraFile(roundTrip, profiles.Values);
        var loaded = Assert.Single(SurveyGridSupport.ReadCameraFile(roundTrip));
        Assert.Equal(8.25f, loaded.FocalLength);
        Assert.Contains("<flen>8.25</flen>", File.ReadAllText(roundTrip));
      } finally {
        CultureInfo.CurrentCulture = previous;
      }
    } finally {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public void Sample_jpeg_uses_physical_frame_dimensions_when_exif_is_absent() {
    string root = MakeTempDirectory();
    try {
      string jpeg = Path.Combine(root, "sample.jpg");
      File.WriteAllBytes(jpeg, MinimalJpeg(width: 3, height: 2));

      var metadata = SurveyGridSupport.ReadSamplePhoto(jpeg);

      Assert.Equal(3, metadata.Width);
      Assert.Equal(2, metadata.Height);
      Assert.Null(metadata.FocalLength);
    } finally {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public void Terrain_stats_ignore_missing_samples_and_report_real_range() {
    var points = new[] {
      new PointLatLngAlt(1, 1),
      new PointLatLngAlt(2, 2),
      new PointLatLngAlt(3, 3),
      new PointLatLngAlt(4, 4),
    };

    var stats = SurveyGridSupport.ComputeTerrainStats(points, point => point.Lat switch {
      1 => 123.4,
      2 => null,
      3 => double.NaN,
      _ => 98.7,
    });

    Assert.Equal(2, stats.SampleCount);
    Assert.Equal(98.7, stats.Minimum);
    Assert.Equal(123.4, stats.Maximum);
  }

  [Fact]
  public void Grid_file_round_trips_the_official_griddata_contract() {
    string root = MakeTempDirectory();
    try {
      string filename = Path.Combine(root, "survey.grid");
      var data = Data();

      SurveyGridSupport.SaveGrid(filename, data);
      var loaded = SurveyGridSupport.LoadGrid(filename);

      Assert.Equal(2, loaded.splitmission);
      Assert.Equal("Camera A", loaded.camera);
      Assert.Equal(3, loaded.poly.Count);
      Assert.True(loaded.trigdist);
      Assert.Equal(9, loaded.repeatservo_no);
      Assert.Contains("<GridData", File.ReadAllText(filename));
    } finally {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public void View_model_maps_griddata_without_losing_split_and_mission_options() {
    var vm = new GridUIViewModel([], PointLatLngAlt.Zero, _ => 50);

    vm.ApplyGridData(Data());
    var saved = vm.CreateGridData();

    Assert.Equal(2, vm.SplitCount);
    Assert.Equal(SurveyMissionBuilder.TriggerDistance, vm.TriggerMode);
    Assert.Equal(SurveyMissionBuilder.FinishRtl, vm.FinishAction);
    Assert.Equal("50-50 m", vm.GroundElevationText);
    Assert.Equal(2, saved.splitmission);
    Assert.True(saved.autotakeoff_RTL);
    Assert.Equal(3, saved.poly.Count);
  }

  private static GridData Data() => new() {
    poly = new List<PointLatLngAlt> {
      new(1, 1),
      new(1, 1.002),
      new(1.002, 1.002),
    },
    camera = "Camera A",
    alt = 100,
    angle = 45,
    camdir = true,
    speed = 8,
    usespeed = true,
    autotakeoff = true,
    autotakeoff_RTL = true,
    splitmission = 2,
    dist = 40,
    spacing = 25,
    startfrom = Grid.StartPosition.Home.ToString(),
    overlap = 60,
    sidelap = 70,
    leadin = 5,
    leadin2 = 5,
    trigdist = true,
    breaktrigdist = true,
    repeatservo_no = 9,
    repeatservo_pwm = 1800,
    repeatservo_cycle = 1,
    setservo_no = 10,
    setservo_low = 1100,
    setservo_high = 1900,
    copter_delay = 2,
    copter_headinghold_chk = true,
    copter_headinghold = 90,
    copter_spline = false,
    minlaneseparation = 0,
    clockwiseLaps = 1,
    laps = 1,
  };

  private static string CameraXml(string name, string focal, string sensorWidth) =>
      $"""
       <?xml version="1.0" encoding="us-ascii"?>
       <Cameras><Camera><name>{name}</name><flen>{focal}</flen><imgh>3000</imgh>
       <imgw>4000</imgw><senh>4.8</senh><senw>{sensorWidth}</senw></Camera></Cameras>
       """;

  private static byte[] MinimalJpeg(int width, int height) => new byte[] {
    0xFF, 0xD8,
    0xFF, 0xC0, 0x00, 0x11, 0x08,
    (byte)(height >> 8), (byte)height,
    (byte)(width >> 8), (byte)width,
    0x03,
    0x01, 0x11, 0x00,
    0x02, 0x11, 0x00,
    0x03, 0x11, 0x00,
    0xFF, 0xD9,
  };

  private static string MakeTempDirectory() {
    string path = Path.Combine(Path.GetTempPath(), "mpa-survey-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
  }
}

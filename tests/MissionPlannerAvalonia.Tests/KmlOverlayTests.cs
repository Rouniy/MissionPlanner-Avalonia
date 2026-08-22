using System.IO.Compression;
using System.Text;
using MissionPlannerAvalonia.Services;
using SharpKml.Dom;
using SkiaSharp;

namespace MissionPlannerAvalonia.Tests;

public sealed class KmlOverlayTests {
  private const string _styledKml = """
      <?xml version="1.0" encoding="UTF-8"?>
      <kml xmlns="http://www.opengis.net/kml/2.2"><Document>
        <Style id="highlight"><LineStyle><color>7f112233</color><width>4.9</width></LineStyle></Style>
        <Style id="normal"><LineStyle><color>ff0000ff</color><width>9</width></LineStyle></Style>
        <StyleMap id="mapped">
          <Pair><key>highlight</key><styleUrl>#highlight</styleUrl></Pair>
          <Pair><key>normal</key><styleUrl>#normal</styleUrl></Pair>
        </StyleMap>
        <Placemark><name>styled</name><styleUrl>#mapped</styleUrl><LineString>
          <coordinates>30,40,1 31,41,2</coordinates>
        </LineString></Placemark>
        <Placemark><name>direct</name><styleUrl>#highlight</styleUrl><LineString>
          <coordinates>32,42 33,43</coordinates>
        </LineString></Placemark>
        <Placemark><name>inline</name>
          <Style><LineStyle><color>ff00ffff</color><width>7</width></LineStyle></Style>
          <LineString><coordinates>34,44 35,45</coordinates></LineString>
        </Placemark>
        <Placemark><name>polygon</name><styleUrl>#mapped</styleUrl><Polygon>
          <outerBoundaryIs><LinearRing><coordinates>
            30,40 31,40 31,41 30,41 30,40
          </coordinates></LinearRing></outerBoundaryIs>
          <innerBoundaryIs><LinearRing><coordinates>
            30.2,40.2 30.4,40.2 30.4,40.4 30.2,40.2
          </coordinates></LinearRing></innerBoundaryIs>
        </Polygon></Placemark>
        <Placemark><name>multi</name><styleUrl>#mapped</styleUrl><MultiGeometry>
          <Point><coordinates>36,46,3</coordinates></Point>
          <LineString><coordinates>36,46 37,47</coordinates></LineString>
        </MultiGeometry></Placemark>
        <Placemark><name>Camera</name><Point><coordinates>38,48,4</coordinates></Point></Placemark>
      </Document></kml>
      """;

  [Fact]
  public void Kml_overlay_preserves_upstream_stylemap_color_alpha_and_width() {
    ImportedMapOverlay overlay = Parse(_styledKml);

    ImportedOverlayRoute styled = Assert.Single(overlay.Routes,
        route => route.Name == "styled");
    Assert.Equal(new ImportedMapColor(0x33, 0x22, 0x11, 0x7f), styled.Color);
    Assert.Equal(4, styled.Width);
    Assert.False(styled.Closed);
  }

  [Fact]
  public async Task Kml_parsing_is_safe_when_imports_run_in_parallel() {
    Task<int>[] imports = Enumerable.Range(0, 128)
        .Select(_ => Task.Run(() => Parse(_styledKml).Routes.Count))
        .ToArray();

    int[] routeCounts = await Task.WhenAll(imports);

    Assert.All(routeCounts, count => Assert.Equal(5, count));
  }

  [Fact]
  public void Kml_overlay_matches_upstream_direct_and_inline_style_fallbacks() {
    ImportedMapOverlay overlay = Parse(_styledKml);

    foreach (string name in new[] { "direct", "inline" }) {
      ImportedOverlayRoute route = Assert.Single(overlay.Routes,
          candidate => candidate.Name == name);
      Assert.Equal(new ImportedMapColor(255, 255, 255), route.Color);
      Assert.Equal(2, route.Width);
    }
  }

  [Fact]
  public void Kml_overlay_keeps_geometries_separate_and_matches_multi_geometry_labels() {
    ImportedMapOverlay overlay = Parse(_styledKml);

    Assert.Equal(5, overlay.Routes.Count);
    ImportedOverlayRoute polygon = Assert.Single(overlay.Routes,
        route => route.Name == "polygon");
    Assert.True(polygon.Closed);
    Assert.Equal(5, polygon.Points.Count);
    ImportedOverlayRoute multiRoute = Assert.Single(overlay.Routes,
        route => route.Name == "kmlroute");
    Assert.Equal(new ImportedMapColor(0x33, 0x22, 0x11, 0x7f), multiRoute.Color);

    Assert.Equal(2, overlay.Markers.Count);
    ImportedOverlayMarker multiPoint = Assert.Single(overlay.Markers,
        marker => marker.Name == "");
    Assert.True(multiPoint.ShowLabel);
    ImportedOverlayMarker namedPoint = Assert.Single(overlay.Markers,
        marker => marker.Name == "Camera");
    Assert.True(namedPoint.ShowLabel);
    Assert.Equal(48, namedPoint.Point.Lat);
    Assert.Equal(38, namedPoint.Point.Lng);
  }

  [Fact]
  public void Kml_overlay_missing_style_or_document_falls_back_without_aborting() {
    ImportedMapOverlay missing = Parse("""
        <kml xmlns="http://www.opengis.net/kml/2.2"><Document><Placemark>
          <styleUrl>#missing</styleUrl><LineString><coordinates>30,40 31,41</coordinates></LineString>
        </Placemark></Document></kml>
        """);
    ImportedMapOverlay noDocument = Parse("""
        <kml xmlns="http://www.opengis.net/kml/2.2"><Placemark>
          <LineString><coordinates>30,40 31,41</coordinates></LineString>
        </Placemark></kml>
        """);

    Assert.Equal(new ImportedMapColor(255, 255, 255),
        Assert.Single(missing.Routes).Color);
    Assert.Equal(new ImportedMapColor(255, 255, 255),
        Assert.Single(noDocument.Routes).Color);
  }

  [Fact]
  public void Empty_Snippet_element_does_not_prevent_overlay_import() {
    ImportedMapOverlay overlay = Parse("""
        <kml xmlns="http://www.opengis.net/kml/2.2"><Document><Placemark>
          <Snippet/><LineString><coordinates>30,40 31,41</coordinates></LineString>
        </Placemark></Document></kml>
        """);

    Assert.Single(overlay.Routes);
  }

  [Fact]
  public void Kmz_overlay_keeps_multiple_lines_as_separate_routes() {
    string path = Path.Combine(Path.GetTempPath(), $"mp-overlay-{Guid.NewGuid():N}.kmz");
    try {
      using (var archive = ZipFile.Open(path, ZipArchiveMode.Create)) {
        using var writer = new StreamWriter(archive.CreateEntry("folder/DOC.KML").Open());
        writer.Write("""
            <kml xmlns="http://www.opengis.net/kml/2.2"><Document>
              <Placemark><name>one</name><LineString><coordinates>30,40 31,41</coordinates></LineString></Placemark>
              <Placemark><name>two</name><LineString><coordinates>32,42 33,43</coordinates></LineString></Placemark>
            </Document></kml>
            """);
      }

      ImportedMapOverlay overlay = KmlMissionReader.ReadOverlay(path);

      Assert.Equal(2, overlay.Routes.Count);
      Assert.All(overlay.Routes, route => Assert.Equal(2, route.Points.Count));
    } finally {
      File.Delete(path);
    }
  }

  [Fact]
  public void Styled_overlay_parser_does_not_change_mission_or_poi_import() {
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(_styledKml));

    KmlMissionContent content = KmlMissionReader.Parse(stream);

    Assert.Equal(8, content.Route.Count);
    Assert.Equal("Camera", Assert.Single(content.Pois).Name);
  }

  [Fact]
  public void LatLonBox_rotation_matches_upstream_corner_order_and_threshold() {
    IReadOnlyList<ImportedGeoPoint> unrotated = Assert.IsAssignableFrom<
        IReadOnlyList<ImportedGeoPoint>>(KmlGroundOverlayProcessor.ConvertLatLonBox(
        new LatLonBox { North = 46, South = 44, East = 11, West = 9, Rotation = 0.0005 }));
    Assert.Equal(new ImportedGeoPoint(44, 9), unrotated[0]);
    Assert.Equal(new ImportedGeoPoint(46, 9), unrotated[3]);

    IReadOnlyList<ImportedGeoPoint> rotated = Assert.IsAssignableFrom<
        IReadOnlyList<ImportedGeoPoint>>(KmlGroundOverlayProcessor.ConvertLatLonBox(
        new LatLonBox { North = 46, South = 44, East = 11, West = 9, Rotation = 30 }));
    Assert.Equal(43.780421, rotated[0].Lat, 5);
    Assert.Equal(9.841081, rotated[0].Lng, 5);
    Assert.True(rotated[1].Lat > rotated[0].Lat);
    Assert.True(rotated[2].Lng > rotated[3].Lng);
  }

  [Fact]
  public void GroundOverlay_affine_warp_preserves_image_orientation_and_ignores_lr_corner() {
    byte[] source = CreateFourColorPng(20);
    var corners = new[] {
      new ImportedGeoPoint(40, 30),
      new ImportedGeoPoint(40, 32),
      new ImportedGeoPoint(41, 32),
      new ImportedGeoPoint(41, 30),
    };

    byte[] first = Assert.IsType<byte[]>(KmlGroundOverlayProcessor.WarpToBoundingRectangle(
        source, corners, 41, 40, 32, 30));
    byte[] second = Assert.IsType<byte[]>(KmlGroundOverlayProcessor.WarpToBoundingRectangle(
        source, corners.Select((corner, index) => index == 1
            ? new ImportedGeoPoint(40.5, 31.5)
            : corner).ToArray(), 41, 40, 32, 30));

    Assert.Equal(first, second);
    using SKBitmap decoded = Assert.IsType<SKBitmap>(SKBitmap.Decode(first));
    int left = decoded.Width / 4;
    int right = decoded.Width * 3 / 4;
    int top = decoded.Height / 4;
    int bottom = decoded.Height * 3 / 4;
    AssertDominant(decoded.GetPixel(left, top), red: true);
    AssertDominant(decoded.GetPixel(right, top), green: true);
    AssertDominant(decoded.GetPixel(left, bottom), blue: true);
    SKColor lowerRight = decoded.GetPixel(right, bottom);
    Assert.True(lowerRight.Red > 180 && lowerRight.Green > 180 && lowerRight.Blue < 80);
  }

  [Fact]
  public void PlainKml_GroundOverlay_resolves_relative_image_and_keeps_bounds() {
    WithDirectory(directory => {
      File.WriteAllBytes(Path.Combine(directory, "corners.png"), CreateFourColorPng(20));
      string path = Path.Combine(directory, "overlay.kml");
      File.WriteAllText(path, GroundOverlayKml("corners.png", """
          <LatLonBox><north>41</north><south>40</south><east>32</east><west>30</west></LatLonBox>
          """));

      ImportedMapOverlay overlay = KmlMissionReader.ReadOverlay(path);

      ImportedOverlayRaster raster = Assert.Single(overlay.Rasters);
      Assert.True(overlay.HasContent);
      Assert.Equal((41d, 40d, 32d, 30d),
          (raster.North, raster.South, raster.East, raster.West));
      using SKBitmap decoded = Assert.IsType<SKBitmap>(SKBitmap.Decode(raster.Png));
      Assert.Equal(20, decoded.Width);
      Assert.Equal(10, decoded.Height);
    });
  }

  [Fact]
  public void Kmz_GroundOverlay_resolves_nested_backslash_href_and_gx_quad() {
    string path = Path.Combine(Path.GetTempPath(), $"mp-ground-{Guid.NewGuid():N}.kmz");
    try {
      using (var archive = ZipFile.Open(path, ZipArchiveMode.Create)) {
        byte[] png = CreateFourColorPng(20);
        using (Stream image = archive.CreateEntry("folder/images/corners.png").Open()) {
          image.Write(png);
        }
        using var writer = new StreamWriter(archive.CreateEntry("folder/doc.kml").Open());
        writer.Write(GroundOverlayKml(@"images\corners.png", """
            <gx:LatLonQuad><coordinates>30,40 32,40 32,41 30,41</coordinates></gx:LatLonQuad>
            """));
      }

      ImportedMapOverlay overlay = KmlMissionReader.ReadOverlay(path);

      Assert.Single(overlay.Rasters);
    } finally {
      File.Delete(path);
    }
  }

  [Fact]
  public void Unsafe_or_bad_GroundOverlay_is_skipped_without_losing_vectors() {
    string path = Path.Combine(Path.GetTempPath(), $"mp-ground-{Guid.NewGuid():N}.kmz");
    try {
      using (var archive = ZipFile.Open(path, ZipArchiveMode.Create)) {
        byte[] png = CreateFourColorPng(20);
        using (Stream image = archive.CreateEntry("image.png").Open()) {
          image.Write(png);
        }
        using var writer = new StreamWriter(archive.CreateEntry("folder/doc.kml").Open());
        writer.Write("""
            <kml xmlns="http://www.opengis.net/kml/2.2"><Document>
              <GroundOverlay><Icon><href>../image.png</href></Icon>
                <LatLonBox><north>41</north><south>40</south><east>32</east><west>30</west></LatLonBox>
              </GroundOverlay>
              <Placemark><LineString><coordinates>30,40 31,41</coordinates></LineString></Placemark>
            </Document></kml>
            """);
      }

      ImportedMapOverlay overlay = KmlMissionReader.ReadOverlay(path);

      Assert.Empty(overlay.Rasters);
      Assert.Single(overlay.Routes);
    } finally {
      File.Delete(path);
    }
  }

  [Fact]
  public void Malformed_and_remote_GroundOverlay_images_are_skipped() {
    WithDirectory(directory => {
      File.WriteAllBytes(Path.Combine(directory, "bad.png"), [1, 2, 3, 4]);
      foreach (string href in new[] { "bad.png", "https://example.invalid/image.png" }) {
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".kml");
        File.WriteAllText(path, GroundOverlayKml(href, """
            <LatLonBox><north>41</north><south>40</south><east>32</east><west>30</west></LatLonBox>
            """, includeLine: true));

        ImportedMapOverlay overlay = KmlMissionReader.ReadOverlay(path);

        Assert.Empty(overlay.Rasters);
        Assert.Single(overlay.Routes);
      }
    });
  }

  private static ImportedMapOverlay Parse(string xml) {
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
    return KmlMissionReader.ParseOverlay(stream);
  }

  private static string GroundOverlayKml(string href, string bounds, bool includeLine = false) => $$"""
      <kml xmlns="http://www.opengis.net/kml/2.2" xmlns:gx="http://www.google.com/kml/ext/2.2"><Document>
        <GroundOverlay><name>image</name><Icon><href>{{href}}</href></Icon>{{bounds}}</GroundOverlay>
        {{(includeLine ? "<Placemark><LineString><coordinates>30,40 31,41</coordinates></LineString></Placemark>" : "")}}
      </Document></kml>
      """;

  private static byte[] CreateFourColorPng(int size) {
    using var bitmap = new SKBitmap(size, size);
    for (int y = 0; y < size; y++) {
      for (int x = 0; x < size; x++) {
        bitmap.SetPixel(x, y, (x < size / 2, y < size / 2) switch {
          (true, true) => SKColors.Red,
          (false, true) => SKColors.Green,
          (true, false) => SKColors.Blue,
          _ => SKColors.Yellow,
        });
      }
    }
    using SKImage image = SKImage.FromBitmap(bitmap);
    using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    return encoded.ToArray();
  }

  private static void AssertDominant(
      SKColor color,
      bool red = false,
      bool green = false,
      bool blue = false) {
    Assert.True(!red || color.Red > 180, color.ToString());
    Assert.True(!green || color.Green > 90, color.ToString());
    Assert.True(!blue || color.Blue > 180, color.ToString());
    if (red) {
      Assert.True(color.Green < 80 && color.Blue < 80, color.ToString());
    }
    if (green) {
      Assert.True(color.Red < 80 && color.Blue < 80, color.ToString());
    }
    if (blue) {
      Assert.True(color.Red < 80 && color.Green < 80, color.ToString());
    }
  }

  private static void WithDirectory(Action<string> action) {
    string directory = Path.Combine(Path.GetTempPath(), $"mp-ground-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try {
      action(directory);
    } finally {
      Directory.Delete(directory, true);
    }
  }
}

using BitMiracle.LibTiff.Classic;
using BruTile;
using MissionPlannerAvalonia.Services;
using SkiaSharp;

namespace MissionPlannerAvalonia.Tests;

public sealed class NativeGdalMapTests {
  private const double WebMercatorHalfWorld = 20037508.342789244;

  [Fact]
  public void Native_library_candidates_include_the_current_platform_and_are_unique() {
    IReadOnlyList<string> candidates = NativeGdalApi.LibraryCandidates();

    Assert.NotEmpty(candidates);
    Assert.Equal(candidates.Count, candidates.Distinct(StringComparer.Ordinal).Count());
    if (OperatingSystem.IsWindows()) {
      Assert.Contains("gdal.dll", candidates);
    } else if (OperatingSystem.IsMacOS()) {
      Assert.Contains("libgdal.dylib", candidates);
    } else {
      Assert.Contains("libgdal.so", candidates);
    }
  }

  [Fact]
  public void Intersection_and_source_over_blending_preserve_transparency() {
    Assert.True(NativeGdalDataset.TryIntersection(
        new Extent(0, 0, 10, 10), new Extent(5, -5, 15, 5), out Extent overlap));
    Assert.Equal(new Extent(5, 0, 10, 5), overlap);
    Assert.False(NativeGdalDataset.TryIntersection(
        new Extent(0, 0, 1, 1), new Extent(1, 0, 2, 1), out _));

    byte[] destination = [0, 0, 255, 255];
    NativeGdalDataset.Blend(destination, 0, 255, 0, 0, 128);

    Assert.InRange(destination[0], 127, 128);
    Assert.Equal(0, destination[1]);
    Assert.InRange(destination[2], 126, 127);
    Assert.Equal(255, destination[3]);
  }

  [Fact]
  public void Composite_tiles_places_a_translucent_local_raster_over_the_base_map() {
    byte[] background = SolidPng(new SKColor(0, 0, 255, 255));
    byte[] overlay = SolidPng(new SKColor(255, 0, 0, 128));

    byte[] composed = Assert.IsType<byte[]>(
        NativeGdalMapService.CompositeTiles(background, overlay, tileSize: 2));
    using SKBitmap bitmap = SKBitmap.Decode(composed);
    SKColor pixel = bitmap.GetPixel(0, 0);

    Assert.InRange(pixel.Red, 127, 129);
    Assert.Equal(0, pixel.Green);
    Assert.InRange(pixel.Blue, 126, 128);
    Assert.Equal(255, pixel.Alpha);
  }

  [Fact]
  public async Task Installed_native_gdal_indexes_and_renders_a_georeferenced_geotiff() {
    if (!NativeGdalMapService.IsAvailable) {
      // GDAL is an optional runtime dependency. Candidate and compositing behavior remains
      // covered on hosts which intentionally do not install it.
      return;
    }

    string directory = Path.Combine(Path.GetTempPath(), "mp-gdal-" + Guid.NewGuid());
    Directory.CreateDirectory(directory);
    try {
      string raster = Path.Combine(directory, "world.tif");
      WriteWebMercatorGeoTiff(raster);

      NativeGdalScanResult result = await NativeGdalMapService.ScanAsync(
          directory, progress: null, CancellationToken.None);

      NativeGdalRasterFile file = Assert.Single(result.Files);
      Assert.True(file.Indexed, file.Error);
      Assert.Equal("GTiff", file.Driver);
      Assert.Contains("64", file.Size);
      byte[] tile = Assert.IsType<byte[]>(NativeGdalMapService.RenderTile(
          new Extent(
              -WebMercatorHalfWorld, -WebMercatorHalfWorld,
              WebMercatorHalfWorld, WebMercatorHalfWorld),
          tileSize: 64));
      using SKBitmap bitmap = SKBitmap.Decode(tile);
      Assert.Equal(64, bitmap.Width);
      Assert.Equal(64, bitmap.Height);
      Assert.Equal(255, bitmap.GetPixel(8, 8).Alpha);
      Assert.NotEqual(bitmap.GetPixel(8, 8).Red, bitmap.GetPixel(56, 56).Red);
    } finally {
      NativeGdalMapService.Unload();
      Directory.Delete(directory, recursive: true);
    }
  }

  private static byte[] SolidPng(SKColor color) {
    using var bitmap = new SKBitmap(2, 2);
    bitmap.Erase(color);
    using SKImage image = SKImage.FromBitmap(bitmap);
    using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    return encoded.ToArray();
  }

  private static void WriteWebMercatorGeoTiff(string path) {
    ElevationSourceService.EnsureGeoTiffTagsRegistered();
    using Tiff tiff = Tiff.Open(path, "w")
        ?? throw new InvalidOperationException("Could not create the GDAL test GeoTIFF.");
    const int size = 64;
    double pixelSize = WebMercatorHalfWorld * 2 / size;
    tiff.SetField(TiffTag.IMAGEWIDTH, size);
    tiff.SetField(TiffTag.IMAGELENGTH, size);
    tiff.SetField(TiffTag.SAMPLESPERPIXEL, 1);
    tiff.SetField(TiffTag.BITSPERSAMPLE, 8);
    tiff.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.UINT);
    tiff.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
    tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
    tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);
    tiff.SetField(TiffTag.COMPRESSION, Compression.NONE);
    tiff.SetField(TiffTag.ROWSPERSTRIP, 1);
    tiff.SetField(TiffTag.GEOTIFF_MODELPIXELSCALETAG, 3,
        new[] { pixelSize, pixelSize, 0d });
    tiff.SetField(TiffTag.GEOTIFF_MODELTIEPOINTTAG, 6,
        new[] { 0d, 0d, 0d, -WebMercatorHalfWorld, WebMercatorHalfWorld, 0d });
    ushort[] geoKeys = [
      1, 1, 0, 4,
      1024, 0, 1, 1,
      1025, 0, 1, 1,
      3072, 0, 1, 3857,
      3076, 0, 1, 9001,
    ];
    tiff.SetField((TiffTag)34735, geoKeys.Length, geoKeys);
    for (int row = 0; row < size; row++) {
      var pixels = new byte[size];
      for (int column = 0; column < size; column++) {
        pixels[column] = (byte)(32 + (row + column) * 191 / (size * 2 - 2));
      }
      Assert.True(tiff.WriteScanline(pixels, row));
    }
    tiff.WriteDirectory();
  }
}

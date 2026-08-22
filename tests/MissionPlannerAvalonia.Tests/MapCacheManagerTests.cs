using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using BruTile;
using BruTile.Cache;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;
using MissionPlannerAvalonia.Views;
using SkiaSharp;

namespace MissionPlannerAvalonia.Tests;

public class MapCacheManagerTests {
  [Fact]
  public void Scans_and_cleans_provider_or_total_cache_safely() {
    string root = Path.Combine(Path.GetTempPath(), "mp-map-cache-" + Guid.NewGuid().ToString("N"));
    string providerA = Path.Combine(root, "provider-a", "1", "2");
    string providerB = Path.Combine(root, "provider-b");
    Directory.CreateDirectory(providerA);
    Directory.CreateDirectory(providerB);
    string oldTile = Path.Combine(providerA, "old.tile");
    string newTile = Path.Combine(providerA, "new.tile");
    string otherTile = Path.Combine(providerB, "other.tile");
    try {
      File.WriteAllBytes(oldTile, [1, 2, 3]);
      File.WriteAllBytes(newTile, [1, 2, 3, 4]);
      File.WriteAllBytes(otherTile, [1, 2]);
      File.SetLastWriteTimeUtc(oldTile, DateTime.UtcNow.AddDays(-40));

      var entries = MapCacheManager.Scan(root);
      Assert.Equal(3, entries.Count);
      var a = Assert.Single(entries, item => item.Name == "provider-a");
      var total = Assert.Single(entries, item => item.IsTotal);
      Assert.Equal(2, a.FileCount);
      Assert.Equal(7, a.SizeBytes);
      Assert.Equal(3, total.FileCount);
      Assert.Equal(9, total.SizeBytes);

      var oldResult = MapCacheManager.DeleteOlderThan(a, DateTime.UtcNow.AddDays(-30), root);
      Assert.Equal(1, oldResult.RemovedFiles);
      Assert.Equal(3, oldResult.FreedBytes);
      Assert.False(File.Exists(oldTile));
      Assert.True(File.Exists(newTile));

      var allResult = MapCacheManager.DeleteOlderThan(total, DateTime.MaxValue, root);
      Assert.Equal(2, allResult.RemovedFiles);
      Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
    } finally {
      if (Directory.Exists(root)) {
        Directory.Delete(root, recursive: true);
      }
    }
  }

  [Fact]
  public void Refuses_cleanup_outside_the_configured_cache_root() {
    string root = Path.Combine(Path.GetTempPath(), "mp-map-root-" + Guid.NewGuid().ToString("N"));
    string outside = Path.Combine(Path.GetTempPath(), "mp-map-outside-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    Directory.CreateDirectory(outside);
    try {
      var entry = new MapCacheSnapshot("outside", outside, 0, 0, null);
      Assert.Throws<InvalidOperationException>(() =>
          MapCacheManager.DeleteOlderThan(entry, DateTime.MaxValue, root));
    } finally {
      Directory.Delete(root, recursive: true);
      Directory.Delete(outside, recursive: true);
    }
  }

  [Fact]
  public void Parses_official_ge_injection_row_column_layout() {
    string root = Path.Combine(Path.GetTempPath(), "mp-map-import-root");
    string tile = Path.Combine(root, "survey", "Z12", "1362", "2048.jpg");

    Assert.True(MapTileImporter.TryParseOfficialPath(root, tile, out TileIndex index));
    Assert.Equal(12, index.Level);
    Assert.Equal(2048, index.Col);
    Assert.Equal(1362, index.Row);
    Assert.False(MapTileImporter.TryParseOfficialPath(
        root, Path.Combine(root, "Z12", "5000", "2048.jpg"), out _));
    Assert.False(MapTileImporter.TryParseOfficialPath(
        root, Path.Combine(root, "12", "1362", "2048.jpg"), out _));
    Assert.False(MapTileImporter.TryParseOfficialPath(
        root, Path.Combine(root, "..", "Z12", "1362", "2048.jpg"), out _));
  }

  [Fact]
  public void Imports_valid_images_and_reports_invalid_duplicate_and_failed_files() {
    string source = Path.Combine(Path.GetTempPath(), "mp-map-import-" + Guid.NewGuid().ToString("N"));
    string cacheRoot = Path.Combine(Path.GetTempPath(), "mp-map-import-cache-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(source, "Z3", "2"));
    Directory.CreateDirectory(Path.Combine(source, "Z3", "20"));
    Directory.CreateDirectory(Path.Combine(source, "zz-copy", "Z3", "2"));
    Directory.CreateDirectory(cacheRoot);
    var cache = new FileCache(cacheRoot, "tile");
    byte[] jpeg = CreateTile(SKEncodedImageFormat.Jpeg, SKColors.OrangeRed);
    byte[] png = CreateTile(SKEncodedImageFormat.Png, SKColors.SteelBlue);
    try {
      cache.Add(new TileIndex(4, 2, 3), png);
      File.WriteAllBytes(Path.Combine(source, "Z3", "2", "4.jpg"), jpeg);
      File.WriteAllBytes(Path.Combine(source, "Z3", "2", "5.png"), png);
      File.WriteAllBytes(Path.Combine(source, "Z3", "2", "6.jpg"), [1, 2, 3]);
      File.WriteAllBytes(Path.Combine(source, "Z3", "20", "7.jpg"), jpeg);
      File.WriteAllBytes(Path.Combine(source, "zz-copy", "Z3", "2", "4.png"), png);
      File.WriteAllText(Path.Combine(source, "Z3", "2", "ignored.txt"), "not a tile");

      MapTileImportResult result = MapTileImporter.Import(source, cache);

      Assert.Equal(5, result.Discovered);
      Assert.Equal(2, result.Imported);
      Assert.Equal(3, result.Skipped);
      Assert.Equal(0, result.Failed);
      Assert.Equal(jpeg.Length + png.Length, result.ImportedBytes);
      Assert.Equal(jpeg, cache.Find(new TileIndex(4, 2, 3)));
      Assert.Equal(png, cache.Find(new TileIndex(5, 2, 3)));
      Assert.Null(cache.Find(new TileIndex(6, 2, 3)));
    } finally {
      if (Directory.Exists(source)) {
        Directory.Delete(source, recursive: true);
      }
      if (Directory.Exists(cacheRoot)) {
        Directory.Delete(cacheRoot, recursive: true);
      }
    }
  }

  [AvaloniaFact]
  public void Map_cache_window_exposes_provider_scoped_import_and_cancel_controls() {
    using var viewModel = new MapCacheViewModel();
    var view = new MapCacheView { DataContext = viewModel };

    var provider = Assert.IsType<ComboBox>(view.FindControl<ComboBox>("ImportMapTypePicker"));
    var import = Assert.IsType<Button>(view.FindControl<Button>("ImportTilesButton"));
    var cancel = Assert.IsType<Button>(view.FindControl<Button>("CancelImportButton"));

    Assert.Contains("GoogleSatelliteMap", viewModel.ImportMapTypes);
    Assert.Equal(viewModel.SelectedImportMapType, provider.SelectedItem);
    Assert.NotNull(import.Command);
    Assert.NotNull(cancel.Command);
    Assert.False(cancel.IsVisible);
  }

  private static byte[] CreateTile(SKEncodedImageFormat format, SKColor color) {
    using var bitmap = new SKBitmap(2, 2);
    bitmap.Erase(color);
    using SKImage image = SKImage.FromBitmap(bitmap);
    using SKData encoded = image.Encode(format, 95);
    return encoded.ToArray();
  }
}

using System.Buffers.Binary;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;
using MissionPlannerAvalonia.Views;

namespace MissionPlannerAvalonia.Tests;

public sealed class TerrainDataServiceTests {
  [Fact]
  public void CrcMatchesStandardXmodemVector() {
    Assert.Equal(0x31c3, TerrainDataService.ComputeCrc(Encoding.ASCII.GetBytes("123456789")));
  }

  [Fact]
  public void BoundsExpandToTheSameWholeDegreeTilesAsUpstream() {
    IReadOnlyList<TerrainTile> tiles = TerrainDataService.TilesForBounds(
        new TerrainBounds(-1.2, 32.9, 0.1, 34.01));

    Assert.Equal(
        [
          new TerrainTile(-2, 32),
          new TerrainTile(-2, 33),
          new TerrainTile(-2, 34),
          new TerrainTile(-1, 32),
          new TerrainTile(-1, 33),
          new TerrainTile(-1, 34),
          new TerrainTile(0, 32),
          new TerrainTile(0, 33),
          new TerrainTile(0, 34),
        ],
        tiles);
    Assert.Equal("S02E032.DAT", tiles[0].FileName);
    Assert.Equal("N00E034.DAT", tiles[^1].FileName);
  }

  [Fact]
  public void EstimateUsesFull2048ByteBlocksAndAllGridSamples() {
    var options = new TerrainMakerOptions(
        new TerrainBounds(34.1, 32.1, 34.2, 32.2),
        30,
        Path.GetTempPath());

    TerrainMakerEstimate estimate = TerrainDataService.Estimate(options);

    Assert.Equal(1, estimate.TileCount);
    Assert.True(estimate.BlockCount > 0);
    Assert.Equal(
        estimate.BlockCount
            * TerrainDataService.TerrainGridBlockSizeX
            * TerrainDataService.TerrainGridBlockSizeY,
        estimate.SampleCount);
    Assert.Equal(estimate.BlockCount * TerrainDataService.IoBlockSize, estimate.OutputBytes);
  }

  [Fact]
  public void ViewModelStartsFromVisibleBoundsAndReportsExactEstimate() {
    var viewModel = new TerrainMakerViewModel(
        new TerrainBounds(34.12345678, 32.12345678, 34.87654321, 32.87654321));

    TerrainMakerOptions options = viewModel.BuildOptions();

    Assert.Equal(34.1234568m, viewModel.South);
    Assert.Equal(32.1234568m, viewModel.West);
    Assert.Equal(34.8765432m, viewModel.North);
    Assert.Equal(32.8765432m, viewModel.East);
    Assert.Equal(30, options.SpacingMeters);
    Assert.Contains("tile(s)", viewModel.EstimateText, StringComparison.Ordinal);
    Assert.Contains("blocks", viewModel.EstimateText, StringComparison.Ordinal);
    Assert.Contains("samples", viewModel.EstimateText, StringComparison.Ordinal);
    viewModel.Dispose();
  }

  [Fact]
  public void PackedBlockHasOfficialLayoutCrcAndCompletePadding() {
    byte[] packed = TerrainDataService.CreatePackedBlock(
        new TerrainTile(34, 32),
        30,
        0,
        (_, _) => new TerrainElevationSample(123, IsValid: true),
        CancellationToken.None,
        out int valid,
        out int missing);

    Assert.Equal(TerrainDataService.IoBlockSize, packed.Length);
    Assert.Equal(TerrainDataService.TerrainGridBlockSizeX
        * TerrainDataService.TerrainGridBlockSizeY, valid);
    Assert.Equal(0, missing);
    Assert.Equal(0x00ffffffffffffffUL,
        BinaryPrimitives.ReadUInt64LittleEndian(packed));
    Assert.Equal(34 * 10_000_000,
        BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(8)));
    Assert.Equal(32 * 10_000_000,
        BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(12)));
    Assert.Equal(TerrainDataService.TerrainGridFormatVersion,
        BinaryPrimitives.ReadUInt16LittleEndian(packed.AsSpan(18)));
    Assert.Equal(30, BinaryPrimitives.ReadUInt16LittleEndian(packed.AsSpan(20)));
    Assert.Equal(123, BinaryPrimitives.ReadInt16LittleEndian(packed.AsSpan(22)));
    Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(packed.AsSpan(1814)));
    Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(packed.AsSpan(1816)));
    Assert.Equal(32, BinaryPrimitives.ReadInt16LittleEndian(packed.AsSpan(1818)));
    Assert.Equal(34, unchecked((sbyte)packed[1820]));
    Assert.All(packed[TerrainDataService.IoBlockDataSize..], value => Assert.Equal(0, value));

    ushort expectedCrc = BinaryPrimitives.ReadUInt16LittleEndian(packed.AsSpan(16));
    byte[] crcInput = packed[..TerrainDataService.IoBlockDataSize];
    crcInput[16] = 0;
    crcInput[17] = 0;
    Assert.Equal(expectedCrc, TerrainDataService.ComputeCrc(crcInput));
  }

  [Fact]
  public void MissingHeightClearsOnlyItsContainingMavlinkGridBit() {
    int sample = 0;
    byte[] packed = TerrainDataService.CreatePackedBlock(
        new TerrainTile(-35, -117),
        100,
        0,
        (_, _) => ++sample == 1
            ? new TerrainElevationSample(0, IsValid: true)
            : new TerrainElevationSample(sample, IsValid: true),
        CancellationToken.None,
        out int valid,
        out int missing);

    Assert.Equal(895, valid);
    Assert.Equal(1, missing);
    Assert.Equal(0x00fffffffffffffeUL,
        BinaryPrimitives.ReadUInt64LittleEndian(packed));
    Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(packed.AsSpan(22)));
    Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(packed.AsSpan(24)));
    Assert.Equal(33, BinaryPrimitives.ReadInt16LittleEndian(
        packed.AsSpan(22 + TerrainDataService.TerrainGridBlockSizeY * sizeof(short))));
  }

  [Fact]
  public async Task PreCancelledGenerationDoesNotCreateOutputDirectory() {
    string output = Path.Combine(
        Path.GetTempPath(), "mp-terrain-cancel-" + Guid.NewGuid().ToString("N"));
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        TerrainDataService.GenerateAsync(
            new TerrainMakerOptions(
                new TerrainBounds(34.1, 32.1, 34.2, 32.2),
                30,
                output),
            progress: null,
            cancellation.Token,
            (_, _) => new TerrainElevationSample(100, IsValid: true)));
    Assert.False(Directory.Exists(output));
  }

  [Fact]
  public void AtomicPublisherWritesOnlyComplete2048ByteBlocks() {
    string directory = Path.Combine(
        Path.GetTempPath(), "mp-terrain-publish-" + Guid.NewGuid().ToString("N"));
    string output = Path.Combine(directory, "N34E032.DAT");
    try {
      TerrainDataService.PublishBlocksAtomically(
          output,
          2,
          blockNumber => Enumerable.Repeat(
              checked((byte)(blockNumber + 1)), TerrainDataService.IoBlockSize).ToArray(),
          CancellationToken.None);

      byte[] published = File.ReadAllBytes(output);
      Assert.Equal(2 * TerrainDataService.IoBlockSize, published.Length);
      Assert.All(published[..TerrainDataService.IoBlockSize], value => Assert.Equal(1, value));
      Assert.All(published[TerrainDataService.IoBlockSize..], value => Assert.Equal(2, value));
      Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    } finally {
      if (Directory.Exists(directory)) {
        Directory.Delete(directory, recursive: true);
      }
    }
  }

  [Fact]
  public void CancelledPublisherPreservesPriorCompleteTileAndRemovesTemporaryFile() {
    string directory = Path.Combine(
        Path.GetTempPath(), "mp-terrain-preserve-" + Guid.NewGuid().ToString("N"));
    string output = Path.Combine(directory, "N34E032.DAT");
    Directory.CreateDirectory(directory);
    byte[] priorTile = [0x51, 0x52, 0x53];
    File.WriteAllBytes(output, priorTile);
    using var cancellation = new CancellationTokenSource();
    try {
      Assert.ThrowsAny<OperationCanceledException>(() =>
          TerrainDataService.PublishBlocksAtomically(
              output,
              2,
              _ => {
                cancellation.Cancel();
                return new byte[TerrainDataService.IoBlockSize];
              },
              cancellation.Token));

      Assert.Equal(priorTile, File.ReadAllBytes(output));
      Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Theory]
  [InlineData(-91, 0, 1, 1, 30)]
  [InlineData(0, 0, 0, 1, 30)]
  [InlineData(0, 1, 1, 0, 30)]
  [InlineData(0, 0, 1, 1, 4)]
  [InlineData(0, 0, 1, 1, 101)]
  public void InvalidCoordinatesOrSpacingAreRejected(
      double south,
      double west,
      double north,
      double east,
      ushort spacing) {
    Assert.ThrowsAny<ArgumentException>(() => TerrainDataService.Validate(
        new TerrainMakerOptions(
            new TerrainBounds(south, west, north, east),
            spacing,
            Path.GetTempPath())));
  }

  [AvaloniaFact]
  public void TerrainMakerWindowLoadsNativeControlsAndPlannerMenuEntry() {
    var window = new TerrainMakerWindow();
    var planner = new FlightPlannerView();
    FlightPlannerMap map = planner.FindControl<FlightPlannerMap>("Map")!;

    Assert.NotNull(window.FindControl<TextBlock>("EstimateText"));
    Assert.Contains(
        map.ContextMenu!.Items.OfType<MenuItem>(),
        item => string.Equals(item.Header?.ToString(), "Make Terrain DAT…",
            StringComparison.Ordinal));

    window.Close();
  }
}

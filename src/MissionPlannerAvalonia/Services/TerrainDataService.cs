using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

internal readonly record struct TerrainBounds(
    double South,
    double West,
    double North,
    double East);

internal readonly record struct TerrainTile(int LatitudeDegrees, int LongitudeDegrees) {
  internal string FileName {
    get {
      string northSouth = LatitudeDegrees < 0 ? "S" : "N";
      string eastWest = LongitudeDegrees < 0 ? "W" : "E";
      return string.Create(
          CultureInfo.InvariantCulture,
          $"{northSouth}{Math.Abs(LatitudeDegrees):00}{eastWest}{Math.Abs(LongitudeDegrees):000}.DAT");
    }
  }
}

internal readonly record struct TerrainMakerOptions(
    TerrainBounds Bounds,
    ushort SpacingMeters,
    string OutputDirectory);

internal readonly record struct TerrainMakerEstimate(
    int TileCount,
    long BlockCount,
    long SampleCount,
    long OutputBytes);

internal readonly record struct TerrainMakerProgress(
    long CompletedBlocks,
    long TotalBlocks,
    int CompletedTiles,
    int TotalTiles,
    string CurrentFile) {
  internal double Fraction => TotalBlocks == 0
      ? 0
      : Math.Clamp((double)CompletedBlocks / TotalBlocks, 0, 1);
}

internal sealed record TerrainMakerResult(
    IReadOnlyList<string> Files,
    long OutputBytes,
    long ValidSamples,
    long MissingSamples);

internal readonly record struct TerrainElevationSample(double AltitudeMeters, bool IsValid);

/// <summary>
/// Writes the ArduPilot terrain database format used by Mission Planner's TerrainMakerPlugin.
/// The pinned plugin is the format authority; this implementation removes its WinForms boundary,
/// mutable global spacing and 2047-byte block bug while preserving its coordinates, height order,
/// bitmap and CRC semantics.
/// </summary>
internal static class TerrainDataService {
  internal const int MinimumSpacingMeters = 5;
  internal const int MaximumSpacingMeters = 100;
  internal const int TerrainGridMavlinkSize = 4;
  internal const int TerrainGridBlockMultiplierX = 7;
  internal const int TerrainGridBlockMultiplierY = 8;
  internal const int TerrainGridBlockSpacingX =
      (TerrainGridBlockMultiplierX - 1) * TerrainGridMavlinkSize;
  internal const int TerrainGridBlockSpacingY =
      (TerrainGridBlockMultiplierY - 1) * TerrainGridMavlinkSize;
  internal const int TerrainGridBlockSizeX =
      TerrainGridMavlinkSize * TerrainGridBlockMultiplierX;
  internal const int TerrainGridBlockSizeY =
      TerrainGridMavlinkSize * TerrainGridBlockMultiplierY;
  internal const ushort TerrainGridFormatVersion = 1;
  internal const int IoBlockSize = 2048;
  internal const int IoBlockDataSize = 1821;

  private const double LocationScalingFactor = 0.011131884502145034;
  private const double LocationScalingFactorInverse = 89.83204953368922;
  private static readonly SemaphoreSlim _generationGate = new(1, 1);

  internal static TerrainMakerEstimate Estimate(TerrainMakerOptions options) {
    Validate(options);
    IReadOnlyList<TerrainTile> tiles = TilesForBounds(options.Bounds);
    long blocks = 0;
    var blocksByLatitude = new Dictionary<int, long>();
    foreach (TerrainTile tile in tiles) {
      if (!blocksByLatitude.TryGetValue(tile.LatitudeDegrees, out long tileBlocks)) {
        tileBlocks = BlockCount(tile, options.SpacingMeters);
        blocksByLatitude.Add(tile.LatitudeDegrees, tileBlocks);
      }
      blocks = checked(blocks + tileBlocks);
    }
    return new TerrainMakerEstimate(
        tiles.Count,
        blocks,
        checked(blocks * TerrainGridBlockSizeX * TerrainGridBlockSizeY),
        checked(blocks * IoBlockSize));
  }

  internal static IReadOnlyList<TerrainTile> TilesForBounds(TerrainBounds bounds) {
    ValidateBounds(bounds);
    int latitudeStart = (int)Math.Floor(bounds.South);
    int latitudeEnd = (int)Math.Ceiling(bounds.North);
    int longitudeStart = (int)Math.Floor(bounds.West);
    int longitudeEnd = (int)Math.Ceiling(bounds.East);
    var tiles = new List<TerrainTile>(
        checked((latitudeEnd - latitudeStart) * (longitudeEnd - longitudeStart)));
    for (int latitude = latitudeStart; latitude < latitudeEnd; latitude++) {
      for (int longitude = longitudeStart; longitude < longitudeEnd; longitude++) {
        tiles.Add(new TerrainTile(latitude, longitude));
      }
    }
    return tiles;
  }

  internal static async Task<TerrainMakerResult> GenerateAsync(
      TerrainMakerOptions options,
      IProgress<TerrainMakerProgress>? progress,
      CancellationToken cancellationToken,
      Func<double, double, TerrainElevationSample>? elevationProvider = null) {
    Validate(options);
    elevationProvider ??= ReadOfficialElevation;
    await _generationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      return await Task.Run(
          () => GenerateCore(options, elevationProvider, progress, cancellationToken),
          cancellationToken).ConfigureAwait(false);
    } finally {
      _generationGate.Release();
    }
  }

  internal static byte[] CreatePackedBlock(
      TerrainTile tile,
      ushort spacingMeters,
      long blockNumber,
      Func<double, double, TerrainElevationSample> elevationProvider,
      CancellationToken cancellationToken,
      out int validSamples,
      out int missingSamples) {
    ArgumentNullException.ThrowIfNull(elevationProvider);
    ValidateSpacing(spacingMeters);
    long blockCount = BlockCount(tile, spacingMeters);
    if (blockNumber < 0 || blockNumber >= blockCount) {
      throw new ArgumentOutOfRangeException(nameof(blockNumber));
    }

    return CreatePackedBlockCore(
        tile,
        spacingMeters,
        blockNumber,
        elevationProvider,
        cancellationToken,
        out validSamples,
        out missingSamples);
  }

  private static byte[] CreatePackedBlockCore(
      TerrainTile tile,
      ushort spacingMeters,
      long blockNumber,
      Func<double, double, TerrainElevationSample> elevationProvider,
      CancellationToken cancellationToken,
      out int validSamples,
      out int missingSamples) {

    TerrainCoordinate location = PositionFromBlockNumber(tile, spacingMeters, blockNumber);
    var block = new TerrainGridBlock(tile, location, spacingMeters);
    validSamples = 0;
    missingSamples = 0;
    for (int x = 0; x < TerrainGridBlockSizeX; x++) {
      cancellationToken.ThrowIfCancellationRequested();
      for (int y = 0; y < TerrainGridBlockSizeY; y++) {
        TerrainCoordinate point = block.Origin.AddOffsetMeters(
            x * spacingMeters,
            y * spacingMeters);
        TerrainElevationSample sample = elevationProvider(
            point.LatitudeE7 * 1.0e-7,
            point.LongitudeE7 * 1.0e-7);
        if (sample.IsValid
            && double.IsFinite(sample.AltitudeMeters)
            && sample.AltitudeMeters != 0
            && sample.AltitudeMeters >= short.MinValue
            && sample.AltitudeMeters <= short.MaxValue) {
          short height = (short)Math.Round(sample.AltitudeMeters);
          block.SetHeight(x, y, height);
          if (height != 0) {
            validSamples++;
          } else {
            missingSamples++;
          }
        } else {
          block.SetHeight(x, y, 0);
          missingSamples++;
        }
      }
    }

    for (int x = 0; x < TerrainGridBlockMultiplierX; x++) {
      for (int y = 0; y < TerrainGridBlockMultiplierY; y++) {
        if (block.IsValidSubgrid(x, y)) {
          block.SetBitmapBit(y + TerrainGridBlockMultiplierY * x);
        }
      }
    }
    return Pack(block);
  }

  internal static long BlockCount(TerrainTile tile, ushort spacingMeters) {
    ValidateSpacing(spacingMeters);
    TerrainCoordinate reference = TileReference(tile);
    int eastBlocks = EastBlockCount(reference, spacingMeters);
    int northRows = 0;
    while (true) {
      TerrainCoordinate location = reference.AddOffsetMeters(
          (long)northRows * TerrainGridBlockSpacingX * spacingMeters,
          0);
      if (location.LatitudeE7 * 1.0e-7 - tile.LatitudeDegrees >= 1.0) {
        break;
      }
      northRows++;
      if (northRows > 10_000) {
        throw new InvalidDataException("Terrain grid row calculation did not converge.");
      }
    }
    return checked((long)eastBlocks * northRows);
  }

  internal static TerrainCoordinate PositionFromBlockNumber(
      TerrainTile tile,
      ushort spacingMeters,
      long blockNumber) {
    ValidateSpacing(spacingMeters);
    if (blockNumber < 0) {
      throw new ArgumentOutOfRangeException(nameof(blockNumber));
    }
    TerrainCoordinate reference = TileReference(tile);
    int stride = EastBlockCount(reference, spacingMeters);
    long gridIndexX = blockNumber / stride;
    long gridIndexY = blockNumber % stride;
    return reference.AddOffsetMeters(
        checked(gridIndexX * TerrainGridBlockSpacingX * spacingMeters),
        checked(gridIndexY * TerrainGridBlockSpacingY * spacingMeters));
  }

  internal static ushort ComputeCrc(ReadOnlySpan<byte> data) {
    ushort crc = 0;
    foreach (byte value in data) {
      crc ^= (ushort)(value << 8);
      for (int bit = 0; bit < 8; bit++) {
        crc = (ushort)((crc & 0x8000) != 0
            ? (crc << 1) ^ 0x1021
            : crc << 1);
      }
    }
    return crc;
  }

  internal static void Validate(TerrainMakerOptions options) {
    ValidateBounds(options.Bounds);
    ValidateSpacing(options.SpacingMeters);
    if (string.IsNullOrWhiteSpace(options.OutputDirectory)) {
      throw new ArgumentException("Choose an output directory.", nameof(options));
    }
    _ = Path.GetFullPath(options.OutputDirectory);
  }

  private static TerrainMakerResult GenerateCore(
      TerrainMakerOptions options,
      Func<double, double, TerrainElevationSample> elevationProvider,
      IProgress<TerrainMakerProgress>? progress,
      CancellationToken cancellationToken) {
    TerrainMakerEstimate estimate = Estimate(options);
    IReadOnlyList<TerrainTile> tiles = TilesForBounds(options.Bounds);
    string outputDirectory = Path.GetFullPath(options.OutputDirectory);
    Directory.CreateDirectory(outputDirectory);
    var files = new List<string>(tiles.Count);
    long completedBlocks = 0;
    long validSamples = 0;
    long missingSamples = 0;
    var reportTimer = Stopwatch.StartNew();
    progress?.Report(new TerrainMakerProgress(
        0, estimate.BlockCount, 0, tiles.Count, tiles[0].FileName));

    for (int tileIndex = 0; tileIndex < tiles.Count; tileIndex++) {
      cancellationToken.ThrowIfCancellationRequested();
      TerrainTile tile = tiles[tileIndex];
      string finalPath = Path.Combine(outputDirectory, tile.FileName);
      long blocks = BlockCount(tile, options.SpacingMeters);
      PublishBlocksAtomically(
          finalPath,
          blocks,
          blockNumber => {
            byte[] packed = CreatePackedBlockCore(
                tile,
                options.SpacingMeters,
                blockNumber,
                elevationProvider,
                cancellationToken,
                out int blockValidSamples,
                out int blockMissingSamples);
            validSamples += blockValidSamples;
            missingSamples += blockMissingSamples;
            completedBlocks++;
            if (reportTimer.ElapsedMilliseconds >= 100
                || completedBlocks == estimate.BlockCount) {
              progress?.Report(new TerrainMakerProgress(
                  completedBlocks,
                  estimate.BlockCount,
                  tileIndex,
                  tiles.Count,
                  tile.FileName));
              reportTimer.Restart();
            }
            return packed;
          },
          cancellationToken);
      files.Add(finalPath);
      progress?.Report(new TerrainMakerProgress(
          completedBlocks,
          estimate.BlockCount,
          tileIndex + 1,
          tiles.Count,
          tile.FileName));
    }

    return new TerrainMakerResult(
        files,
        files.Sum(path => new FileInfo(path).Length),
        validSamples,
        missingSamples);
  }

  internal static void PublishBlocksAtomically(
      string finalPath,
      long blockCount,
      Func<long, byte[]> blockFactory,
      CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
    ArgumentNullException.ThrowIfNull(blockFactory);
    if (blockCount < 0) {
      throw new ArgumentOutOfRangeException(nameof(blockCount));
    }

    finalPath = Path.GetFullPath(finalPath);
    string? directory = Path.GetDirectoryName(finalPath);
    if (string.IsNullOrEmpty(directory)) {
      throw new ArgumentException("Terrain DAT path must have a parent directory.", nameof(finalPath));
    }
    Directory.CreateDirectory(directory);
    string temporaryPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try {
      using (var stream = new FileStream(
                 temporaryPath,
                 FileMode.CreateNew,
                 FileAccess.Write,
                 FileShare.None,
                 1024 * 1024,
                 FileOptions.SequentialScan)) {
        for (long blockNumber = 0; blockNumber < blockCount; blockNumber++) {
          cancellationToken.ThrowIfCancellationRequested();
          byte[] packed = blockFactory(blockNumber);
          if (packed.Length != IoBlockSize) {
            throw new InvalidDataException(
                $"Terrain block {blockNumber} is {packed.Length} bytes, expected {IoBlockSize}.");
          }
          stream.Write(packed);
        }
        stream.Flush(flushToDisk: true);
      }
      cancellationToken.ThrowIfCancellationRequested();
      File.Move(temporaryPath, finalPath, overwrite: true);
    } catch {
      TryDelete(temporaryPath);
      throw;
    }
  }

  private static byte[] Pack(TerrainGridBlock block) {
    byte[] packed = new byte[IoBlockSize];
    Span<byte> output = packed;
    int offset = 0;
    BinaryPrimitives.WriteUInt64LittleEndian(output[offset..], block.Bitmap);
    offset += sizeof(ulong);
    BinaryPrimitives.WriteInt32LittleEndian(output[offset..], block.LatitudeE7);
    offset += sizeof(int);
    BinaryPrimitives.WriteInt32LittleEndian(output[offset..], block.LongitudeE7);
    offset += sizeof(int);
    int crcOffset = offset;
    BinaryPrimitives.WriteUInt16LittleEndian(output[offset..], 0);
    offset += sizeof(ushort);
    BinaryPrimitives.WriteUInt16LittleEndian(output[offset..], TerrainGridFormatVersion);
    offset += sizeof(ushort);
    BinaryPrimitives.WriteUInt16LittleEndian(output[offset..], block.SpacingMeters);
    offset += sizeof(ushort);

    for (int x = 0; x < TerrainGridBlockSizeX; x++) {
      for (int y = 0; y < TerrainGridBlockSizeY; y++) {
        BinaryPrimitives.WriteInt16LittleEndian(output[offset..], block.GetHeight(x, y));
        offset += sizeof(short);
      }
    }
    BinaryPrimitives.WriteUInt16LittleEndian(output[offset..], block.GridIndexX);
    offset += sizeof(ushort);
    BinaryPrimitives.WriteUInt16LittleEndian(output[offset..], block.GridIndexY);
    offset += sizeof(ushort);
    BinaryPrimitives.WriteInt16LittleEndian(output[offset..], checked((short)block.Tile.LongitudeDegrees));
    offset += sizeof(short);
    output[offset++] = unchecked((byte)checked((sbyte)block.Tile.LatitudeDegrees));
    if (offset != IoBlockDataSize) {
      throw new InvalidDataException($"Terrain block data is {offset} bytes, expected {IoBlockDataSize}.");
    }

    ushort crc = ComputeCrc(output[..IoBlockDataSize]);
    BinaryPrimitives.WriteUInt16LittleEndian(output[crcOffset..], crc);
    return packed;
  }

  private static TerrainElevationSample ReadOfficialElevation(double latitude, double longitude) {
    srtm.altresponce response = srtm.getAltitude(latitude, longitude, 20);
    return new TerrainElevationSample(
        response.alt,
        response.currenttype == srtm.tiletype.valid);
  }

  private static void ValidateBounds(TerrainBounds bounds) {
    if (!double.IsFinite(bounds.South)
        || !double.IsFinite(bounds.West)
        || !double.IsFinite(bounds.North)
        || !double.IsFinite(bounds.East)) {
      throw new ArgumentException("Terrain bounds must contain finite coordinates.", nameof(bounds));
    }
    if (bounds.South < -90 || bounds.North > 90 || bounds.South >= bounds.North) {
      throw new ArgumentOutOfRangeException(
          nameof(bounds), "South/north bounds must satisfy -90 ≤ south < north ≤ 90.");
    }
    if (bounds.West < -180 || bounds.East > 180 || bounds.West >= bounds.East) {
      throw new ArgumentOutOfRangeException(
          nameof(bounds), "West/east bounds must satisfy -180 ≤ west < east ≤ 180.");
    }
  }

  private static void ValidateSpacing(ushort spacingMeters) {
    if (spacingMeters is < MinimumSpacingMeters or > MaximumSpacingMeters) {
      throw new ArgumentOutOfRangeException(
          nameof(spacingMeters),
          $"Terrain grid spacing must be {MinimumSpacingMeters}–{MaximumSpacingMeters} metres.");
    }
  }

  private static TerrainCoordinate TileReference(TerrainTile tile) => new(
      checked(tile.LatitudeDegrees * 10_000_000),
      checked(tile.LongitudeDegrees * 10_000_000));

  private static int EastBlockCount(TerrainCoordinate reference, ushort spacingMeters) {
    TerrainCoordinate east = reference.AddOffsetMeters(
        0,
        2L * spacingMeters * TerrainGridBlockSizeY);
    east = east with { LongitudeE7 = checked(east.LongitudeE7 + 10_000_000) };
    (_, double eastDistance) = reference.DistanceNorthEastTo(east);
    return Math.Max(
        1,
        (int)(Math.Round(eastDistance) / (spacingMeters * TerrainGridBlockSpacingY)));
  }

  private static void TryDelete(string path) {
    try {
      if (File.Exists(path)) {
        File.Delete(path);
      }
    } catch {
      // Preserve the primary generation error. A stale uniquely named temporary file can be
      // identified safely and never replaces a previously complete DAT.
    }
  }

  internal readonly record struct TerrainCoordinate(int LatitudeE7, int LongitudeE7) {
    internal TerrainCoordinate AddOffsetMeters(long northMeters, long eastMeters) {
      double latitudeDelta = northMeters * LocationScalingFactorInverse;
      double longitudeDelta = eastMeters * LocationScalingFactorInverse
          / LongitudeScale((int)(LatitudeE7 + latitudeDelta / 2));
      return new TerrainCoordinate(
          checked(LatitudeE7 + (int)latitudeDelta),
          checked(LongitudeE7 + (int)longitudeDelta));
    }

    internal (double North, double East) DistanceNorthEastTo(TerrainCoordinate other) => (
        (other.LatitudeE7 - LatitudeE7) * LocationScalingFactor,
        LongitudeDifference(other.LongitudeE7, LongitudeE7)
            * LocationScalingFactor
            * LongitudeScale((LatitudeE7 + other.LatitudeE7) / 2));

    private static double LongitudeScale(int latitudeE7) =>
        Math.Max(Math.Cos(latitudeE7 * 1.0e-7 * Math.PI / 180.0), 0.01);

    private static int LongitudeDifference(int first, int second) {
      if ((first & int.MinValue) == (second & int.MinValue)) {
        return first - second;
      }
      long difference = (long)first - second;
      if (difference > 1_800_000_000L) {
        difference -= 3_600_000_000L;
      } else if (difference < -1_800_000_000L) {
        difference += 3_600_000_000L;
      }
      return (int)difference;
    }
  }

  private sealed class TerrainGridBlock {
    private readonly short[] _heights =
        new short[TerrainGridBlockSizeX * TerrainGridBlockSizeY];

    internal TerrainGridBlock(
        TerrainTile tile,
        TerrainCoordinate blockLocation,
        ushort spacingMeters) {
      Tile = tile;
      SpacingMeters = spacingMeters;
      TerrainCoordinate reference = TileReference(tile);
      (double north, double east) = reference.DistanceNorthEastTo(blockLocation);
      long indexX = (long)(Math.Round(north) / spacingMeters);
      long indexY = (long)(Math.Round(east) / spacingMeters);
      GridIndexX = checked((ushort)Math.Floor((double)indexX / TerrainGridBlockSpacingX));
      GridIndexY = checked((ushort)Math.Floor((double)indexY / TerrainGridBlockSpacingY));
      Origin = reference.AddOffsetMeters(
          (long)GridIndexX * TerrainGridBlockSpacingX * spacingMeters,
          (long)GridIndexY * TerrainGridBlockSpacingY * spacingMeters);
    }

    internal TerrainTile Tile { get; }
    internal TerrainCoordinate Origin { get; }
    internal int LatitudeE7 => Origin.LatitudeE7;
    internal int LongitudeE7 => Origin.LongitudeE7;
    internal ushort SpacingMeters { get; }
    internal ushort GridIndexX { get; }
    internal ushort GridIndexY { get; }
    internal ulong Bitmap { get; private set; }

    internal short GetHeight(int x, int y) => _heights[y * TerrainGridBlockSizeX + x];

    internal void SetHeight(int x, int y, short value) =>
        _heights[y * TerrainGridBlockSizeX + x] = value;

    internal bool IsValidSubgrid(int x, int y) {
      for (int xOffset = 0; xOffset < TerrainGridMavlinkSize; xOffset++) {
        for (int yOffset = 0; yOffset < TerrainGridMavlinkSize; yOffset++) {
          if (GetHeight(
                  x * TerrainGridMavlinkSize + xOffset,
                  y * TerrainGridMavlinkSize + yOffset) == 0) {
            return false;
          }
        }
      }
      return true;
    }

    internal void SetBitmapBit(int bitNumber) {
      if (bitNumber is < 0 or >= 56) {
        throw new ArgumentOutOfRangeException(nameof(bitNumber));
      }
      Bitmap |= 1UL << bitNumber;
    }
  }

}

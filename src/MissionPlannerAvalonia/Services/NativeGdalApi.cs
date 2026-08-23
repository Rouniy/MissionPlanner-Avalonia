using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace MissionPlannerAvalonia.Services;

/// <summary>
/// Minimal, optional binding to the stable GDAL C ABI. Official Mission Planner uses the old
/// Windows-only GDAL 2.3.2 C# package; resolving the C ABI at runtime keeps the Avalonia port
/// usable without GDAL while allowing current system GDAL builds on every supported OS.
/// </summary>
internal sealed class NativeGdalApi {
  private const uint OpenReadOnlyRaster = 0x00u | 0x02u | 0x40u;
  private const int Read = 0;
  private const int Byte = 1;
  private const int Bilinear = 1;
  private const int TraditionalGisOrder = 0;

  private readonly nint _library;
  private readonly GdalAllRegister _allRegister;
  private readonly GdalVersionInfo _versionInfo;
  private readonly GdalOpenEx _openEx;
  private readonly GdalClose _close;
  private readonly GdalAutoCreateWarpedVrt _autoCreateWarpedVrt;
  private readonly GdalGetInteger _getRasterXSize;
  private readonly GdalGetInteger _getRasterYSize;
  private readonly GdalGetInteger _getRasterCount;
  private readonly GdalGetGeoTransform _getGeoTransform;
  private readonly GdalGetHandle _getDatasetDriver;
  private readonly GdalGetString _getDriverShortName;
  private readonly GdalGetRasterBand _getRasterBand;
  private readonly GdalGetInteger _getRasterColorInterpretation;
  private readonly GdalGetHandle _getRasterColorTable;
  private readonly GdalGetColorEntryAsRgb _getColorEntryAsRgb;
  private readonly GdalGetHandle _getMaskBand;
  private readonly GdalGetInteger _getMaskFlags;
  private readonly GdalRasterIo _rasterIo;
  private readonly OsrNewSpatialReference _newSpatialReference;
  private readonly OsrImportFromEpsg _importFromEpsg;
  private readonly OsrSetAxisMappingStrategy _setAxisMappingStrategy;
  private readonly OsrExportToWkt _exportToWkt;
  private readonly OsrDestroySpatialReference _destroySpatialReference;
  private readonly VsiFree _vsiFree;
  private readonly CplErrorReset _errorReset;
  private readonly CplGetLastErrorMessage _getLastErrorMessage;

  private NativeGdalApi(nint library, string libraryPath) {
    _library = library;
    LibraryPath = libraryPath;
    _allRegister = Export<GdalAllRegister>("GDALAllRegister");
    _versionInfo = Export<GdalVersionInfo>("GDALVersionInfo");
    _openEx = Export<GdalOpenEx>("GDALOpenEx");
    _close = Export<GdalClose>("GDALClose");
    _autoCreateWarpedVrt = Export<GdalAutoCreateWarpedVrt>("GDALAutoCreateWarpedVRT");
    _getRasterXSize = Export<GdalGetInteger>("GDALGetRasterXSize");
    _getRasterYSize = Export<GdalGetInteger>("GDALGetRasterYSize");
    _getRasterCount = Export<GdalGetInteger>("GDALGetRasterCount");
    _getGeoTransform = Export<GdalGetGeoTransform>("GDALGetGeoTransform");
    _getDatasetDriver = Export<GdalGetHandle>("GDALGetDatasetDriver");
    _getDriverShortName = Export<GdalGetString>("GDALGetDriverShortName");
    _getRasterBand = Export<GdalGetRasterBand>("GDALGetRasterBand");
    _getRasterColorInterpretation =
        Export<GdalGetInteger>("GDALGetRasterColorInterpretation");
    _getRasterColorTable = Export<GdalGetHandle>("GDALGetRasterColorTable");
    _getColorEntryAsRgb = Export<GdalGetColorEntryAsRgb>("GDALGetColorEntryAsRGB");
    _getMaskBand = Export<GdalGetHandle>("GDALGetMaskBand");
    _getMaskFlags = Export<GdalGetInteger>("GDALGetMaskFlags");
    _rasterIo = Export<GdalRasterIo>("GDALRasterIO");
    _newSpatialReference = Export<OsrNewSpatialReference>("OSRNewSpatialReference");
    _importFromEpsg = Export<OsrImportFromEpsg>("OSRImportFromEPSG");
    _setAxisMappingStrategy =
        Export<OsrSetAxisMappingStrategy>("OSRSetAxisMappingStrategy");
    _exportToWkt = Export<OsrExportToWkt>("OSRExportToWkt");
    _destroySpatialReference =
        Export<OsrDestroySpatialReference>("OSRDestroySpatialReference");
    _vsiFree = Export<VsiFree>("VSIFree");
    _errorReset = Export<CplErrorReset>("CPLErrorReset");
    _getLastErrorMessage = Export<CplGetLastErrorMessage>("CPLGetLastErrorMsg");

    _allRegister();
    Version = PtrToString(_versionInfo("RELEASE_NAME"));
    WebMercatorWkt = CreateWebMercatorWkt();
  }

  internal string LibraryPath { get; }
  internal string Version { get; }
  internal string WebMercatorWkt { get; }

  internal static NativeGdalLoadResult TryLoad() {
    var errors = new List<string>();
    foreach (string candidate in LibraryCandidates()) {
      try {
        if (!NativeLibrary.TryLoad(candidate, out nint handle)) {
          continue;
        }
        try {
          return new NativeGdalLoadResult(
              new NativeGdalApi(handle, candidate),
              $"GDAL {candidate} loaded.");
        } catch (Exception ex) {
          NativeLibrary.Free(handle);
          errors.Add($"{candidate}: {ex.Message}");
        }
      } catch (Exception ex) {
        errors.Add($"{candidate}: {ex.Message}");
      }
    }

    string suffix = errors.Count == 0 ? "" : " " + string.Join("; ", errors.Take(3));
    return new NativeGdalLoadResult(
        null,
        "Native GDAL is not installed. Local GeoTIFF/DTED elevation still works without it; " +
        "install a current GDAL runtime or set MISSIONPLANNER_GDAL_LIBRARY to its exact path." +
        suffix);
  }

  internal static IReadOnlyList<string> LibraryCandidates() {
    var candidates = new List<string>();
    string? configured = Environment.GetEnvironmentVariable("MISSIONPLANNER_GDAL_LIBRARY");
    if (!string.IsNullOrWhiteSpace(configured)) {
      candidates.Add(configured.Trim());
    }

    if (OperatingSystem.IsWindows()) {
      candidates.AddRange([
        "gdal.dll", "gdal313.dll", "gdal312.dll", "gdal311.dll", "gdal310.dll",
        "gdal309.dll", "gdal308.dll", "gdal307.dll", "gdal306.dll", "gdal305.dll",
      ]);
    } else if (OperatingSystem.IsMacOS()) {
      candidates.AddRange([
        "libgdal.dylib",
        "/opt/homebrew/lib/libgdal.dylib",
        "/usr/local/lib/libgdal.dylib",
      ]);
    } else {
      candidates.Add("libgdal.so");
      for (int abi = 40; abi >= 30; abi--) {
        candidates.Add($"libgdal.so.{abi}");
      }
    }
    return candidates.Distinct(StringComparer.Ordinal).ToArray();
  }

  internal nint OpenRaster(string path) {
    ResetError();
    return _openEx(path, OpenReadOnlyRaster, nint.Zero, nint.Zero, nint.Zero);
  }

  internal nint CreateWebMercatorVrt(nint source) {
    ResetError();
    return _autoCreateWarpedVrt(
        source, null, WebMercatorWkt, Bilinear, 0, nint.Zero);
  }

  internal void Close(nint dataset) {
    if (dataset != nint.Zero) {
      _close(dataset);
    }
  }

  internal int RasterXSize(nint dataset) => _getRasterXSize(dataset);
  internal int RasterYSize(nint dataset) => _getRasterYSize(dataset);
  internal int RasterCount(nint dataset) => _getRasterCount(dataset);
  internal int GetGeoTransform(nint dataset, double[] transform) =>
      _getGeoTransform(dataset, transform);
  internal nint RasterBand(nint dataset, int band) => _getRasterBand(dataset, band);
  internal int ColorInterpretation(nint band) => _getRasterColorInterpretation(band);
  internal nint ColorTable(nint band) => _getRasterColorTable(band);
  internal nint MaskBand(nint band) => _getMaskBand(band);
  internal int MaskFlags(nint band) => _getMaskFlags(band);

  internal string DriverName(nint dataset) {
    nint driver = _getDatasetDriver(dataset);
    return driver == nint.Zero ? "Unknown" : PtrToString(_getDriverShortName(driver));
  }

  internal bool TryGetColor(nint table, int index, out NativeGdalColorEntry color) {
    color = default;
    return table != nint.Zero && _getColorEntryAsRgb(table, index, out color) != 0;
  }

  internal bool ReadByteBand(
      nint band,
      int xOffset,
      int yOffset,
      int xSize,
      int ySize,
      byte[] destination,
      int destinationWidth,
      int destinationHeight) {
    var pinned = GCHandle.Alloc(destination, GCHandleType.Pinned);
    try {
      ResetError();
      return _rasterIo(
          band,
          Read,
          xOffset,
          yOffset,
          xSize,
          ySize,
          pinned.AddrOfPinnedObject(),
          destinationWidth,
          destinationHeight,
          Byte,
          0,
          0) == 0;
    } finally {
      pinned.Free();
    }
  }

  internal string LastError() {
    string message = PtrToString(_getLastErrorMessage());
    return string.IsNullOrWhiteSpace(message) ? "unknown GDAL error" : message.Trim();
  }

  private string CreateWebMercatorWkt() {
    nint spatialReference = _newSpatialReference(null);
    if (spatialReference == nint.Zero) {
      throw new InvalidOperationException("OSRNewSpatialReference returned null.");
    }
    try {
      if (_importFromEpsg(spatialReference, 3857) != 0) {
        throw new InvalidOperationException("GDAL cannot initialize EPSG:3857: " + LastError());
      }
      _setAxisMappingStrategy(spatialReference, TraditionalGisOrder);
      if (_exportToWkt(spatialReference, out nint text) != 0 || text == nint.Zero) {
        throw new InvalidOperationException("GDAL cannot export EPSG:3857: " + LastError());
      }
      try {
        return PtrToString(text);
      } finally {
        _vsiFree(text);
      }
    } finally {
      _destroySpatialReference(spatialReference);
    }
  }

  private void ResetError() => _errorReset();

  private T Export<T>(string name) where T : Delegate =>
      Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

  private static string PtrToString(nint value) =>
      value == nint.Zero ? "" : Marshal.PtrToStringUTF8(value) ?? "";

  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void GdalAllRegister();
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate nint GdalVersionInfo([MarshalAs(UnmanagedType.LPUTF8Str)] string request);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate nint GdalOpenEx(
      [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
      uint flags,
      nint allowedDrivers,
      nint openOptions,
      nint siblingFiles);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void GdalClose(nint dataset);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate nint GdalAutoCreateWarpedVrt(
      nint source,
      [MarshalAs(UnmanagedType.LPUTF8Str)] string? sourceWkt,
      [MarshalAs(UnmanagedType.LPUTF8Str)] string destinationWkt,
      int resampling,
      double maximumError,
      nint options);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate int GdalGetInteger(nint handle);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate nint GdalGetHandle(nint handle);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate nint GdalGetString(nint handle);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate int GdalGetGeoTransform(nint dataset, [Out] double[] transform);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate nint GdalGetRasterBand(nint dataset, int band);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate int GdalGetColorEntryAsRgb(
      nint table, int index, out NativeGdalColorEntry color);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate int GdalRasterIo(
      nint band,
      int readWrite,
      int xOffset,
      int yOffset,
      int xSize,
      int ySize,
      nint buffer,
      int bufferWidth,
      int bufferHeight,
      int bufferType,
      int pixelSpace,
      int lineSpace);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate nint OsrNewSpatialReference(
      [MarshalAs(UnmanagedType.LPUTF8Str)] string? wkt);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate int OsrImportFromEpsg(nint spatialReference, int code);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void OsrSetAxisMappingStrategy(nint spatialReference, int strategy);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate int OsrExportToWkt(nint spatialReference, out nint text);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void OsrDestroySpatialReference(nint spatialReference);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void VsiFree(nint memory);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate void CplErrorReset();
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate nint CplGetLastErrorMessage();
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeGdalColorEntry {
  internal short Red;
  internal short Green;
  internal short Blue;
  internal short Alpha;
}

internal sealed record NativeGdalLoadResult(NativeGdalApi? Api, string Status);

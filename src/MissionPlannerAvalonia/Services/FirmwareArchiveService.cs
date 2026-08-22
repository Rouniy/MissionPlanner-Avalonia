using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace MissionPlannerAvalonia.Services;

internal sealed record FirmwareArchiveProgress(int Completed, int Total, string Item);

internal sealed record FirmwareArchiveResult(
    string Directory, Uri ManifestSource, int FileCount, int FailedFiles, long BytesDownloaded);

/// <summary>
/// Cross-platform implementation of Mission Planner temp.cs' "rip all fw" workflow.
/// The official version writes directly into History while downloads are still in progress. This
/// implementation builds a new sibling directory and publishes it with one rename, so cancellation
/// or a failed mirror cannot leave an archive that looks complete.
/// </summary>
internal sealed class FirmwareArchiveService {
  internal const int MaxParallelDownloads = 4;
  internal const int MaxManifestBytes = 8 * 1024 * 1024;
  internal const long MaxFirmwareBytes = 256L * 1024 * 1024;

  internal static readonly IReadOnlyList<Uri> OfficialManifestUris = new[] {
      new Uri("https://github.com/ArduPilot/binary/raw/master/Firmware/firmware2.xml"),
      new Uri("https://firmware.ardupilot.org/Tools/MissionPlanner/Firmware/firmware2.xml"),
  };

  private readonly HttpClient _http;

  internal FirmwareArchiveService(HttpClient http) =>
      _http = http ?? throw new ArgumentNullException(nameof(http));

  internal async Task<FirmwareArchiveResult> DownloadAsync(
      IReadOnlyList<Uri> manifestUris,
      string destinationDirectory,
      IProgress<FirmwareArchiveProgress>? progress,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(manifestUris);
    if (manifestUris.Count == 0) {
      throw new ArgumentException("At least one firmware manifest URI is required.",
          nameof(manifestUris));
    }
    if (string.IsNullOrWhiteSpace(destinationDirectory)) {
      throw new ArgumentException("An archive destination is required.",
          nameof(destinationDirectory));
    }

    string destination = Path.GetFullPath(destinationDirectory);
    string? parent = Path.GetDirectoryName(destination);
    if (parent == null || !Directory.Exists(parent)) {
      throw new DirectoryNotFoundException("The selected archive parent directory does not exist.");
    }
    if (Directory.Exists(destination) || File.Exists(destination)) {
      throw new IOException("The firmware archive destination already exists.");
    }

    (Uri source, byte[] bytes) = await DownloadManifestAsync(
        manifestUris, cancellationToken).ConfigureAwait(false);
    XDocument manifest = ParseManifest(bytes);
    List<FirmwareReference> references = ExtractReferences(manifest);
    if (references.Count == 0) {
      throw new InvalidDataException(
          "The firmware manifest contains no absolute HTTP(S) firmware URLs.");
    }

    var downloads = new Dictionary<string, FirmwareDownload>(StringComparer.Ordinal);
    foreach (FirmwareReference reference in references) {
      string key = reference.Uri.AbsoluteUri;
      if (!downloads.TryGetValue(key, out FirmwareDownload? download)) {
        download = new FirmwareDownload(reference.Uri, ArchiveRelativePath(reference.Uri));
        downloads.Add(key, download);
      }
      download.Elements.Add(reference.Element);
    }
    if (downloads.Values.Select(download => download.RelativePath)
        .Distinct(StringComparer.OrdinalIgnoreCase).Count() != downloads.Count) {
      throw new InvalidDataException("Two firmware URLs produced the same archive path.");
    }

    string temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
    Directory.CreateDirectory(temporary);
    long bytesDownloaded = 0;
    int completed = 0;
    try {
      await Parallel.ForEachAsync(
          downloads.Values,
          new ParallelOptions {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = MaxParallelDownloads,
          },
          async (download, token) => {
            string output = Path.Combine(temporary, download.RelativePath);
            string? outputParent = Path.GetDirectoryName(output);
            if (outputParent == null) {
              throw new InvalidDataException("A firmware URL produced an invalid archive path.");
            }
            Directory.CreateDirectory(outputParent);
            try {
              FirmwareFileResult result = await DownloadFileAsync(download.Uri, output, token)
                  .ConfigureAwait(false);
              download.Sha256 = result.Sha256;
              Interlocked.Add(ref bytesDownloaded, result.Bytes);
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
              throw;
            } catch (Exception ex) {
              download.Error = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
            }
            int current = Interlocked.Increment(ref completed);
            progress?.Report(new FirmwareArchiveProgress(
                current, downloads.Count, download.RelativePath.Replace(
                    Path.DirectorySeparatorChar, '/')));
          }).ConfigureAwait(false);

      cancellationToken.ThrowIfCancellationRequested();
      FirmwareDownload[] successful = downloads.Values
          .Where(download => download.Error == null && download.Sha256 != null)
          .OrderBy(download => download.RelativePath, StringComparer.Ordinal)
          .ToArray();
      FirmwareDownload[] failed = downloads.Values
          .Where(download => download.Error != null || download.Sha256 == null)
          .OrderBy(download => download.Uri.AbsoluteUri, StringComparer.Ordinal)
          .ToArray();
      if (successful.Length == 0) {
        throw new HttpRequestException(
            "None of the firmware URLs in the official manifest could be downloaded.");
      }
      foreach (FirmwareDownload download in successful) {
        string relative = download.RelativePath.Replace(Path.DirectorySeparatorChar, '/');
        foreach (XElement element in download.Elements) {
          element.Value = relative;
        }
      }

      string manifestPath = Path.Combine(temporary, "firmware2.xml");
      await using (var stream = new FileStream(
          manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
          bufferSize: 16 * 1024, FileOptions.Asynchronous)) {
        var settings = new XmlWriterSettings {
          Async = true,
          Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
          Indent = true,
          NewLineChars = "\n",
        };
        using XmlWriter writer = XmlWriter.Create(stream, settings);
        await manifest.SaveAsync(writer, cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
      }

      await File.WriteAllLinesAsync(
          Path.Combine(temporary, "checksums.sha256"),
          successful.Select(download =>
              $"{download.Sha256}  {download.RelativePath.Replace(Path.DirectorySeparatorChar, '/')}"),
          cancellationToken).ConfigureAwait(false);
      var report = new List<string> {
          "Mission Planner firmware archive",
          "Manifest: " + source,
          "Downloaded: " + successful.Length,
          "Unavailable: " + failed.Length,
          "Bytes: " + Interlocked.Read(ref bytesDownloaded),
          "",
          "Unavailable URLs remain unchanged in firmware2.xml:",
      };
      report.AddRange(failed.Select(download =>
          download.Uri.AbsoluteUri + " | " + (download.Error ?? "download produced no digest")));
      await File.WriteAllLinesAsync(
          Path.Combine(temporary, "archive-report.txt"), report, cancellationToken)
          .ConfigureAwait(false);

      cancellationToken.ThrowIfCancellationRequested();
      Directory.Move(temporary, destination);
      return new FirmwareArchiveResult(
          destination, source, successful.Length, failed.Length,
          Interlocked.Read(ref bytesDownloaded));
    } catch {
      DeleteOwnedTemporaryDirectory(temporary);
      throw;
    }
  }

  private async Task<(Uri Source, byte[] Bytes)> DownloadManifestAsync(
      IReadOnlyList<Uri> candidates, CancellationToken cancellationToken) {
    var errors = new List<string>();
    foreach (Uri candidate in candidates) {
      cancellationToken.ThrowIfCancellationRequested();
      if (!IsHttps(candidate)) {
        errors.Add(candidate + ": only absolute HTTPS manifest URLs are accepted");
        continue;
      }
      try {
        byte[] bytes = await DownloadBytesAsync(
            candidate, MaxManifestBytes, cancellationToken).ConfigureAwait(false);
        return (candidate, bytes);
      } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
        throw;
      } catch (Exception ex) {
        errors.Add(candidate + ": " + ex.Message);
      }
    }
    throw new HttpRequestException(
        "No official firmware manifest mirror succeeded. " + string.Join(" | ", errors));
  }

  private async Task<FirmwareFileResult> DownloadFileAsync(
      Uri uri, string output, CancellationToken cancellationToken) {
    if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) {
      var secure = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
      try {
        return await DownloadFileFromUriAsync(secure, output, cancellationToken)
            .ConfigureAwait(false);
      } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
        throw;
      } catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) {
        DeleteOwnedPartialFile(output);
      }
    }
    return await DownloadFileFromUriAsync(uri, output, cancellationToken).ConfigureAwait(false);
  }

  private async Task<FirmwareFileResult> DownloadFileFromUriAsync(
      Uri uri, string output, CancellationToken cancellationToken) {
    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
    using HttpResponseMessage response = await _http.SendAsync(
        request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    response.EnsureSuccessStatusCode();
    long? declared = response.Content.Headers.ContentLength;
    if (declared is > MaxFirmwareBytes) {
      throw new InvalidDataException(
          $"Firmware response exceeds the {MaxFirmwareBytes}-byte safety limit: {uri}");
    }

    try {
      await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken)
          .ConfigureAwait(false);
      await using var destination = new FileStream(
          output, FileMode.CreateNew, FileAccess.Write, FileShare.None,
          bufferSize: 64 * 1024, FileOptions.Asynchronous);
      using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      var buffer = new byte[64 * 1024];
      long total = 0;
      while (true) {
        int count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (count == 0) {
          break;
        }
        total += count;
        if (total > MaxFirmwareBytes) {
          throw new InvalidDataException(
              $"Firmware response exceeds the {MaxFirmwareBytes}-byte safety limit: {uri}");
        }
        digest.AppendData(buffer, 0, count);
        await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken)
            .ConfigureAwait(false);
      }
      return new FirmwareFileResult(
          total, Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant());
    } catch {
      DeleteOwnedPartialFile(output);
      throw;
    }
  }

  private async Task<byte[]> DownloadBytesAsync(
      Uri uri, int maximumBytes, CancellationToken cancellationToken) {
    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
    using HttpResponseMessage response = await _http.SendAsync(
        request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    response.EnsureSuccessStatusCode();
    if (response.Content.Headers.ContentLength is long length && length > maximumBytes) {
      throw new InvalidDataException(
          $"Response exceeds the {maximumBytes}-byte safety limit: {uri}");
    }
    await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken)
        .ConfigureAwait(false);
    using var destination = new MemoryStream();
    var buffer = new byte[32 * 1024];
    while (true) {
      int count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
      if (count == 0) {
        break;
      }
      if (destination.Length + count > maximumBytes) {
        throw new InvalidDataException(
            $"Response exceeds the {maximumBytes}-byte safety limit: {uri}");
      }
      await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken)
          .ConfigureAwait(false);
    }
    return destination.ToArray();
  }

  private static XDocument ParseManifest(byte[] bytes) {
    using var stream = new MemoryStream(bytes, writable: false);
    var settings = new XmlReaderSettings {
      DtdProcessing = DtdProcessing.Prohibit,
      XmlResolver = null,
      MaxCharactersInDocument = MaxManifestBytes,
      IgnoreComments = false,
    };
    using XmlReader reader = XmlReader.Create(stream, settings);
    XDocument document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    if (document.Root == null) {
      throw new InvalidDataException("The firmware manifest has no root element.");
    }
    return document;
  }

  private static List<FirmwareReference> ExtractReferences(XDocument manifest) {
    var references = new List<FirmwareReference>();
    foreach (XElement element in manifest.Descendants().Where(element =>
                 element.Name.LocalName.StartsWith("url", StringComparison.OrdinalIgnoreCase))) {
      string value = element.Value.Trim();
      if (value.Length == 0) {
        continue;
      }
      if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || !IsFirmwareUri(uri)) {
        throw new InvalidDataException(
            $"Firmware manifest field {element.Name.LocalName} is not an absolute HTTP(S) URL.");
      }
      references.Add(new FirmwareReference(element, uri));
    }
    return references;
  }

  private static bool IsHttps(Uri uri) =>
      uri.IsAbsoluteUri
      && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
      && !string.IsNullOrWhiteSpace(uri.Host)
      && string.IsNullOrEmpty(uri.UserInfo);

  private static bool IsFirmwareUri(Uri uri) =>
      uri.IsAbsoluteUri
      && (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
          || uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
      && !string.IsNullOrWhiteSpace(uri.Host)
      && string.IsNullOrEmpty(uri.UserInfo);

  internal static string ArchiveRelativePath(Uri uri) {
    if (!IsFirmwareUri(uri)) {
      throw new ArgumentException("Only absolute HTTP(S) firmware URLs are accepted.", nameof(uri));
    }
    string host = SanitizeSegment(uri.IdnHost);
    string[] pathSegments = uri.AbsolutePath
        .Split('/', StringSplitOptions.RemoveEmptyEntries)
        .Select(segment => SanitizeSegment(Uri.UnescapeDataString(segment)))
        .ToArray();
    string fileName = pathSegments.Length == 0 ? "firmware.bin" : pathSegments[^1];
    string hash = Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(uri.AbsoluteUri)))[..12].ToLowerInvariant();
    fileName = hash + "-" + fileName;
    return Path.Combine("files", host, fileName);
  }

  private static string SanitizeSegment(string value) {
    string source = string.IsNullOrWhiteSpace(value) || value is "." or ".." ? "_" : value;
    var result = new StringBuilder(Math.Min(source.Length, 96));
    foreach (char character in source) {
      if (result.Length == 96) {
        break;
      }
      bool invalid = char.IsControl(character)
          || character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*';
      result.Append(invalid ? '_' : character);
    }
    string sanitized = result.ToString().Trim().TrimEnd('.');
    return sanitized.Length == 0 || sanitized is "." or ".." ? "_" : sanitized;
  }

  private static void DeleteOwnedTemporaryDirectory(string path) {
    try {
      if (Directory.Exists(path)
          && Path.GetFileName(path).Contains(".partial-", StringComparison.Ordinal)) {
        Directory.Delete(path, recursive: true);
      }
    } catch {
      // The primary exception is more useful. A locked partial directory retains the explicit
      // .partial marker and can never be mistaken for a completed archive.
    }
  }

  private static void DeleteOwnedPartialFile(string path) {
    try {
      if (File.Exists(path)) {
        File.Delete(path);
      }
    } catch {
    }
  }

  private sealed record FirmwareReference(XElement Element, Uri Uri);
  private sealed record FirmwareFileResult(long Bytes, string Sha256);

  private sealed class FirmwareDownload(Uri uri, string relativePath) {
    internal Uri Uri { get; } = uri;
    internal string RelativePath { get; } = relativePath;
    internal List<XElement> Elements { get; } = new();
    internal string? Sha256 { get; set; }
    internal string? Error { get; set; }
  }
}

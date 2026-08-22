using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MissionPlannerAvalonia.Services;

internal sealed class FrameDefaultCatalogService {
  internal const string CatalogRoot = "Tools/Frame_params";
  private const string ApiRoot = "https://api.github.com/repos/ArduPilot/ardupilot/contents/";
  private const string RawRoot = "https://raw.githubusercontent.com/ArduPilot/ardupilot/master/";
  private static readonly HttpClient SharedClient = CreateClient();

  private readonly HttpClient _http;
  private readonly SemaphoreSlim _catalogGate = new(1, 1);
  private IReadOnlyList<FrameDefaultCatalogItem>? _cachedCatalog;

  internal static FrameDefaultCatalogService Shared { get; } = new();

  internal FrameDefaultCatalogService(HttpClient? httpClient = null) =>
      _http = httpClient ?? SharedClient;

  internal async Task<IReadOnlyList<FrameDefaultCatalogItem>> ListAsync(
      CancellationToken cancellationToken, bool forceRefresh = false) {
    if (!forceRefresh && _cachedCatalog is { } cached) {
      return cached;
    }
    await _catalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      if (!forceRefresh && _cachedCatalog is { } current) {
        return current;
      }
      IReadOnlyList<FrameDefaultCatalogItem> loaded =
          await LoadCatalogAsync(cancellationToken).ConfigureAwait(false);
      _cachedCatalog = loaded;
      return loaded;
    } finally {
      _catalogGate.Release();
    }
  }

  private async Task<IReadOnlyList<FrameDefaultCatalogItem>> LoadCatalogAsync(
      CancellationToken cancellationToken) {
    var pending = new Queue<string>();
    var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var files = new List<FrameDefaultCatalogItem>();
    pending.Enqueue(CatalogRoot);

    while (pending.TryDequeue(out string? directory)) {
      cancellationToken.ThrowIfCancellationRequested();
      if (!visited.Add(directory)) {
        continue;
      }

      using var request = CreateRequest(ApiRoot + EscapePath(directory));
      using HttpResponseMessage response = await _http.SendAsync(
          request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
      response.EnsureSuccessStatusCode();
      await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken)
          .ConfigureAwait(false);
      var entries = await JsonSerializer.DeserializeAsync<List<GitHubContentEntry>>(
          body, cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];

      foreach (GitHubContentEntry entry in entries) {
        string path = NormalizeEntryPath(entry.Path);
        if (entry.Type.Equals("dir", StringComparison.OrdinalIgnoreCase)) {
          pending.Enqueue(path);
        } else if (entry.Type.Equals("file", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith(".param", StringComparison.OrdinalIgnoreCase)) {
          files.Add(new FrameDefaultCatalogItem(
              string.IsNullOrWhiteSpace(entry.Name) ? Path.GetFileName(path) : entry.Name,
              NormalizeParamPath(path)));
        }
      }
    }

    return files.GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
        .ToList();
  }

  internal async Task<byte[]> DownloadAsync(
      string catalogPath, CancellationToken cancellationToken) {
    string normalized = NormalizeParamPath(catalogPath);
    using var request = CreateRequest(RawRoot + EscapePath(normalized));
    using HttpResponseMessage response = await _http.SendAsync(
        request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
  }

  internal static string GetCachePath(string cacheRoot, string catalogPath) {
    string normalized = NormalizeParamPath(catalogPath);
    string root = Path.GetFullPath(Path.Combine(cacheRoot, "frame-defaults"));
    string result = Path.GetFullPath(Path.Combine(
        root, Path.Combine(normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))));
    string prefix = root.EndsWith(Path.DirectorySeparatorChar)
        ? root
        : root + Path.DirectorySeparatorChar;
    if (!result.StartsWith(prefix, StringComparison.Ordinal)) {
      throw new InvalidDataException("Frame-default cache path escapes its cache directory.");
    }
    return result;
  }

  internal static string NormalizeParamPath(string path) {
    string normalized = NormalizeEntryPath(path);
    if (!normalized.EndsWith(".param", StringComparison.OrdinalIgnoreCase)) {
      throw new InvalidDataException("Frame-default files must use the .param extension.");
    }
    return normalized;
  }

  private static string NormalizeEntryPath(string? path) {
    if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains('\\')) {
      throw new InvalidDataException("GitHub returned an invalid frame-default path.");
    }
    string[] parts = path.Split('/');
    if (parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or "..")) {
      throw new InvalidDataException("GitHub returned an unsafe frame-default path.");
    }
    string normalized = string.Join('/', parts);
    if (!normalized.StartsWith(CatalogRoot + "/", StringComparison.Ordinal)
        && !normalized.Equals(CatalogRoot, StringComparison.Ordinal)) {
      throw new InvalidDataException("GitHub returned a path outside Tools/Frame_params.");
    }
    return normalized;
  }

  private static string EscapePath(string path) => string.Join('/',
      path.Split('/').Select(Uri.EscapeDataString));

  private static HttpRequestMessage CreateRequest(string uri) {
    var request = new HttpRequestMessage(HttpMethod.Get, uri);
    request.Headers.UserAgent.ParseAdd("MissionPlanner-Avalonia/frame-defaults");
    request.Headers.Accept.ParseAdd("application/vnd.github+json");
    return request;
  }

  private static HttpClient CreateClient() => new() { Timeout = TimeSpan.FromSeconds(30) };

  private sealed class GitHubContentEntry {
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";
  }
}

internal sealed record FrameDefaultCatalogItem(string Name, string Path);

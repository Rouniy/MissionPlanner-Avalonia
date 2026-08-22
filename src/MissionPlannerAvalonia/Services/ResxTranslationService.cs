using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace MissionPlannerAvalonia.Services;

public sealed record TranslationCulture(string Name, string DisplayName) {
  public string Label => $"{DisplayName} ({Name})";
}

public readonly record struct TranslationIdentity(string RelativePath, string Key);

public sealed record ResxTranslationEntry(
    string RelativePath,
    string Key,
    string SourceText,
    string Translation,
    string? Comment,
    bool HasExistingTranslation);

public sealed record ResxTranslationProject(
    string SourceRoot,
    string Culture,
    IReadOnlyList<ResxTranslationEntry> Entries,
    int ResourceFiles,
    IReadOnlyList<string> Warnings);

public sealed record ResxTranslationExportResult(
    string OutputRoot,
    int ResourceFiles,
    int TranslatedEntries,
    int OverwrittenFiles,
    string? BackupDirectory,
    string ResumeHtmlPath);

public static partial class ResxTranslationService {
  // Mission Planner resources originate on Windows. Keep their identities stable across
  // platforms so a Linux checkout cannot export two paths that collide on Windows.
  private static readonly StringComparer RelativePathComparer = StringComparer.OrdinalIgnoreCase;

  private static readonly IEqualityComparer<TranslationIdentity> IdentityComparer =
      new TranslationIdentityEqualityComparer();

  private static readonly HashSet<string> IgnoredDirectoryNames = new(
      [".git", ".backup", "bin", "obj", "translation"],
      StringComparer.OrdinalIgnoreCase);

  private static readonly IReadOnlyList<TranslationCulture> KnownCultures = CultureInfo
      .GetCultures(CultureTypes.AllCultures)
      .Where(item => !string.IsNullOrWhiteSpace(item.Name))
      .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
      .Select(group => group.First())
      .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
      .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
      .Select(item => new TranslationCulture(item.Name, item.DisplayName))
      .ToArray();

  private static readonly string[] CultureSuffixes = KnownCultures
      .Select(item => "." + item.Name)
      .OrderByDescending(item => item.Length)
      .ToArray();

  public static IReadOnlyList<TranslationCulture> Cultures => KnownCultures;

  public static ResxTranslationProject Load(
      string sourceRoot,
      string culture,
      CancellationToken cancellationToken = default) {
    string root = ValidateDirectory(sourceRoot, nameof(sourceRoot));
    string normalizedCulture = ValidateCulture(culture);
    var warnings = new List<string>();
    var entries = new List<ResxTranslationEntry>();
    var identities = new HashSet<TranslationIdentity>(IdentityComparer);
    int resourceFiles = 0;

    string[] discoveredFiles = DiscoverNeutralResources(root, cancellationToken)
        .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal)
        .ToArray();
    var discoveredByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (string path in discoveredFiles) {
      if (!discoveredByPath.TryAdd(Path.GetFullPath(path), path)) {
        warnings.Add($"Case-colliding resource path ignored: {NormalizeRelativePath(Path.GetRelativePath(root, path))}");
      }
    }
    string[] files = discoveredByPath.Values
        .Where(path => !IsLocalizedResource(path))
        .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase)
        .ToArray();

    foreach (string sourcePath in files) {
      cancellationToken.ThrowIfCancellationRequested();
      string relativePath = NormalizeRelativePath(Path.GetRelativePath(root, sourcePath));
      IReadOnlyDictionary<string, ResxValue> sourceValues;
      try {
        sourceValues = ReadStringResources(sourcePath);
      } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException
                                   or InvalidDataException) {
        warnings.Add($"{relativePath}: {ex.Message}");
        continue;
      }

      string targetPath = LocalizedPath(sourcePath, normalizedCulture);
      if (discoveredByPath.TryGetValue(Path.GetFullPath(targetPath), out string? actualTargetPath)) {
        targetPath = actualTargetPath;
      }
      IReadOnlyDictionary<string, ResxValue> targetValues = new Dictionary<string, ResxValue>();
      if (File.Exists(targetPath)) {
        try {
          targetValues = ReadStringResources(targetPath);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException
                                     or InvalidDataException) {
          warnings.Add($"{NormalizeRelativePath(Path.GetRelativePath(root, targetPath))}: {ex.Message}");
        }
      }

      int before = entries.Count;
      foreach ((string key, ResxValue value) in sourceValues.OrderBy(item => item.Key, StringComparer.Ordinal)) {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsTranslatable(Path.GetFileName(sourcePath), key)) {
          continue;
        }
        var identity = new TranslationIdentity(relativePath, key);
        if (!identities.Add(identity)) {
          warnings.Add($"Duplicate resource identity ignored: {relativePath} / {key}");
          continue;
        }
        bool hasTranslation = targetValues.TryGetValue(key, out ResxValue? target);
        target ??= value;
        entries.Add(new ResxTranslationEntry(
            relativePath,
            key,
            value.Value,
            hasTranslation ? target.Value : value.Value,
            hasTranslation ? target.Comment ?? value.Comment : value.Comment,
            hasTranslation));
      }
      if (entries.Count != before) {
        resourceFiles++;
      }
    }

    if (resourceFiles == 0) {
      throw new InvalidDataException(
          "No neutral .resx files with Mission Planner translatable string keys were found.");
    }
    return new ResxTranslationProject(root, normalizedCulture, entries, resourceFiles, warnings);
  }

  public static ResxTranslationExportResult Export(
      string outputRoot,
      string culture,
      IReadOnlyCollection<ResxTranslationEntry> entries,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(entries);
    string normalizedCulture = ValidateCulture(culture);
    ResxTranslationEntry[] snapshot = entries
        .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.Key, StringComparer.Ordinal)
        .ToArray();
    ValidateEntries(snapshot);
    string root = ValidateOutputDirectory(outputRoot);

    var resourceGroups = snapshot
        .GroupBy(item => NormalizeRelativePath(item.RelativePath), RelativePathComparer)
        .ToArray();
    var outputs = resourceGroups
        .Select(group => LocalizedRelativePath(group.Key, normalizedCulture))
        .Append("output.html")
        .Distinct(RelativePathComparer)
        .ToArray();
    string? backupDirectory = null;
    int overwritten = 0;
    foreach (string relativeOutput in outputs) {
      cancellationToken.ThrowIfCancellationRequested();
      string destination = ContainedPath(root, relativeOutput);
      if (!File.Exists(destination)) {
        continue;
      }
      backupDirectory ??= Path.Combine(
          root,
          ".backup",
          DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
              + "-" + Guid.NewGuid().ToString("N")[..8]);
      string backupRootRelative = NormalizeRelativePath(Path.GetRelativePath(root, backupDirectory));
      string backup = ContainedPath(root, backupRootRelative + "/" + relativeOutput);
      Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
      File.Copy(destination, backup, overwrite: false);
      overwritten++;
    }

    foreach (IGrouping<string, ResxTranslationEntry> group in resourceGroups) {
      cancellationToken.ThrowIfCancellationRequested();
      string relativeOutput = LocalizedRelativePath(group.Key, normalizedCulture);
      string destination = ContainedPath(root, relativeOutput);
      XDocument document = CreateResx(group.Where(
          item => !string.Equals(item.SourceText, item.Translation, StringComparison.Ordinal)));
      WriteXmlAtomically(destination, document);
    }

    string htmlPath = ContainedPath(root, "output.html");
    WriteTextAtomically(htmlPath, BuildResumeHtml(normalizedCulture, snapshot));
    return new ResxTranslationExportResult(
        root,
        resourceGroups.Length,
        snapshot.Count(item => !string.Equals(item.SourceText, item.Translation, StringComparison.Ordinal)),
        overwritten,
        backupDirectory,
        htmlPath);
  }

  public static IReadOnlyDictionary<TranslationIdentity, string> ImportResumeHtml(string path) {
    string fullPath = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
    if (!File.Exists(fullPath)) {
      throw new FileNotFoundException("Translation resume HTML was not found.", fullPath);
    }
    if (new FileInfo(fullPath).Length > 100 * 1024 * 1024) {
      throw new InvalidDataException("Translation resume HTML exceeds the 100 MiB safety limit.");
    }

    string html = File.ReadAllText(fullPath, Encoding.UTF8);
    var values = new Dictionary<TranslationIdentity, string>(IdentityComparer);
    foreach (Match match in ResumeRowRegex().Matches(html)) {
      string relativePath = DecodeHtmlCell(match.Groups[1].Value);
      string key = DecodeHtmlCell(match.Groups[2].Value);
      if (string.Equals(relativePath, "File", StringComparison.OrdinalIgnoreCase)
          && string.Equals(key, "Key", StringComparison.OrdinalIgnoreCase)) {
        continue;
      }
      if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(key)) {
        continue;
      }
      values[new TranslationIdentity(NormalizeRelativePath(relativePath), key)] =
          DecodeHtmlCell(match.Groups[3].Value);
    }
    return values;
  }

  public static string BuildCsv(IEnumerable<ResxTranslationEntry> entries) {
    ArgumentNullException.ThrowIfNull(entries);
    var output = new StringBuilder("File,Key,English,Translation\r\n");
    foreach (ResxTranslationEntry entry in entries) {
      output.Append(Csv(entry.RelativePath)).Append(',')
          .Append(Csv(entry.Key)).Append(',')
          .Append(Csv(entry.SourceText)).Append(',')
          .Append(Csv(entry.Translation)).Append("\r\n");
    }
    return output.ToString();
  }

  internal static string LocalizedRelativePath(string relativePath, string culture) {
    string normalized = NormalizeRelativePath(relativePath);
    if (Path.IsPathRooted(normalized) || normalized.Split('/').Any(part => part is ".." or "." or "")) {
      throw new InvalidDataException("Resource path must be a safe relative path.");
    }
    if (!normalized.EndsWith(".resx", StringComparison.OrdinalIgnoreCase)) {
      throw new InvalidDataException("Resource path must end in .resx.");
    }
    string? directory = Path.GetDirectoryName(normalized.Replace('/', Path.DirectorySeparatorChar));
    string fileName = Path.GetFileNameWithoutExtension(normalized) + "." + ValidateCulture(culture) + ".resx";
    return NormalizeRelativePath(string.IsNullOrEmpty(directory) ? fileName : Path.Combine(directory, fileName));
  }

  internal static bool IsTranslatable(string fileName, string key) {
    if (string.Equals(fileName, "Strings.resx", StringComparison.OrdinalIgnoreCase)) {
      return true;
    }
    return key.EndsWith(".ToolTip", StringComparison.Ordinal)
        || key.EndsWith(".Text", StringComparison.Ordinal)
        || key.EndsWith("HeaderText", StringComparison.Ordinal)
        || key.EndsWith("ToolTipText", StringComparison.Ordinal);
  }

  internal static bool ResumeFileMatches(string importedFile, string sourceRelativePath) {
    string imported = NormalizeRelativePath(importedFile);
    string source = NormalizeRelativePath(sourceRelativePath);
    if (RelativePathComparer.Equals(imported, source)) {
      return true;
    }
    string sourceFile = Path.GetFileName(source);
    if (string.Equals(Path.GetFileName(imported), sourceFile, StringComparison.OrdinalIgnoreCase)) {
      return true;
    }

    string sourceStem = source[..^".resx".Length].Replace('/', '.');
    string resourceName = sourceStem + ".resources";
    return string.Equals(imported, resourceName, StringComparison.OrdinalIgnoreCase)
        || imported.EndsWith("." + resourceName, StringComparison.OrdinalIgnoreCase);
  }

  private static string ValidateDirectory(string value, string parameterName) {
    if (string.IsNullOrWhiteSpace(value)) {
      throw new ArgumentException("A source directory is required.", parameterName);
    }
    string fullPath = Path.GetFullPath(value);
    if (!Directory.Exists(fullPath)) {
      throw new DirectoryNotFoundException($"Resource source directory does not exist: {fullPath}");
    }
    return fullPath;
  }

  private static string ValidateOutputDirectory(string value) {
    if (string.IsNullOrWhiteSpace(value)) {
      throw new ArgumentException("An output directory is required.", nameof(value));
    }
    string fullPath = Path.GetFullPath(value);
    if (File.Exists(fullPath)) {
      throw new IOException($"The output path is a file: {fullPath}");
    }
    Directory.CreateDirectory(fullPath);
    return fullPath;
  }

  private static string ValidateCulture(string culture) {
    if (string.IsNullOrWhiteSpace(culture)) {
      throw new ArgumentException("Select a non-invariant target culture.", nameof(culture));
    }
    try {
      CultureInfo info = CultureInfo.GetCultureInfo(culture.Trim());
      if (string.IsNullOrWhiteSpace(info.Name)) {
        throw new ArgumentException("Select a non-invariant target culture.", nameof(culture));
      }
      return info.Name;
    } catch (CultureNotFoundException ex) {
      throw new ArgumentException($"Unknown target culture: {culture}", nameof(culture), ex);
    }
  }

  private static IEnumerable<string> DiscoverNeutralResources(
      string root,
      CancellationToken cancellationToken) {
    var pending = new Stack<string>();
    pending.Push(root);
    while (pending.Count != 0) {
      cancellationToken.ThrowIfCancellationRequested();
      string directory = pending.Pop();
      foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                   .Where(path => string.Equals(
                       Path.GetExtension(path), ".resx", StringComparison.OrdinalIgnoreCase))) {
        cancellationToken.ThrowIfCancellationRequested();
        yield return file;
      }
      foreach (string child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                   .OrderByDescending(path => path, RelativePathComparer)) {
        cancellationToken.ThrowIfCancellationRequested();
        if (IgnoredDirectoryNames.Contains(Path.GetFileName(child))) {
          continue;
        }
        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) {
          continue;
        }
        pending.Push(child);
      }
    }
  }

  private static bool IsLocalizedResource(string path) {
    string stem = Path.GetFileNameWithoutExtension(path);
    return CultureSuffixes.Any(suffix => stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
  }

  private static string LocalizedPath(string sourcePath, string culture) =>
      Path.Combine(
          Path.GetDirectoryName(sourcePath)!,
          Path.GetFileNameWithoutExtension(sourcePath) + "." + culture + ".resx");

  private static IReadOnlyDictionary<string, ResxValue> ReadStringResources(string path) {
    var settings = new XmlReaderSettings {
      DtdProcessing = DtdProcessing.Prohibit,
      XmlResolver = null,
      IgnoreComments = false,
      IgnoreWhitespace = false,
    };
    using XmlReader reader = XmlReader.Create(path, settings);
    XDocument document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    if (document.Root == null || document.Root.Name.LocalName != "root") {
      throw new InvalidDataException("The file is not a RESX resource document.");
    }
    var values = new Dictionary<string, ResxValue>(StringComparer.Ordinal);
    foreach (XElement data in document.Root.Elements().Where(item => item.Name.LocalName == "data")) {
      string? name = data.Attribute("name")?.Value;
      XElement? value = data.Elements().FirstOrDefault(item => item.Name.LocalName == "value");
      string? type = data.Attribute("type")?.Value;
      string? mimeType = data.Attribute("mimetype")?.Value;
      bool stringType = string.IsNullOrEmpty(type)
          || type.StartsWith("System.String", StringComparison.Ordinal);
      if (string.IsNullOrEmpty(name) || value == null || !stringType || !string.IsNullOrEmpty(mimeType)) {
        continue;
      }
      string? comment = data.Elements().FirstOrDefault(item => item.Name.LocalName == "comment")?.Value;
      values[name] = new ResxValue(value.Value, comment);
    }
    return values;
  }

  private static void ValidateEntries(IEnumerable<ResxTranslationEntry> entries) {
    var identities = new HashSet<TranslationIdentity>(IdentityComparer);
    foreach (ResxTranslationEntry entry in entries) {
      if (entry.Key.Length == 0) {
        throw new InvalidDataException("A resource key cannot be empty.");
      }
      string relative = NormalizeRelativePath(entry.RelativePath);
      _ = LocalizedRelativePath(relative, "en-US");
      if (!identities.Add(new TranslationIdentity(relative, entry.Key))) {
        throw new InvalidDataException($"Duplicate translation entry: {relative} / {entry.Key}");
      }
    }
  }

  private static XDocument CreateResx(IEnumerable<ResxTranslationEntry> entries) {
    XNamespace xml = XNamespace.Xml;
    var root = new XElement("root",
        Header("resmimetype", "text/microsoft-resx"),
        Header("version", "2.0"),
        Header("reader", "System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, "
            + "Culture=neutral, PublicKeyToken=b77a5c561934e089"),
        Header("writer", "System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, "
            + "Culture=neutral, PublicKeyToken=b77a5c561934e089"));
    foreach (ResxTranslationEntry entry in entries.OrderBy(item => item.Key, StringComparer.Ordinal)) {
      var data = new XElement("data",
          new XAttribute("name", entry.Key),
          new XAttribute(xml + "space", "preserve"),
          new XElement("value", entry.Translation));
      if (!string.IsNullOrWhiteSpace(entry.Comment)) {
        data.Add(new XElement("comment", entry.Comment));
      }
      root.Add(data);
    }
    return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
  }

  private static XElement Header(string name, string value) =>
      new("resheader", new XAttribute("name", name), new XElement("value", value));

  private static string BuildResumeHtml(string culture, IEnumerable<ResxTranslationEntry> entries) {
    var output = new StringBuilder();
    output.Append("<!doctype html>\n<html><head><meta charset=\"utf-8\"><title>Mission Planner ")
        .Append(WebUtility.HtmlEncode(culture))
        .Append(" translation</title></head><body><table>\n")
        .Append("<tr><th>File</th><th>Key</th><th>Translation</th></tr>\n");
    foreach (ResxTranslationEntry entry in entries) {
      output.Append("<tr><td>").Append(WebUtility.HtmlEncode(entry.RelativePath))
          .Append("</td><td>").Append(WebUtility.HtmlEncode(entry.Key))
          .Append("</td><td>").Append(WebUtility.HtmlEncode(entry.Translation))
          .Append("</td></tr>\n");
    }
    return output.Append("</table></body></html>\n").ToString();
  }

  private static string DecodeHtmlCell(string value) {
    return WebUtility.HtmlDecode(value);
  }

  private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

  private static string NormalizeRelativePath(string value) => value.Replace('\\', '/');

  private static string ContainedPath(string root, string relativePath) {
    string fullRoot = Path.GetFullPath(root);
    string fullPath = Path.GetFullPath(Path.Combine(
        fullRoot,
        NormalizeRelativePath(relativePath).Replace('/', Path.DirectorySeparatorChar)));
    StringComparison comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    string rootPrefix = Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
    if (!fullPath.StartsWith(rootPrefix, comparison)) {
      throw new InvalidDataException("A generated resource path escapes the selected output directory.");
    }
    EnsureNoChildDirectorySymlinks(fullRoot, fullPath);
    return fullPath;
  }

  private static void EnsureNoChildDirectorySymlinks(string root, string path) {
    string? parent = Path.GetDirectoryName(path);
    if (parent == null) {
      return;
    }
    string relative = Path.GetRelativePath(root, parent);
    if (relative == ".") {
      return;
    }
    string current = root;
    foreach (string segment in relative.Split(
                 [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                 StringSplitOptions.RemoveEmptyEntries)) {
      current = Path.Combine(current, segment);
      if (Directory.Exists(current)
          && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) {
        throw new IOException(
            $"The output path crosses a symbolic link or reparse point: {current}");
      }
    }
  }

  private static void WriteXmlAtomically(string path, XDocument document) {
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try {
      var settings = new XmlWriterSettings {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Indent = true,
        NewLineChars = "\n",
        NewLineHandling = NewLineHandling.Replace,
      };
      using (XmlWriter writer = XmlWriter.Create(temporary, settings)) {
        document.Save(writer);
      }
      File.Move(temporary, path, overwrite: true);
    } finally {
      if (File.Exists(temporary)) {
        File.Delete(temporary);
      }
    }
  }

  private static void WriteTextAtomically(string path, string contents) {
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try {
      File.WriteAllText(temporary, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
      File.Move(temporary, path, overwrite: true);
    } finally {
      if (File.Exists(temporary)) {
        File.Delete(temporary);
      }
    }
  }

  private sealed record ResxValue(string Value, string? Comment);

  private sealed class TranslationIdentityEqualityComparer : IEqualityComparer<TranslationIdentity> {
    public bool Equals(TranslationIdentity x, TranslationIdentity y) =>
        RelativePathComparer.Equals(
            NormalizeRelativePath(x.RelativePath), NormalizeRelativePath(y.RelativePath))
        && string.Equals(x.Key, y.Key, StringComparison.Ordinal);

    public int GetHashCode(TranslationIdentity value) => HashCode.Combine(
        RelativePathComparer.GetHashCode(NormalizeRelativePath(value.RelativePath)),
        StringComparer.Ordinal.GetHashCode(value.Key));
  }

  [GeneratedRegex(@"<tr\b[^>]*>\s*<td\b[^>]*>(.*?)</td>\s*<td\b[^>]*>(.*?)</td>\s*<td\b[^>]*>(.*?)</td>\s*</tr>",
      RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
      matchTimeoutMilliseconds: 2000)]
  private static partial Regex ResumeRowRegex();

}

using System;
using System.Globalization;
using System.Reflection;

namespace MissionPlannerAvalonia.Services;

public static class AppVersion {

  public static string Number => Parse(Info()).Number;

  public static string BuildDate => Parse(Info()).BuildDate;

  public static string Hash => Parse(Info()).Hash;

  public static string Informational => Info();

  public static string Title => "Mission Planner " + Full;

  public static string Full => Parse(Info()).Display;

  private static string Info() =>
      Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
          ?.InformationalVersion
      ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
      ?? "";

  internal static AppVersionParts Parse(string? informationalVersion) {
    string value = (informationalVersion ?? "").Trim();
    int plus = value.IndexOf('+');
    string number = (plus >= 0 ? value.Substring(0, plus) : value).Trim();
    if (plus < 0 || plus + 1 >= value.Length) {
      return new AppVersionParts(number, "", "", false);
    }

    string[] metadata = value.Substring(plus + 1).Split('.',
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    int position = 0;
    string buildDate = "";
    if (metadata.Length > 0
        && DateTime.TryParseExact(metadata[0], "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateTime parsedDate)) {
      buildDate = parsedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
      position++;
    }

    string hash = position < metadata.Length ? metadata[position] : "";
    if (LooksLikeCommit(hash) && hash.Length > 7) {
      hash = hash.Substring(0, 7);
    }
    if (!LooksLikeCommit(hash)) {
      hash = "";
    }
    if (hash.Length > 0 && Array.Exists(metadata,
            part => string.Equals(part, "dirty", StringComparison.OrdinalIgnoreCase))) {
      hash += "-dirty";
    }
    return new AppVersionParts(number, buildDate, hash, hash.EndsWith("-dirty",
        StringComparison.Ordinal));
  }

  private static bool LooksLikeCommit(string value) {
    if (value.Length < 7 || value.Length > 40) {
      return false;
    }
    foreach (char c in value) {
      if (!Uri.IsHexDigit(c)) {
        return false;
      }
    }
    return true;
  }
}

internal readonly record struct AppVersionParts(
    string Number, string BuildDate, string Hash, bool IsDirty) {
  internal string Display {
    get {
      string details = BuildDate;
      if (Hash.Length > 0) {
        details = details.Length == 0 ? Hash : $"{details}, {Hash}";
      }
      return details.Length == 0 ? Number : $"{Number} ({details})";
    }
  }
}

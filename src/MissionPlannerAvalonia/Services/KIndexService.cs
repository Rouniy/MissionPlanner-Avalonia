using System;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

internal static class KIndexService {
  private const string Source = "https://services.swpc.noaa.gov/text/wwv.txt";
  private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };

  internal static async Task RefreshAsync() {
    string today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    if (Settings.Instance["kindexdate_utc"] == today) {
      MissionPlanner.CurrentState.KIndexstatic = Settings.Instance.GetInt32("kindex");
      return;
    }

    try {
      string report = await Client.GetStringAsync(Source).ConfigureAwait(false);
      if (!TryParse(report, out int index)) {
        return;
      }
      MissionPlanner.CurrentState.KIndexstatic = index;
      Settings.Instance["kindex"] = index.ToString(CultureInfo.InvariantCulture);
      Settings.Instance["kindexdate_utc"] = today;
    } catch {
      // Space-weather information is advisory and must never delay or fail application startup.
    }
  }

  internal static bool TryParse(string report, out int index) {
    index = -1;
    var match = Regex.Match(report ?? "",
        @"estimated\s+planetary\s+K-index\s+at\s+.+?\s+was\s+([0-9]+(?:\.[0-9]+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    return match.Success
        && double.TryParse(match.Groups[1].Value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out double value)
        && (index = Math.Clamp((int)Math.Floor(value), 0, 9)) >= 0;
  }
}

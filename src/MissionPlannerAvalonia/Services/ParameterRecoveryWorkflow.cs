using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MissionPlanner;

namespace MissionPlannerAvalonia.Services;

internal sealed record ParameterRecoveryTarget(
    MAVLinkInterface Link,
    MAVState State,
    byte SystemId,
    byte ComponentId);

internal sealed record ParameterRecoveryResult(
    int Set,
    int Unchanged,
    IReadOnlyList<string> Failed);

internal sealed class ParameterRecoveryTargetChangedException : OperationCanceledException {
  public ParameterRecoveryTargetChangedException()
      : base("The active modem or vehicle changed, disconnected, or became armed.") {
  }
}

internal static class ParameterRecoveryWorkflow {
  internal static bool TargetsMatch(ParameterRecoveryTarget expected, MAVLinkInterface activeLink) =>
      ReferenceEquals(expected.Link, activeLink)
      && activeLink.sysidcurrent == expected.SystemId
      && activeLink.compidcurrent == expected.ComponentId
      && ReferenceEquals(activeLink.MAV, expected.State);

  internal static ParameterRecoveryResult Run(
      IReadOnlyDictionary<string, double> values,
      ParameterRecoveryTarget target,
      Func<ParameterRecoveryTarget, bool> canContinue,
      Action<ParameterRecoveryTarget, string, bool> read,
      Func<ParameterRecoveryTarget, string, double, bool> write,
      Func<ParameterRecoveryTarget, string, double?> cachedValue,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(values);
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(canContinue);
    ArgumentNullException.ThrowIfNull(read);
    ArgumentNullException.ThrowIfNull(write);
    ArgumentNullException.ThrowIfNull(cachedValue);

    void EnsureCurrent() {
      cancellationToken.ThrowIfCancellationRequested();
      if (!canContinue(target)) {
        throw new ParameterRecoveryTargetChangedException();
      }
    }

    foreach (var item in values) {
      EnsureCurrent();
      read(target, item.Key, false);
      EnsureCurrent();
    }

    foreach (var item in values.Where(
                 item => item.Key.Contains("ENABLE", StringComparison.OrdinalIgnoreCase))) {
      try {
        EnsureCurrent();
        write(target, item.Key, item.Value);
        EnsureCurrent();
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        // The complete pass below retries the value and reports it if it still fails.
        EnsureCurrent();
      }
    }

    int set = 0;
    int unchanged = 0;
    var failed = new List<string>();
    foreach (var item in values) {
      try {
        EnsureCurrent();
        double? current = cachedValue(target, item.Key);
        if (current.HasValue && Math.Abs(current.Value - item.Value) < 1e-9) {
          unchanged++;
          continue;
        }

        read(target, item.Key, true);
        EnsureCurrent();
        if (item.Key.EndsWith("_ID", StringComparison.OrdinalIgnoreCase)) {
          write(target, item.Key, 0);
          EnsureCurrent();
        }
        if (write(target, item.Key, item.Value)) {
          set++;
        } else {
          failed.Add(item.Key);
        }
        EnsureCurrent();
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        EnsureCurrent();
        failed.Add(item.Key);
      }
    }

    EnsureCurrent();
    return new ParameterRecoveryResult(set, unchanged, failed);
  }
}

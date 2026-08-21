using System;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner;

namespace MissionPlannerAvalonia.Services;

/// <summary>
/// Serializes parameter reads on the shared MAVLink transport while allowing a newer device or
/// explicit refresh to cancel the previous read. The upstream legacy parameter reader already
/// has reliable cancellation cleanup; this adapter supplies its headless reporter.
/// </summary>
internal sealed class VehicleParameterLoadCoordinator {
  private readonly Func<byte, byte, CancellationToken, Action<int, string>?, Task> _loader;
  private readonly SemaphoreSlim _gate = new(1, 1);
  private readonly LatestOperationController _operations = new();

  internal VehicleParameterLoadCoordinator(MAVLinkInterface comPort) =>
      _loader = (sysid, compid, token, progress) =>
          LoadUpstream(comPort, sysid, compid, token, progress);

  internal VehicleParameterLoadCoordinator(
      Func<byte, byte, CancellationToken, Action<int, string>?, Task> loader) =>
      _loader = loader;

  internal Operation Start(
      byte sysid,
      byte compid,
      CancellationToken lifetimeToken = default,
      Action<int, string>? progress = null) {
    var lease = _operations.Begin(lifetimeToken);
    var operation = new Operation(lease);
    operation.Completion = Run(operation, sysid, compid, progress);
    return operation;
  }

  internal void CancelCurrent() => _operations.CancelCurrent();

  internal async Task<bool> LoadLatestAsync(
      byte sysid,
      byte compid,
      CancellationToken lifetimeToken = default,
      Action<int, string>? progress = null) {
    var operation = Start(sysid, compid, lifetimeToken, progress);
    try {
      await operation.Completion.ConfigureAwait(false);
      return operation.IsLatest;
    } catch (OperationCanceledException) when (operation.Token.IsCancellationRequested) {
      return false;
    }
  }

  private async Task Run(
      Operation operation,
      byte sysid,
      byte compid,
      Action<int, string>? progress) {
    bool entered = false;
    try {
      await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
      entered = true;
      operation.Token.ThrowIfCancellationRequested();
      await _loader(sysid, compid, operation.Token, progress).ConfigureAwait(false);
      operation.Token.ThrowIfCancellationRequested();
    } finally {
      if (entered) {
        _gate.Release();
      }
      operation.CompleteLease();
    }
  }

  private static async Task LoadUpstream(
      MAVLinkInterface comPort,
      byte sysid,
      byte compid,
      CancellationToken cancellationToken,
      Action<int, string>? progress) {
    var reporter = new CancellationProgressReporter(cancellationToken, progress);
    var previousReporter = comPort.frmProgressReporter;
    comPort.frmProgressReporter = reporter;
    try {
      // The legacy PARAM_VALUE path mirrors upstream's fallback and, unlike the current MAVFTP
      // wrapper, removes both packet subscriptions when cancellation is acknowledged.
      await Task.Run(() => comPort.getParamList(sysid, compid)).ConfigureAwait(false);
      cancellationToken.ThrowIfCancellationRequested();
    } finally {
      if (ReferenceEquals(comPort.frmProgressReporter, reporter)) {
        comPort.frmProgressReporter = previousReporter;
      }
      reporter.Dispose();
    }
  }

  internal sealed class Operation {
    private LatestOperationController.Lease? _lease;

    internal Operation(LatestOperationController.Lease lease) => _lease = lease;

    internal Task Completion { get; set; } = Task.CompletedTask;
    internal CancellationToken Token => _lease?.Token ?? default;
    internal bool IsLatest => _lease?.IsLatest == true;

    internal void CompleteLease() => _lease?.Dispose();
  }
}

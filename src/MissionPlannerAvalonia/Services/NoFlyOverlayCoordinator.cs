using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Mapsui.Layers;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

/// <summary>
/// Owns the cancellable local/online NoFly layer lifecycle for one map. Flight Planner and Flight
/// Data deliberately get separate layer instances while sharing the same serialized disk cache.
/// </summary>
internal sealed class NoFlyOverlayCoordinator : IDisposable {
  private readonly Action<ILayer?> _setLayer;
  private readonly Action<string> _setStatus;
  private CancellationTokenSource? _cancellation;
  private int _version;
  private bool _active;

  internal NoFlyOverlayCoordinator(Action<ILayer?> setLayer, Action<string> setStatus) {
    _setLayer = setLayer ?? throw new ArgumentNullException(nameof(setLayer));
    _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
  }

  internal void Activate() {
    if (_active) {
      return;
    }
    _active = true;
    NoFlyOverlay.VisibilityChanged += OnVisibilityChanged;
    Refresh();
  }

  internal void Deactivate() {
    if (!_active) {
      return;
    }
    _active = false;
    NoFlyOverlay.VisibilityChanged -= OnVisibilityChanged;
    _cancellation?.Cancel();
    _version++;
  }

  internal void Refresh() {
    if (!_active) {
      return;
    }
    _cancellation?.Cancel();
    var cancellation = new CancellationTokenSource();
    _cancellation = cancellation;
    int version = ++_version;
    _ = RefreshAsync(version, cancellation);
  }

  private void OnVisibilityChanged() => Dispatcher.UIThread.Post(Refresh);

  private async Task RefreshAsync(int version, CancellationTokenSource cancellation) {
    try {
      Settings settings = Settings.Instance;
      if (!settings.GetBoolean("ShowNoFly", false)) {
        ApplyIfCurrent(version, cancellation, () => _setLayer(null));
        return;
      }

      ILayer? localLayer = await Task.Run(
          () => NoFlyOverlay.BuildLayerFromDirectory(NoFlyOverlay.DefaultDirectory),
          cancellation.Token);
      if (!ApplyIfCurrent(version, cancellation, () => _setLayer(localLayer))) {
        return;
      }

      if (!settings.GetBoolean("hknfzforceshow", false)) {
        if (localLayer != null) {
          ApplyIfCurrent(version, cancellation, () =>
              _setStatus("Local NoFly overlay loaded from " + NoFlyOverlay.DefaultDirectory));
        }
        return;
      }

      ApplyIfCurrent(version, cancellation, () =>
          _setStatus("Loading opt-in Hong Kong CAD eSUA no-fly zones…"));
      HongKongNoFlyResult result = await HongKongNoFlyService.Shared.LoadAsync(cancellation.Token);
      ILayer? combined = await Task.Run(
          () => NoFlyOverlay.BuildLayerFromDirectoryAndHongKong(
              NoFlyOverlay.DefaultDirectory, result.Zones),
          cancellation.Token);
      ApplyIfCurrent(version, cancellation, () => {
        _setLayer(combined);
        string cache = result.Stale ? "stale cache after a network failure"
            : result.FromCache ? "12-hour cache" : "official live feed";
        _setStatus(
            $"Loaded {result.Zones.Count} Hong Kong CAD eSUA no-fly polygon(s) from {cache}.");
      });
    } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
    } catch (Exception ex) {
      ApplyIfCurrent(version, cancellation, () =>
          _setStatus("Hong Kong eSUA zones unavailable; local NoFly files remain active: "
              + ex.Message));
    } finally {
      if (ReferenceEquals(_cancellation, cancellation)) {
        _cancellation = null;
      }
      cancellation.Dispose();
    }
  }

  private bool ApplyIfCurrent(
      int version, CancellationTokenSource cancellation, Action action) {
    if (!_active || version != _version || cancellation.IsCancellationRequested
        || !ReferenceEquals(_cancellation, cancellation)) {
      return false;
    }
    action();
    return true;
  }

  public void Dispose() => Deactivate();
}

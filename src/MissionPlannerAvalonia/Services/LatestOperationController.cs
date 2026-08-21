using System;
using System.Threading;

namespace MissionPlannerAvalonia.Services;

/// <summary>
/// Owns one cancellable operation at a time. Starting a replacement cancels the previous
/// operation but leaves disposal to its lease, so callbacks cannot observe a disposed source.
/// </summary>
internal sealed class LatestOperationController : IDisposable {
  private readonly object _sync = new();
  private CancellationTokenSource? _current;
  private int _generation;
  private bool _disposed;

  internal Lease Begin(CancellationToken lifetimeToken) {
    var source = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
    CancellationTokenSource? previous;
    int generation;
    lock (_sync) {
      ObjectDisposedException.ThrowIf(_disposed, this);
      previous = _current;
      _current = source;
      generation = ++_generation;
    }
    Cancel(previous);
    return new Lease(this, source, generation);
  }

  internal void CancelCurrent() {
    CancellationTokenSource? current;
    lock (_sync) {
      current = _current;
      _current = null;
      _generation++;
    }
    Cancel(current);
  }

  private bool IsCurrent(CancellationTokenSource source, int generation) {
    lock (_sync) {
      return !_disposed && ReferenceEquals(_current, source) && _generation == generation;
    }
  }

  private bool IsLatest(int generation) {
    lock (_sync) {
      return !_disposed && _generation == generation;
    }
  }

  private void Complete(CancellationTokenSource source, int generation) {
    lock (_sync) {
      if (ReferenceEquals(_current, source) && _generation == generation) {
        _current = null;
      }
    }
    source.Dispose();
  }

  private static void Cancel(CancellationTokenSource? source) {
    try {
      source?.Cancel();
    } catch (ObjectDisposedException) {
    }
  }

  public void Dispose() {
    CancellationTokenSource? current;
    lock (_sync) {
      if (_disposed) {
        return;
      }
      _disposed = true;
      current = _current;
      _current = null;
      _generation++;
    }
    Cancel(current);
  }

  internal sealed class Lease : IDisposable {
    private readonly LatestOperationController _owner;
    private readonly CancellationTokenSource _source;
    private readonly CancellationToken _token;
    private int _disposed;

    internal Lease(
        LatestOperationController owner,
        CancellationTokenSource source,
        int generation) {
      _owner = owner;
      _source = source;
      _token = source.Token;
      Generation = generation;
    }

    internal int Generation { get; }
    internal CancellationToken Token => _token;
    internal bool IsCurrent => _owner.IsCurrent(_source, Generation);
    internal bool IsLatest => _owner.IsLatest(Generation);

    public void Dispose() {
      if (Interlocked.Exchange(ref _disposed, 1) == 0) {
        _owner.Complete(_source, Generation);
      }
    }
  }
}

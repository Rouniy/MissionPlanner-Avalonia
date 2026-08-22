using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;

using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;
using MissionPlannerAvalonia.Views.GCSViews.ConfigurationView;

namespace MissionPlannerAvalonia.Tests;

public class AudioVarioTests {
  [Fact]
  public void ToneFormulaMatchesUpstreamVarioCadence() {
    Assert.Null(VarioController.CreateTone(0.3f));
    Assert.Null(VarioController.CreateTone(-0.3f));
    Assert.Null(VarioController.CreateTone(float.NaN));

    Assert.Equal(new VarioTone(730, 295, 20), VarioController.CreateTone(1f));
    Assert.Equal(new VarioTone(620, 600, 0), VarioController.CreateTone(-1f));
    Assert.Equal(new VarioTone(850, 275, 20), VarioController.CreateTone(5f));
  }

  [Fact]
  public void StartIsIdempotentAndStopCancelsActiveTone() {
    using var player = new BlockingTonePlayer();
    using var controller = new VarioController(player);
    controller.SetClimbRate(1f);

    Assert.True(controller.Start());
    Assert.True(controller.Start());
    Assert.True(SpinWait.SpinUntil(() => player.PlayCount > 0, TimeSpan.FromSeconds(2)));

    controller.Stop();

    Assert.Equal(1, player.EnsureCount);
    Assert.Equal(1, player.PlayCount);
    Assert.False(controller.IsRunning);
    Assert.Equal("vario stopped", controller.Status);
  }

  [Fact]
  public void GeneratedWaveHasValidPcmHeaderLengthAndFadedEdges() {
    byte[] wave = WavToneSynthesizer.CreateWave(700, 100);

    Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wave, 0, 4));
    Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wave, 8, 4));
    Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wave, 36, 4));
    Assert.Equal(44_100 * 100 / 1000 * sizeof(short), BitConverter.ToInt32(wave, 40));
    Assert.Equal(44 + BitConverter.ToInt32(wave, 40), wave.Length);
    Assert.Equal(0, BitConverter.ToInt16(wave, 44));
    Assert.Equal(0, BitConverter.ToInt16(wave, wave.Length - sizeof(short)));
  }

  [Fact]
  public void PlaybackFailureStopsLoopAndPublishesState() {
    using var controller = new VarioController(new FailingTonePlayer());
    int stateChanges = 0;
    controller.StateChanged += (_, _) => Interlocked.Increment(ref stateChanges);
    controller.SetClimbRate(1f);

    Assert.True(controller.Start());
    Assert.True(SpinWait.SpinUntil(() => !controller.IsRunning, TimeSpan.FromSeconds(2)));

    Assert.Contains("vario unavailable", controller.Status);
    Assert.True(Volatile.Read(ref stateChanges) >= 2);
  }

  [AvaloniaFact]
  public void PlannerViewExposesBoundAudioControls() {
    using var viewModel = new ConfigPlannerViewModel();
    var view = new ConfigPlannerView { DataContext = viewModel };
    Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    var vario = view.FindControl<Button>("VarioToggle");
    Assert.NotNull(vario);
    Assert.Same(viewModel.ToggleVarioCommand, vario.Command);
    Assert.Equal("Start Vario", vario.Content);

    var speechTest = view.FindControl<Button>("SpeechTest");
    Assert.NotNull(speechTest);
    Assert.Same(viewModel.TestSpeechCommand, speechTest.Command);
    Assert.NotNull(view.FindControl<TextBlock>("SpeechBackendStatus"));
  }

  private sealed class BlockingTonePlayer : IVarioTonePlayer {
    private readonly ConcurrentBag<VarioTone> _tones = new();

    public string Status => "test audio ready";

    public int EnsureCount { get; private set; }

    public int PlayCount => _tones.Count;

    public bool EnsureAvailable() {
      EnsureCount++;
      return true;
    }

    public async Task PlayAsync(VarioTone tone, CancellationToken cancellationToken) {
      _tones.Add(tone);
      await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    public void Stop() {
    }

    public void Dispose() {
    }
  }

  private sealed class FailingTonePlayer : IVarioTonePlayer {
    public string Status => "test audio ready";

    public bool EnsureAvailable() => true;

    public Task PlayAsync(VarioTone tone, CancellationToken cancellationToken) =>
        Task.FromException(new InvalidOperationException("test output failure"));

    public void Stop() {
    }

    public void Dispose() {
    }
  }
}

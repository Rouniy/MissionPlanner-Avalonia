using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;
using MissionPlannerAvalonia.Views.GCSViews.ConfigurationView;

namespace MissionPlannerAvalonia.Tests;

public class SshTerminalTests {
  [Fact]
  public void Terminal_buffer_applies_cursor_addressing_and_erase_commands_across_chunks() {
    var terminal = new AnsiTerminalBuffer(12, 4);

    terminal.Write("hello\r\nworld");
    terminal.Write("\u001b[");
    terminal.Write("1;1HXY\u001b[2;3H\u001b[K");

    Assert.Equal("XYllo\nwo", terminal.Render());
    Assert.Equal(1, terminal.CursorRow);
    Assert.Equal(2, terminal.CursorColumn);
  }

  [Fact]
  public void Terminal_buffer_scrolls_the_active_region() {
    var terminal = new AnsiTerminalBuffer(8, 3);

    terminal.Write("one\r\ntwo\r\nthree\r\nfour");

    Assert.Equal("two\nthree\nfour", terminal.Render());
  }

  [Fact]
  public void Terminal_buffer_restores_primary_screen_after_nano_style_alternate_screen() {
    var terminal = new AnsiTerminalBuffer(20, 4);
    terminal.Write("primary prompt");

    terminal.Write("\u001b[?1049heditor\u001b[2;1Hline two");
    Assert.Equal("editor\nline two", terminal.Render());

    terminal.Write("\u001b[?1049l");
    Assert.Equal("primary prompt", terminal.Render());
  }

  [Fact]
  public void Terminal_buffer_tracks_application_cursor_mode_and_answers_position_query() {
    var terminal = new AnsiTerminalBuffer(20, 4);
    string? response = null;
    terminal.ResponseGenerated += value => response = value;

    terminal.Write("abc\u001b[?1h\u001b[6n");

    Assert.True(terminal.ApplicationCursorKeys);
    Assert.Equal("\u001b[1;4R", response);
    terminal.Write("\u001b[?1l");
    Assert.False(terminal.ApplicationCursorKeys);
  }

  [Fact]
  public void Terminal_buffer_retains_official_sgr_colours_and_font_attributes_per_cell() {
    var terminal = new AnsiTerminalBuffer(20, 3);

    terminal.Write("plain\u001b[1;3;");
    terminal.Write("4;31;44mA\u001b[22;23;24;39;49mB" +
        "\u001b[7mC\u001b[27mD");
    TerminalSnapshot screen = terminal.Snapshot();

    TerminalAttributes styled = screen[0, 5].Attributes;
    Assert.True(styled.Bold);
    Assert.True(styled.Italic);
    Assert.True(styled.Underline);
    Assert.Equal(new TerminalRgb(255, 0, 0), styled.Foreground);
    Assert.Equal(new TerminalRgb(92, 92, 255), styled.Background);
    Assert.Equal(default, screen[0, 6].Attributes);
    Assert.True(screen[0, 7].Attributes.Inverse);
    Assert.False(screen[0, 8].Attributes.Inverse);
  }

  [Fact]
  public void Terminal_buffer_supports_xterm_palette_truecolour_and_styled_screen_edits() {
    var terminal = new AnsiTerminalBuffer(12, 3);

    terminal.Write("\u001b[38;5;196;48;2;12;34;56mRGB");
    terminal.Write("\u001b[1G\u001b[@");
    TerminalSnapshot screen = terminal.Snapshot();

    Assert.Equal(' ', screen[0, 0].Character);
    Assert.Equal(new TerminalRgb(255, 0, 0), screen[0, 1].Attributes.Foreground);
    Assert.Equal(new TerminalRgb(12, 34, 56), screen[0, 1].Attributes.Background);

    terminal.Write("\u001b[?1049h\u001b[32malt\u001b[?1049l");
    screen = terminal.Snapshot();
    Assert.Equal(" RGB", terminal.Render());
    Assert.Equal(new TerminalRgb(255, 0, 0), screen[0, 1].Attributes.Foreground);
  }

  [Fact]
  public void Terminal_buffer_honours_xterm_cursor_visibility_mode() {
    var terminal = new AnsiTerminalBuffer(10, 2);

    Assert.True(terminal.Snapshot().CursorVisible);
    terminal.Write("abc\u001b[?25l");
    Assert.False(terminal.Snapshot().CursorVisible);
    terminal.Write("\u001b[?25h");
    TerminalSnapshot screen = terminal.Snapshot();
    Assert.True(screen.CursorVisible);
    Assert.Equal(0, screen.CursorRow);
    Assert.Equal(3, screen.CursorColumn);
  }

  [Fact]
  public void Terminal_buffer_scrolls_cell_attributes_with_their_characters() {
    var terminal = new AnsiTerminalBuffer(8, 3);

    terminal.Write("\u001b[31mred\r\n\u001b[32mgreen\r\n\u001b[34mblue\r\nlast");
    TerminalSnapshot screen = terminal.Snapshot();

    Assert.Equal("green\nblue\nlast", terminal.Render());
    Assert.Equal(new TerminalRgb(0, 205, 0), screen[0, 0].Attributes.Foreground);
    Assert.Equal(new TerminalRgb(92, 92, 255), screen[1, 0].Attributes.Foreground);
    Assert.Equal(new TerminalRgb(92, 92, 255), screen[2, 0].Attributes.Foreground);
  }

  [Fact]
  public void Unterminated_remote_string_control_cannot_wedge_terminal_output_forever() {
    var terminal = new AnsiTerminalBuffer(20, 4);

    terminal.Write("\u001b]" + new string('x', 4097) + "visible");

    Assert.Equal("visible", terminal.Render());
  }

  [Theory]
  [InlineData("companion.local", 22, "companion.local", 22)]
  [InlineData("companion.local:2202", 22, "companion.local", 2202)]
  [InlineData("[fe80::1234]:2202", 22, "fe80::1234", 2202)]
  [InlineData("fe80::1234", 22, "fe80::1234", 22)]
  public void Ssh_endpoint_accepts_official_host_port_form_and_ipv6(
      string input, int defaultPort, string expectedHost, int expectedPort) {
    Assert.True(SshEndpoint.TryParse(input, defaultPort, out string host, out int port));
    Assert.Equal(expectedHost, host);
    Assert.Equal(expectedPort, port);
  }

  [Theory]
  [InlineData("")]
  [InlineData("host:not-a-port")]
  [InlineData("[broken")]
  public void Ssh_endpoint_rejects_invalid_values(string input) {
    Assert.False(SshEndpoint.TryParse(input, 22, out _, out _));
  }

  [Fact]
  public void Trusted_host_setting_is_case_insensitive_and_xml_key_safe() {
    string first = SshEndpoint.TrustedKeySettingName("Companion.Local", 22);
    string second = SshEndpoint.TrustedKeySettingName("companion.local", 22);

    Assert.Equal(first, second);
    Assert.Matches("^SSHHostKey_[0-9A-F]{64}$", first);
  }

  [Theory]
  [InlineData(Key.Up, KeyModifiers.None, false, "\u001b[A")]
  [InlineData(Key.Up, KeyModifiers.None, true, "\u001bOA")]
  [InlineData(Key.Home, KeyModifiers.None, true, "\u001bOH")]
  [InlineData(Key.End, KeyModifiers.None, true, "\u001bOF")]
  [InlineData(Key.F5, KeyModifiers.None, false, "\u001b[15~")]
  [InlineData(Key.Tab, KeyModifiers.Shift, false, "\u001b[Z")]
  [InlineData(Key.C, KeyModifiers.Control, false, "\u0003")]
  public void Keyboard_encoder_matches_xterm_sequences(
      Key key, KeyModifiers modifiers, bool applicationMode, string expected) {
    Assert.Equal(expected, TerminalKeyEncoder.Encode(key, modifiers, applicationMode));
  }

  [Fact]
  public void Keyboard_encoder_leaves_linux_paste_shortcut_to_the_text_input_stack() {
    Assert.Null(TerminalKeyEncoder.Encode(
        Key.V, KeyModifiers.Control | KeyModifiers.Shift, false));
    Assert.Null(TerminalKeyEncoder.Encode(Key.Insert, KeyModifiers.Shift, false));
  }

  [Fact]
  public void Fingerprints_require_an_exact_pinned_sha256_value() {
    const string fingerprint = "SHA256:abcdefghijklmnopqrstuvwxyz0123456789";
    Assert.True(SshHostKeyGuard.FingerprintsEqual(fingerprint, fingerprint));
    Assert.False(SshHostKeyGuard.FingerprintsEqual(null, fingerprint));
    Assert.False(SshHostKeyGuard.FingerprintsEqual(
        fingerprint, "SHA256:ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"));
  }

  [AvaloniaFact]
  public void Terminal_page_exposes_ssh_as_the_official_companion_transport() {
    using var viewModel = new ConfigTerminalViewModel(new FakeSshSession());
    var view = new ConfigTerminalView { DataContext = viewModel };

    Assert.Equal(new[] {
      ConfigTerminalViewModel.MavlinkTransport,
      ConfigTerminalViewModel.RawTransport,
      ConfigTerminalViewModel.SshTransport,
    }, viewModel.TransportOptions);

    viewModel.SelectedTransport = ConfigTerminalViewModel.SshTransport;
    Assert.True(viewModel.IsSshSelected);
    Assert.NotNull(view.FindControl<Avalonia.Controls.TextBox>("TerminalInput"));
    Assert.NotNull(view.FindControl<AnsiTerminalView>("TerminalOutput"));
  }

  [AvaloniaFact]
  public void Styled_terminal_view_renders_colours_attributes_and_a_distinct_cursor() {
    var terminal = new AnsiTerminalBuffer(12, 3);
    terminal.Write("\u001b[1;3;4;31;44mA\u001b[0mB");
    var view = new AnsiTerminalView {
      Screen = terminal.Snapshot(),
      ShowCursor = true,
      FontFamily = new FontFamily("monospace"),
      FontSize = 14,
    };

    var runs = view.Inlines!.OfType<Run>().ToArray();
    Assert.Equal(3, runs.Length);
    Assert.Equal("A", runs[0].Text);
    Assert.Equal(FontWeight.Bold, runs[0].FontWeight);
    Assert.Equal(FontStyle.Italic, runs[0].FontStyle);
    Assert.Equal(Avalonia.Media.TextDecorations.Underline, runs[0].TextDecorations);
    Assert.Equal(Color.FromRgb(255, 0, 0),
        Assert.IsType<SolidColorBrush>(runs[0].Foreground).Color);
    Assert.Equal(Color.FromRgb(92, 92, 255),
        Assert.IsType<SolidColorBrush>(runs[0].Background).Color);
    Assert.Equal(" ", runs[2].Text);
    Assert.Equal(Color.FromRgb(221, 221, 221),
        Assert.IsType<SolidColorBrush>(runs[2].Background).Color);

    view.SelectionStart = 0;
    view.SelectionEnd = 2;
    Assert.Equal("AB", view.SelectedText);

    view.Measure(new Size(300, 100));
    view.Arrange(new Rect(0, 0, 300, 100));
    using var target = new RenderTargetBitmap(new PixelSize(300, 100));
    target.Render(view);
  }

  [AvaloniaFact]
  public async Task Ssh_view_model_starts_writes_stops_and_clears_the_password() {
    var session = new FakeSshSession();
    using var viewModel = ReadySshViewModel(session);

    await viewModel.StartSessionCommand.ExecuteAsync(null);

    Assert.True(viewModel.SessionOpen);
    Assert.True(viewModel.IsSshSession);
    Assert.Equal("", viewModel.SshPassword);
    Assert.Equal("companion.local", session.Connection?.Host);
    viewModel.Input = "uname -a";
    await viewModel.SendCommand.ExecuteAsync(null);
    Assert.Contains("uname -a\r", session.Writes);

    await viewModel.StopSessionCommand.ExecuteAsync(null);
    Assert.False(viewModel.SessionOpen);
    Assert.True(session.StopCount > 0);
  }

  [AvaloniaFact]
  public async Task Ssh_view_model_publishes_styled_screen_snapshots_from_remote_output() {
    var session = new FakeSshSession();
    using var viewModel = ReadySshViewModel(session);
    await viewModel.StartSessionCommand.ExecuteAsync(null);

    session.Emit("\u001b[38;5;46mready\u001b[?25l");
    await Dispatcher.UIThread.InvokeAsync(() => { });

    Assert.Equal("ready", viewModel.Output);
    Assert.NotNull(viewModel.TerminalScreen);
    Assert.Equal(new TerminalRgb(0, 255, 0),
        viewModel.TerminalScreen![0, 0].Attributes.Foreground);
    Assert.False(viewModel.TerminalScreen.CursorVisible);
  }

  [AvaloniaFact]
  public async Task Tunnel_input_handlers_forward_text_and_function_keys_without_editing_text_box() {
    var session = new FakeSshSession();
    using var viewModel = ReadySshViewModel(session);
    await viewModel.StartSessionCommand.ExecuteAsync(null);
    var view = new ConfigTerminalView { DataContext = viewModel };
    var window = new Window { Content = view };
    try {
      window.Show();
      var input = Assert.IsType<TextBox>(view.FindControl<TextBox>("TerminalInput"));
      input.Focus();

      window.KeyTextInput("abc");
      window.KeyPress(
          Key.F12, RawInputModifiers.None, PhysicalKey.F12, "F12");
      await Dispatcher.UIThread.InvokeAsync(() => { });

      Assert.Contains("abc", session.Writes);
      Assert.Contains("\u001b[24~", session.Writes);
      Assert.True(string.IsNullOrEmpty(input.Text));
    } finally {
      window.Close();
    }
  }

  [AvaloniaFact]
  public async Task Linux_terminal_paste_shortcut_forwards_clipboard_to_remote_pty() {
    var session = new FakeSshSession();
    using var viewModel = ReadySshViewModel(session);
    await viewModel.StartSessionCommand.ExecuteAsync(null);
    var view = new ConfigTerminalView { DataContext = viewModel };
    var window = new Window { Content = view };
    try {
      window.Show();
      var input = Assert.IsType<TextBox>(view.FindControl<TextBox>("TerminalInput"));
      input.Focus();
      await window.Clipboard!.SetTextAsync("pasted text");

      window.KeyPress(Key.V, RawInputModifiers.Control | RawInputModifiers.Shift,
          PhysicalKey.V, "v");
      string written = await session.NextWrite.Task.WaitAsync(TimeSpan.FromSeconds(2));

      Assert.Equal("pasted text", written);
      Assert.True(string.IsNullOrEmpty(input.Text));
    } finally {
      window.Close();
    }
  }

  [AvaloniaFact]
  public async Task Unexpected_ssh_close_updates_session_state() {
    var session = new FakeSshSession();
    using var viewModel = ReadySshViewModel(session);
    await viewModel.StartSessionCommand.ExecuteAsync(null);

    session.Close("remote closed");
    await Dispatcher.UIThread.InvokeAsync(() => { });

    Assert.False(viewModel.SessionOpen);
    Assert.Equal("remote closed", viewModel.Status);
  }

  [AvaloniaFact]
  public async Task Stop_command_cancels_an_in_progress_ssh_connect() {
    var session = new FakeSshSession { BlockConnect = true };
    using var viewModel = ReadySshViewModel(session);

    Task start = viewModel.StartSessionCommand.ExecuteAsync(null);
    await session.ConnectStarted.Task;
    Assert.True(viewModel.IsStarting);
    Assert.True(viewModel.StopSessionCommand.CanExecute(null));

    await viewModel.StopSessionCommand.ExecuteAsync(null);
    await start;

    Assert.False(viewModel.SessionOpen);
    Assert.False(viewModel.IsStarting);
    Assert.Contains("cancel", viewModel.Status, StringComparison.OrdinalIgnoreCase);
  }

  private static ConfigTerminalViewModel ReadySshViewModel(FakeSshSession session) => new(session) {
    SelectedTransport = ConfigTerminalViewModel.SshTransport,
    SshHost = "companion.local",
    SshPort = "22",
    SshUsername = "pilot",
    SshPassword = "secret",
  };

  private sealed class FakeSshSession : ISshTerminalSession {
    public event Action<string>? TextReceived;
    public event Action<string>? ConnectionClosed;
    public bool IsConnected { get; private set; }
    public SshTerminalConnection? Connection { get; private set; }
    public List<string> Writes { get; } = new();
    public int StopCount { get; private set; }
    public bool BlockConnect { get; init; }
    public TaskCompletionSource ConnectStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<string> NextWrite { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task ConnectAsync(SshTerminalConnection connection, string? trustedFingerprint,
        CancellationToken cancellationToken) {
      cancellationToken.ThrowIfCancellationRequested();
      Connection = connection;
      ConnectStarted.TrySetResult();
      if (BlockConnect) {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
      }
      IsConnected = true;
    }

    public Task WriteAsync(string text, CancellationToken cancellationToken = default) {
      cancellationToken.ThrowIfCancellationRequested();
      Writes.Add(text);
      NextWrite.TrySetResult(text);
      return Task.CompletedTask;
    }

    public Task StopAsync() {
      Stop();
      return Task.CompletedTask;
    }

    public void Stop() {
      StopCount++;
      IsConnected = false;
    }

    public void Emit(string text) => TextReceived?.Invoke(text);
    public void Close(string reason) => ConnectionClosed?.Invoke(reason);
    public ValueTask DisposeAsync() {
      Stop();
      return ValueTask.CompletedTask;
    }
  }
}

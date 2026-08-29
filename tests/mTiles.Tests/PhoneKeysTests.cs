using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using AvaloniaEdit;
using mTiles.Models;
using mTiles.Services.Phone;
using mTiles.Services.Speech;
using mTiles.ViewModels;
using Terminal.Avalonia;
using Terminal.Pty;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Where a key pressed on the phone lands.
/// </summary>
/// <remarks>
/// The keys exist to answer the prompt an agent is waiting on, so the two things worth pinning are that
/// they reach the shell <em>as the shell reads them</em> — a cursor key is not the characters
/// <c>ESC [ A</c> to every application, and only the terminal control knows which — and that they follow
/// the transcript's own routing rather than a rule of their own. Dictating a line and pressing Enter is
/// one gesture in two halves; if the halves chose their target differently the sentence would sit in one
/// place while the Enter submitted something else in another.
/// </remarks>
public class PhoneKeysTests : IDisposable
{
    private static readonly ShellProfile Shell = new()
    {
        Name = "fake",
        ExecutablePath = "fake-shell",
        Args = ["-l"],
        Type = ShellType.Bash,
    };

    private readonly TempSettings _settings = new();
    private readonly List<TerminalControl> _controls = [];
    private readonly List<PhoneBridgeManager> _managers = [];

    public void Dispose()
    {
        foreach (var manager in _managers)
            manager.DisposeAsync().AsTask().GetAwaiter().GetResult();

        _settings.Dispose();
    }

    private void OnUiThread(Func<Task> body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(PhoneKeysTests).Assembly);
        session.Dispatch(async () =>
        {
            try { await body(); }
            finally
            {
                foreach (var control in _controls)
                    control.Dispose();
                _controls.Clear();
            }
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"timed out waiting until {what}");
            await Task.Delay(1);
        }
    }

    /// <summary>A window is what makes a control "on screen" as far as the routing is concerned.</summary>
    private static Window ShowingWindow(Control content)
    {
        var window = new Window { Content = content };
        window.Show();
        return window;
    }

    /// <summary>A tile running a shell that records every byte the control sends it.</summary>
    private (LeafTileNodeViewModel Tile, TerminalControl Control, FakePty Pty) TerminalTile()
    {
        FakePty? pty = null;
        var control = new TerminalControl { PtyFactory = options => pty = new FakePty(options) };
        _controls.Add(control);

        var content = new TerminalTileViewModel("", Shell, _settings.Service, LaunchScripts.None);
        content.AttachControl(control);
        control.Start(new PtyOptions { Command = "fake-shell", Arguments = ["-l"] });

        var tile = new LeafTileNodeViewModel(TileContentType.Terminal, content, "", new TileActivationScope());
        return (tile, control, pty!);
    }

    // ── the shell ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The sequences a shell actually receives.
    /// </summary>
    /// <remarks>
    /// This is the reason a key event is synthesised rather than bytes written: what Up means on the wire
    /// is the terminal control's decision and it changes under the application's feet — DECCKM turns
    /// <c>ESC [ A</c> into <c>ESC O A</c>, and win32-input-mode replaces both with INPUT_RECORDs. What is
    /// pinned here is the plain case; the point is that the answer comes from the control rather than
    /// from a table in this application that would have to be kept in step with it.
    /// </remarks>
    [Theory]
    [InlineData("enter", "\r")]
    [InlineData("up", "\x1b[A")]
    [InlineData("down", "\x1b[B")]
    public void A_key_reaches_the_shell_as_the_shell_reads_it(string name, string expected)
        => OnUiThread(async () =>
        {
            var (tile, _, pty) = TerminalTile();
            Assert.True(PhoneKeys.TryParse(name, out var key));

            Assert.True(PhoneKeys.Press(tile, key));

            await WaitUntil(() => pty.Written.Length > 0, "the shell has been sent something");
            Assert.Equal(expected, pty.Written);
        });

    /// <summary>
    /// A tile whose shell has exited is refused rather than pressed at.
    /// </summary>
    /// <remarks>
    /// False is what turns into a sentence on the phone. Reporting success for a key that went nowhere
    /// leaves the user pressing Enter at a dead terminal with nothing to tell them why — and the phone is
    /// usually the only screen they are looking at.
    /// </remarks>
    [Fact]
    public void A_dead_shell_takes_nothing()
        => OnUiThread(async () =>
        {
            var (tile, control, pty) = TerminalTile();
            pty.EndProcess();
            await WaitUntil(() => !control.IsRunning, "the session has been reported dead");

            Assert.False(PhoneKeys.Press(tile, PhoneKey.Enter));
        });

    [Fact]
    public void A_tile_that_is_not_a_terminal_takes_nothing()
        => OnUiThread(() =>
        {
            var tile = new LeafTileNodeViewModel(TileContentType.Empty, null, "", new TileActivationScope());

            Assert.False(PhoneKeys.Press(tile, PhoneKey.Enter));
            Assert.False(PhoneKeys.Press(null, PhoneKey.Enter));
            return Task.CompletedTask;
        });

    // ── the focused control ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_focused_text_box_takes_the_key_before_the_terminal()
        => OnUiThread(async () =>
        {
            var (tile, _, pty) = TerminalTile();
            var box = new TextBox();
            ShowingWindow(box);
            var seen = new List<Key>();
            box.KeyDown += (_, e) => seen.Add(e.Key);

            Assert.True(PhoneKeys.Press(tile, PhoneKey.Down, box));

            await Task.Yield();
            Assert.Equal([Key.Down], seen);
            Assert.Equal("", pty.Written);
        });

    /// <summary>
    /// A read-only control is not a destination, so the key carries on to the shell.
    /// </summary>
    /// <remarks>
    /// The same rule the transcript follows. Half the text in this application is in a read-only editor —
    /// a diff, a transcript — and one of them holding the focus must not swallow the Enter that was meant
    /// for the agent waiting next door.
    /// </remarks>
    [Fact]
    public void A_read_only_editor_does_not_swallow_the_key()
        => OnUiThread(async () =>
        {
            var (tile, _, pty) = TerminalTile();
            var editor = new TextEditor { IsReadOnly = true };
            ShowingWindow(editor);

            Assert.True(PhoneKeys.Press(tile, PhoneKey.Enter, editor));

            await WaitUntil(() => pty.Written.Length > 0, "the shell has been sent something");
            Assert.Equal("\r", pty.Written);
        });

    /// <summary>
    /// A control whose window has gone is not a destination either.
    /// </summary>
    /// <remarks>
    /// The focused element is read on a socket thread's behalf and used against a tree that may have
    /// closed underneath it — a dialog dismissed while the phone was in a pocket. Pressing at a detached
    /// control is a key nobody can see, reported as delivered.
    /// </remarks>
    [Fact]
    public void A_control_that_has_left_the_tree_does_not_take_the_key()
        => OnUiThread(async () =>
        {
            var (tile, _, pty) = TerminalTile();
            var box = new TextBox();
            var window = ShowingWindow(box);
            window.Content = null;                       // the dialog closed while the phone was in a pocket
            window.Close();

            Assert.True(PhoneKeys.Press(tile, PhoneKey.Enter, box));

            await WaitUntil(() => pty.Written.Length > 0, "the shell has been sent something");
            Assert.Equal("\r", pty.Written);
        });

    // ── what the phone is told ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The sentence a refused key comes back as names the reason, and a delivered one says nothing.
    /// </summary>
    /// <remarks>
    /// <para>Between <c>PhoneKeys</c>, which is pinned above, and the server, which is pinned against a
    /// fake sink, sits the piece that turns "it did not land" into words on a phone screen — and it was
    /// the only part of this path nothing exercised. The phone is usually the only screen the user is
    /// looking at, so the difference between three sentences is the difference between walking back to
    /// the computer and knowing what to do; a Note tile answered with "the shell is not running" would
    /// send somebody to restart a shell that was never involved.</para>
    /// <para>Driven through <see cref="IPhoneSink"/> rather than the method, because that is the surface
    /// the server holds and the one a refusal has to travel back through.</para>
    /// </remarks>
    [Fact]
    public void A_refused_key_comes_back_as_the_reason_it_was_refused()
        => OnUiThread(async () =>
        {
            var manager = Manager(out var active);

            active.Tile = null;
            Assert.Equal("No tile is active in mTiles.", await Press(manager, PhoneKey.Enter));

            var (terminal, control, pty) = TerminalTile();
            active.Tile = terminal;
            Assert.Null(await Press(manager, PhoneKey.Enter));
            await WaitUntil(() => pty.Written.Length > 0, "the shell has been sent something");

            pty.EndProcess();
            await WaitUntil(() => !control.IsRunning, "the session has been reported dead");
            Assert.Equal("The shell in that tile is not running.", await Press(manager, PhoneKey.Enter));

            // Anything that is not a terminal at all. The two refusals are deliberately different
            // sentences: one names a shell to restart, the other says the tile was never a destination.
            active.Tile = new LeafTileNodeViewModel(TileContentType.Note, null, "", new TileActivationScope());
            Assert.Equal("That tile has nothing to type into.", await Press(manager, PhoneKey.Enter));
        });

    /// <summary>
    /// A handler that throws costs the keystroke and nothing else.
    /// </summary>
    /// <remarks>
    /// Pressing the key ends in a <c>RaiseEvent</c>, which runs the application's own KeyDown handlers
    /// on this thread. Unwrapped, a throw from any of them is captured into the task, returns to the
    /// socket thread, passes both catches in the server's pump — neither is a cancellation or a
    /// <c>WebSocketException</c> — and reaches Kestrel, which drops the connection: the phone blinks
    /// "Offline" and reconnects, over a keystroke, with nothing anywhere saying why.
    /// </remarks>
    [Fact]
    public void A_handler_that_throws_does_not_cost_the_connection()
        => OnUiThread(async () =>
        {
            var manager = Manager(out var active);
            var (terminal, _, _) = TerminalTile();
            active.Tile = terminal;

            var box = new TextBox();
            ShowingWindow(box);
            box.KeyDown += (_, _) => throw new InvalidOperationException("a handler somewhere");
            manager.FocusedElement = () => box;

            // Not reported as delivered: whether the key reached anything before the handler threw is
            // unknowable from here, and the honest answer is that it did not work.
            Assert.Equal("mTiles could not deliver that key.", await Press(manager, PhoneKey.Enter));
        });

    /// <summary>Whatever the test says is the active tile, read at the moment of the press.</summary>
    private sealed class ActiveTile
    {
        public LeafTileNodeViewModel? Tile { get; set; }
    }

    private static Task<string?> Press(PhoneBridgeManager manager, PhoneKey key) =>
        ((IPhoneSink)manager).PressKeyAsync(key);

    /// <summary>
    /// A manager with nothing running in it.
    /// </summary>
    /// <remarks>
    /// The bridge is never started here: pressing a key touches the active tile and the focused control
    /// and nothing else, so there is no server, no certificate and no port to arrange. The dispatcher
    /// runs inline because this already is the UI thread.
    /// </remarks>
    private PhoneBridgeManager Manager(out ActiveTile active)
    {
        var holder = new ActiveTile();
        active = holder;

        var router = new RoutedAudioCapture(new NothingCapture(), new PhoneAudioCapture());
        var manager = new PhoneBridgeManager(
            _settings.Service,
            new DictationService(_settings.Service, router),
            router,
            activeTile: () => holder.Tile,
            dispatcher: new InlineDispatcher(),
            sessionStore: new NowhereStore());

        _managers.Add(manager);
        return manager;
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();

        public Task<T> InvokeAsync<T>(Func<T> work) => Task.FromResult(work());
    }

    private sealed class NowhereStore : IPhoneSessionStore
    {
        public IReadOnlyList<PhoneSession> Load() => [];

        public void Save(IReadOnlyList<PhoneSession> sessions) { }
    }

    private sealed class NothingCapture : IAudioCapture
    {
        public bool IsAvailable => true;
        public bool IsRecording => false;

        public IReadOnlyList<string> GetInputDevices(bool rescan = false) => ["silent"];

        public void Start(string deviceName) { }

        public IRecordingHandle? Detach() => null;

        public float[] Finish(IRecordingHandle? recording) => [];

        public void Dispose() { }
    }

    // ── the wire names ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The names are exactly the page's three, matched exactly.
    /// </summary>
    /// <remarks>
    /// What arrives is a string from a paired device across a network, and what it selects is a keystroke
    /// into a shell. A closed list, matched case-sensitively, is the whole of the parsing — anything else
    /// is nonsense, which the server answers with silence.
    /// </remarks>
    [Theory]
    [InlineData("enter", true)]
    [InlineData("up", true)]
    [InlineData("down", true)]
    [InlineData("Enter", false)]
    [InlineData("left", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_three_names_are_keys(string? name, bool known)
        => Assert.Equal(known, PhoneKeys.TryParse(name, out _));

    /// <summary>
    /// Every key in the enum has a wire name and a keystroke, and no two share either.
    /// </summary>
    /// <remarks>
    /// The set is closed at compile time, but nothing in the compiler makes the three places that list it
    /// agree — the enum, <see cref="PhoneKeys.TryParse"/> and <see cref="PhoneKeys.ToAvalonia"/>. Walked
    /// rather than enumerated here on purpose: a fourth key added to the enum and missed in either of the
    /// others fails this without anybody having to remember to come back and add a case. What it is
    /// standing guard over is one specific outcome — a key that arrives, is accepted, and is delivered as
    /// something else. Enter is the one it would have been delivered as, and Enter is the press that
    /// takes an agent's default answer.
    /// </remarks>
    [Fact]
    public void Every_key_has_a_name_and_a_keystroke_of_its_own()
    {
        var keys = Enum.GetValues<PhoneKey>();

        // The wire name is the member's own name in lower case — the convention the page is written to,
        // pinned here so it stays one rather than becoming a lookup table somebody has to remember.
        foreach (var key in keys)
        {
            Assert.True(PhoneKeys.TryParse(key.ToString().ToLowerInvariant(), out var parsed),
                $"{key} has no wire name");
            Assert.Equal(key, parsed);
        }

        // And no two of them arrive as the same keystroke, which is what a forgotten arm used to produce.
        var strokes = keys.Select(PhoneKeys.ToAvalonia).ToList();
        Assert.Equal(keys.Length, strokes.Distinct().Count());
    }
}

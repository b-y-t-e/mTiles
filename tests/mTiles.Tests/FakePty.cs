using Terminal.Pty;

namespace mTiles.Tests;

/// <summary>
/// A pseudo-terminal under the test's control: nothing is spawned, and the "child" lives exactly as
/// long as the test says. The launch chain is all about how long a command survives, so that has to be
/// something a test decides rather than something it waits out.
/// </summary>
internal sealed class FakePty : IPtyConnection
{
    private readonly BlockingStream _output = new();
    private readonly TaskCompletionSource<int> _exit = new();

    public FakePty(PtyOptions options) => Options = options;

    /// <summary>What the control was asked to spawn — the command line the chain decided on.</summary>
    public PtyOptions Options { get; }

    public int ProcessId => 4242;
    public string HostDescription => "fake";
    public Stream Output => _output;
    public bool Disposed { get; private set; }

    public event Action<int>? Exited;

    public void Write(ReadOnlySpan<byte> data) { }
    public void Resize(int columns, int rows) { }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        => _exit.Task.WaitAsync(cancellationToken);

    /// <summary>Ends the "child": the pipe closes and the exit code is reported.</summary>
    public void EndProcess(int exitCode = 0)
    {
        _output.Dispose();
        if (_exit.TrySetResult(exitCode))
            Exited?.Invoke(exitCode);
    }

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        EndProcess(-1);   // as the real backends do: disposing terminates the child
    }

    /// <summary>A stream whose Read blocks until the stream is closed — the one property of a PTY that
    /// makes the control's pump thread behave as it does in production.</summary>
    private sealed class BlockingStream : Stream
    {
        private readonly SemaphoreSlim _closed = new(0);

        public override int Read(byte[] buffer, int offset, int count)
        {
            _closed.Wait();
            _closed.Release();
            return 0;   // EOF
        }

        protected override void Dispose(bool disposing)
        {
            _closed.Release();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

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

    /// <summary>Everything the control has sent to the "child", decoded. The startup script is only
    /// ever observable here: it is typed into the session rather than passed as an argument, so a test
    /// that does not read the writes cannot tell a script that arrived from one that was dropped.</summary>
    public string Written => _written.ToString();

    private readonly System.Text.StringBuilder _written = new();

    public void Write(ReadOnlySpan<byte> data) => _written.Append(System.Text.Encoding.UTF8.GetString(data));

    /// <summary>Makes the "child" speak. The control gates its startup input on the first byte of
    /// output — a shell that has not opened its stdin yet silently drops what arrives before that — so
    /// nothing is typed into a session until this has been called at least once.</summary>
    public void Emit(string text) => _output.Feed(System.Text.Encoding.UTF8.GetBytes(text));
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
        private readonly SemaphoreSlim _available = new(0);
        private readonly Queue<byte[]> _chunks = new();
        private bool _closed;

        /// <summary>Queues a chunk for the pump to read, as the child writing to its pipe.</summary>
        public void Feed(byte[] chunk)
        {
            lock (_chunks) _chunks.Enqueue(chunk);
            _available.Release();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (true)
            {
                _available.Wait();
                lock (_chunks)
                {
                    if (_chunks.Count > 0)
                    {
                        var chunk = _chunks.Dequeue();
                        int n = Math.Min(count, chunk.Length);
                        chunk.AsSpan(0, n).CopyTo(buffer.AsSpan(offset));
                        if (n < chunk.Length)
                            _chunks.Enqueue(chunk[n..]);
                        return n;
                    }
                }
                if (_closed)
                {
                    _available.Release();   // every other reader sees the close too
                    return 0;               // EOF
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            _closed = true;
            _available.Release();
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

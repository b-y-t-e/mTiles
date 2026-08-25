using System.Net;
using System.Security.Cryptography;
using System.Text;
using mTiles.Services.Speech;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Downloading a model, through the store's own code with the network faked at
/// <c>httpClientFactory</c>. Everything here is measured in bytes rather than megabytes, but it is the
/// same path a user's half-gigabyte takes: resume, digest, unpack, and the refusals.
/// </summary>
public class SpeechModelDownloadTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mtiles-tests", Guid.NewGuid().ToString("N"));

    public SpeechModelDownloadTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static string Digest(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static SpeechModel ModelFor(byte[] content, string fileName = "fake-model.bin",
        SpeechModelKind kind = SpeechModelKind.WhisperGgml, string? digest = null) =>
        new("fake", "Fake Model", fileName, "https://example.invalid/model",
            content.Length, digest ?? Digest(content), "for tests", kind);

    /// <summary>Serves one payload, and records what was asked for — the range header is the only way to
    /// tell a resumed download from one that started again.</summary>
    private sealed class FakeServer(byte[] content, bool honourRange = true, bool refuseRange = false)
        : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public long? RequestedFrom { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            RequestedFrom = request.Headers.Range?.Ranges.FirstOrDefault()?.From;

            if (refuseRange && RequestedFrom > 0)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));

            var from = honourRange ? RequestedFrom ?? 0 : 0;
            var body = content.AsSpan((int)from).ToArray();
            var partial = from > 0 && honourRange;
            var message = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };

            // A real 206 says where it starts, and the store checks: a partial body appended at the
            // wrong offset makes a file of the right length out of the wrong bytes.
            if (partial)
                message.Content.Headers.ContentRange =
                    new System.Net.Http.Headers.ContentRangeHeaderValue(from, content.Length - 1, content.Length);

            return Task.FromResult(message);
        }
    }

    private SpeechModelStore StoreFor(FakeServer server) =>
        new(_directory, () => new HttpClient(server));

    /// <summary>A body that delivers some bytes and then simply never says anything more — a dropped
    /// route, a proxy holding the socket open. Not an error: nothing at all.</summary>
    private sealed class StallingStream(byte[] prefix) : Stream
    {
        private int _offset;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_offset >= prefix.Length)
            {
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            }

            var count = Math.Min(buffer.Length, prefix.Length - _offset);
            prefix.AsSpan(_offset, count).CopyTo(buffer.Span);
            _offset += count;
            return count;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StallingServer(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingStream(content[..10])),
            });
    }

    /// <summary>
    /// A connection that goes quiet is given up on, and what arrived is kept to resume from.
    /// </summary>
    /// <remarks>
    /// The client's own timeout is infinite and has to be — it covers the whole request, and half a
    /// gigabyte on a slow line legitimately takes an hour. Without a per-read watchdog there is nothing
    /// left watching, and the download sits at 43% for ever behind a progress bar that still looks alive.
    /// </remarks>
    [Fact]
    public async Task A_download_that_goes_quiet_is_given_up_on_and_what_arrived_is_kept()
    {
        var content = Encoding.UTF8.GetBytes(new string('q', 5_000));
        var model = ModelFor(content);
        var store = new SpeechModelStore(_directory, () => new HttpClient(new StallingServer(content)),
            TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAsync<IOException>(() => store.DownloadAsync(model));

        Assert.False(store.IsDownloaded(model));
        Assert.Equal(10, new FileInfo(store.GetPath(model) + ".partial").Length);
    }

    [Fact]
    public async Task A_download_lands_where_the_engine_will_look_for_it()
    {
        var content = Encoding.UTF8.GetBytes("pretend this is half a gigabyte of model");
        var model = ModelFor(content);
        var store = StoreFor(new FakeServer(content));

        var reported = new List<double>();
        await store.DownloadAsync(model, new Progress<double>(reported.Add));

        Assert.True(store.IsDownloaded(model));
        Assert.Equal(content, File.ReadAllBytes(store.GetPath(model)));
        Assert.Contains(1.0, reported);
        Assert.False(File.Exists(store.GetPath(model) + ".partial"));
    }

    /// <summary>
    /// The digest is the whole reason for downloading to a temporary name. A file that fails it must
    /// never be left where something would load it — a corrupt half-gigabyte model is a native crash,
    /// not an error message.
    /// </summary>
    [Fact]
    public async Task A_payload_that_fails_its_digest_is_refused_and_thrown_away()
    {
        var content = Encoding.UTF8.GetBytes("this is not the model you asked for");
        var model = ModelFor(content, digest: new string('a', 64));
        var store = StoreFor(new FakeServer(content));

        await Assert.ThrowsAsync<InvalidDataException>(() => store.DownloadAsync(model));

        Assert.False(store.IsDownloaded(model));
        Assert.False(File.Exists(store.GetPath(model)));
        Assert.False(File.Exists(store.GetPath(model) + ".partial"));
    }

    [Fact]
    public async Task An_interrupted_download_resumes_from_what_is_already_on_disk()
    {
        var content = Encoding.UTF8.GetBytes(new string('x', 5_000));
        var model = ModelFor(content);
        File.WriteAllBytes(Path.Combine(_directory, model.FileName + ".partial"), content[..2_000]);

        var server = new FakeServer(content);
        await StoreFor(server).DownloadAsync(model);

        Assert.Equal(2_000, server.RequestedFrom);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(_directory, model.FileName)));
    }

    /// <summary>
    /// A server may ignore the range and send the whole file. Appending that to the part already there
    /// would produce a file of the right size made of the wrong bytes — which the digest would catch,
    /// after another full download.
    /// </summary>
    [Fact]
    public async Task A_server_that_ignores_the_range_starts_the_file_again_rather_than_appending()
    {
        var content = Encoding.UTF8.GetBytes(new string('y', 5_000));
        var model = ModelFor(content);
        File.WriteAllBytes(Path.Combine(_directory, model.FileName + ".partial"), content[..2_000]);

        await StoreFor(new FakeServer(content, honourRange: false)).DownloadAsync(model);

        Assert.Equal(content, File.ReadAllBytes(Path.Combine(_directory, model.FileName)));
    }

    /// <summary>
    /// A partial file the server will not resume from — its published size moved, or the file is a
    /// leftover from a different build — is thrown away and the download started again. Letting the 416
    /// out instead left that file in place, so every later attempt sent the same impossible range: the
    /// model became permanently un-downloadable, recoverable only by deleting a file by hand.
    /// </summary>
    [Fact]
    public async Task A_resume_the_server_refuses_starts_the_download_again()
    {
        var content = Encoding.UTF8.GetBytes(new string('z', 5_000));
        var model = ModelFor(content);
        var partial = Path.Combine(_directory, model.FileName + ".partial");
        File.WriteAllBytes(partial, content[..4_000]);

        var server = new FakeServer(content, refuseRange: true);
        var store = StoreFor(server);
        await store.DownloadAsync(model);

        Assert.Equal(2, server.Requests);
        Assert.True(store.IsDownloaded(model));
        Assert.Equal(content, File.ReadAllBytes(store.GetPath(model)));
        Assert.False(File.Exists(partial));
    }

    /// <summary>
    /// A 206 that starts somewhere other than where it was asked to is treated as no resume at all.
    /// </summary>
    /// <remarks>
    /// A proxy rewriting the request, or a mirror with its own idea of the offset. Trusting the status
    /// code alone appends that body to the partial file and produces a file of exactly the right length
    /// made of the wrong bytes — caught by the digest, but only after the whole download.
    /// </remarks>
    [Fact]
    public async Task A_partial_response_from_the_wrong_offset_is_not_appended()
    {
        var content = Encoding.UTF8.GetBytes(new string('w', 5_000));
        var model = ModelFor(content);
        File.WriteAllBytes(Path.Combine(_directory, model.FileName + ".partial"), content[..2_000]);

        var store = new SpeechModelStore(_directory, () => new HttpClient(new MisalignedServer(content)));
        await store.DownloadAsync(model);

        Assert.Equal(content, File.ReadAllBytes(store.GetPath(model)));
    }

    /// <summary>
    /// A misaligned 206 that carries a <em>fragment</em> and no length is asked for again from zero.
    /// </summary>
    /// <remarks>
    /// <para>The wrong-offset check used to answer this by setting the offset back to zero and writing
    /// the body as if it were the beginning of the file — which the length check catches for any server
    /// that sends one, and nothing catches for a chunked reply. A fragment from the middle written as a
    /// beginning is a corrupt <c>.partial</c> that the next resume then continues, so the digest fails
    /// after the whole download and the file is thrown away.</para>
    /// <para>Asking again from zero is the only thing that can start this download, and it is what the
    /// comment beside the check always claimed to do.</para>
    /// </remarks>
    [Fact]
    public async Task A_misaligned_partial_response_is_asked_for_again_from_the_start()
    {
        var content = Encoding.UTF8.GetBytes(new string('m', 5_000));
        var model = ModelFor(content);
        var partial = Path.Combine(_directory, model.FileName + ".partial");
        File.WriteAllBytes(partial, content[..2_000]);

        var server = new MisalignedFragmentServer(content);
        var store = new SpeechModelStore(_directory, () => new HttpClient(server));
        await store.DownloadAsync(model);

        Assert.Equal(2, server.Requests);                       // the bad one, then one from zero
        Assert.Equal(content, File.ReadAllBytes(store.GetPath(model)));
        Assert.False(File.Exists(partial));
    }

    /// <summary>Answers a range request with a fragment from somewhere else, claiming it starts at zero
    /// and saying nothing about its length; anything without a range it serves properly.</summary>
    private sealed class MisalignedFragmentServer(byte[] content) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            if (request.Headers.Range is null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(content),
                });

            // A chunked reply: StreamContent with the length taken back off, so nothing but the offset
            // itself says this body is in the wrong place.
            var message = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(new MemoryStream(content[2_500..])),
            };
            message.Content.Headers.ContentLength = null;
            message.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(0, content.Length - 1, content.Length);
            return Task.FromResult(message);
        }
    }

    /// <summary>
    /// Two callers asking for the same model at once produce one download, not two writers on one file.
    /// </summary>
    /// <remarks>
    /// There are two model lists over one store — the Speech tab and the setup wizard — each with its
    /// own row and its own "already downloading" flag, so nothing between them stops both starting. The
    /// second writer cannot even open the <c>.partial</c> file, and the user gets a Windows sharing
    /// message naming a path they have never seen. It waits instead, and then finds the model there.
    /// </remarks>
    [Fact]
    public async Task Two_callers_asking_for_one_model_produce_one_download()
    {
        var content = Encoding.UTF8.GetBytes(new string('p', 5_000));
        var model = ModelFor(content);
        var server = new GatedServer(content);
        var store = new SpeechModelStore(_directory, () => new HttpClient(server));

        var first = store.DownloadAsync(model);
        await server.Asked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = store.DownloadAsync(model);       // while the first is still in the air

        server.Release();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, server.Requests);              // the second waited and found it downloaded
        Assert.Equal(content, File.ReadAllBytes(store.GetPath(model)));
    }

    /// <summary>Serves the payload, but only once it is let go.</summary>
    private sealed class GatedServer(byte[] content) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Asked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Requests { get; private set; }

        public void Release() => _gate.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            Requests++;
            Asked.TrySetResult();
            await _gate.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        }
    }

    /// <summary>
    /// A server that keeps sending past the published size is cut off, and what arrived is thrown away.
    /// </summary>
    /// <remarks>
    /// The only thing bounding the write loop. Without it a server that is broken, hostile, or simply
    /// serving the wrong file writes until the disk fills — and the digest that would have caught it is
    /// never reached, because the loop never ends. Half a gigabyte is a plausible model; the same stream
    /// not stopping is not.
    /// </remarks>
    [Fact]
    public async Task A_server_sending_more_than_the_published_size_is_stopped()
    {
        var content = Encoding.UTF8.GetBytes(new string('o', 5_000));
        var model = ModelFor(content);
        var store = new SpeechModelStore(_directory,
            () => new HttpClient(new OversizedServer(Encoding.UTF8.GetBytes(new string('o', 200_000)))));

        await Assert.ThrowsAsync<InvalidDataException>(() => store.DownloadAsync(model));

        Assert.False(store.IsDownloaded(model));
        var partial = new FileInfo(Path.Combine(_directory, model.FileName + ".partial"));
        Assert.True(!partial.Exists || partial.Length <= model.DownloadBytes,
            "more than the published size was written to disk");
    }

    /// <summary>
    /// A length that disagrees with the catalogue is the wrong file, and it is refused before a byte of
    /// it is fetched rather than after all of it has failed the digest.
    /// </summary>
    [Fact]
    public async Task A_body_of_the_wrong_advertised_length_is_refused_before_it_is_fetched()
    {
        var content = Encoding.UTF8.GetBytes(new string('n', 5_000));
        var model = ModelFor(content) with { DownloadBytes = 9_999 };   // the catalogue says otherwise
        var server = new FakeServer(content);
        var store = StoreFor(server);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.DownloadAsync(model));

        Assert.False(File.Exists(Path.Combine(_directory, model.FileName + ".partial")));
        Assert.Equal(1, server.Requests);
    }

    /// <summary>
    /// A server that accepts the request and then says nothing is given up on, like one that stops
    /// mid-body.
    /// </summary>
    /// <remarks>
    /// The client's own timeout is infinite and has to be — half a gigabyte over a slow line takes an
    /// hour — so nothing watched the phase before the first byte. The download sat at 0% for as long as
    /// the application ran, looking like a slow connection.
    /// </remarks>
    [Fact]
    public async Task A_server_that_never_answers_is_given_up_on()
    {
        var content = Encoding.UTF8.GetBytes(new string('s', 5_000));
        var model = ModelFor(content);
        var store = new SpeechModelStore(_directory, () => new HttpClient(new SilentServer()),
            stallTimeout: TimeSpan.FromMilliseconds(150));

        var failure = await Assert.ThrowsAsync<IOException>(() => store.DownloadAsync(model));

        Assert.Contains("did not answer", failure.Message);
    }

    /// <summary>The user's own cancellation stays a cancellation, whatever the deadline is doing.</summary>
    [Fact]
    public async Task Cancelling_while_waiting_for_the_server_is_a_cancellation()
    {
        var content = Encoding.UTF8.GetBytes(new string('c', 5_000));
        var model = ModelFor(content);
        var store = new SpeechModelStore(_directory, () => new HttpClient(new SilentServer()),
            stallTimeout: TimeSpan.FromSeconds(30));

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.DownloadAsync(model, cancellationToken: cancel.Token));
    }

    /// <summary>Accepts the request and never answers it.</summary>
    private sealed class SilentServer : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new System.Diagnostics.UnreachableException();
        }
    }

    /// <summary>Sends far more than it was asked to, in one body with no length declared.</summary>
    private sealed class OversizedServer(byte[] tooMuch) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var message = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(tooMuch)),
            };
            message.Content.Headers.ContentLength = null;
            return Task.FromResult(message);
        }
    }

    /// <summary>Answers 206 — from the beginning, whatever was asked for.</summary>
    private sealed class MisalignedServer(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var message = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(content),
            };
            message.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(0, content.Length - 1, content.Length);
            return Task.FromResult(message);
        }
    }

    /// <summary>
    /// A <c>.partial</c> that is already whole is verified and kept, not thrown away.
    /// </summary>
    /// <remarks>
    /// It is a download interrupted between its last byte and the digest check — the file is there and
    /// was about to be accepted. Resuming skipped it (there is nothing left to fetch) and the write
    /// opened it with <c>FileMode.Create</c>, so half a gigabyte was truncated and fetched again.
    /// </remarks>
    [Fact]
    public async Task A_partial_file_that_is_already_complete_is_verified_rather_than_re_downloaded()
    {
        var content = Encoding.UTF8.GetBytes(new string('c', 5_000));
        var model = ModelFor(content);
        File.WriteAllBytes(Path.Combine(_directory, model.FileName + ".partial"), content);

        var server = new FakeServer(content);
        var store = StoreFor(server);
        await store.DownloadAsync(model);

        Assert.Equal(0, server.Requests);
        Assert.True(store.IsDownloaded(model));
        Assert.Equal(content, File.ReadAllBytes(store.GetPath(model)));
    }

    /// <summary>Whole and wrong is not resumable either: there is nothing to add to it, so it goes and
    /// the download starts again.</summary>
    [Fact]
    public async Task A_complete_partial_file_that_fails_its_digest_is_replaced()
    {
        var content = Encoding.UTF8.GetBytes(new string('d', 5_000));
        var model = ModelFor(content);
        File.WriteAllBytes(Path.Combine(_directory, model.FileName + ".partial"),
            Encoding.UTF8.GetBytes(new string('!', 5_000)));

        var server = new FakeServer(content);
        var store = StoreFor(server);
        await store.DownloadAsync(model);

        Assert.Equal(1, server.Requests);
        Assert.Null(server.RequestedFrom);          // from the beginning, not resumed
        Assert.Equal(content, File.ReadAllBytes(store.GetPath(model)));
    }

    /// <summary>
    /// A connection that ends early keeps what arrived, and the attempt after it picks up from there.
    /// </summary>
    /// <remarks>
    /// A dropped route reaches the read loop as a clean end of stream — there is nothing to tell it from
    /// success except the byte count. Without that check the short file went to the digest, failed it,
    /// and was <em>deleted</em>: an interruption at 90% threw away 90% of a download, which is exactly
    /// what resuming exists to prevent. One test, because keeping the bytes is only worth anything if
    /// the next attempt then asks for the rest of them.
    /// </remarks>
    [Fact]
    public async Task A_download_that_ends_early_keeps_what_arrived_and_the_next_attempt_finishes_it()
    {
        var content = Encoding.UTF8.GetBytes(new string('e', 5_000));
        var model = ModelFor(content);
        var truncating = new SpeechModelStore(_directory,
            () => new HttpClient(new TruncatingServer(content, 3_000)));

        await Assert.ThrowsAsync<IOException>(() => truncating.DownloadAsync(model));

        var partial = new FileInfo(truncating.GetPath(model) + ".partial");
        Assert.True(partial.Exists, "the partial file was deleted");
        Assert.Equal(3_000, partial.Length);
        Assert.False(truncating.IsDownloaded(model));

        var server = new FakeServer(content);
        var store = StoreFor(server);
        await store.DownloadAsync(model);

        Assert.Equal(3_000, server.RequestedFrom);          // it carried on rather than starting again
        Assert.Equal(content, File.ReadAllBytes(store.GetPath(model)));
    }

    /// <summary>
    /// Answers without a length and then stops early — a connection dropping mid-body.
    /// </summary>
    /// <remarks>
    /// Deliberately not a short <c>ByteArrayContent</c>: that declares its length, and the store refuses
    /// a Content-Length that disagrees with the catalogue before writing a byte. This is the other case,
    /// the one that reaches the read loop and looks exactly like a finished download.
    /// </remarks>
    private sealed class TruncatingServer(byte[] content, int cut) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new EndsEarlyStream(content[..cut])),
            });
    }

    private sealed class EndsEarlyStream(byte[] body) : Stream
    {
        private int _offset;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = Math.Min(count, body.Length - _offset);
            if (take <= 0)
                return 0;

            Array.Copy(body, _offset, buffer, offset, take);
            _offset += take;
            return take;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_model_already_on_disk_is_not_fetched_again()
    {
        var content = Encoding.UTF8.GetBytes("already here");
        var model = ModelFor(content);
        File.WriteAllBytes(Path.Combine(_directory, model.FileName), content);

        var server = new FakeServer(content);
        await StoreFor(server).DownloadAsync(model);

        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task An_archive_is_unpacked_and_the_archive_removed()
    {
        // A whole model: the store now calls an archive unpacked only when every file the engine opens
        // is there, so a fixture with one graph in it is a half-extracted model, not a small one.
        var archive = TarGzFixture.Build(
            ("model/vocab.txt", "<blk> 0\n"),
            ("model/nemo128.onnx", "preprocessor"),
            ("model/encoder-model.int8.onnx", "encoder"),
            ("model/decoder_joint-model.int8.onnx", "decoder"));
        var model = ModelFor(archive, "model-dir", SpeechModelKind.ParakeetOnnx);
        var store = StoreFor(new FakeServer(archive));

        await store.DownloadAsync(model);

        Assert.True(store.IsDownloaded(model));
        Assert.Equal("encoder", File.ReadAllText(Path.Combine(store.GetPath(model), "encoder-model.int8.onnx")));
        Assert.False(File.Exists(Path.Combine(_directory, model.DownloadFileName)));
    }

    /// <summary>
    /// The path back from an unpacking that failed: the archive is still there and already verified, so
    /// it is expanded rather than fetched again. Losing an hour of somebody's bandwidth to a bug in the
    /// unpacking is how this was learned.
    /// </summary>
    [Fact]
    public async Task A_verified_archive_still_on_disk_is_unpacked_without_touching_the_network()
    {
        // A whole model: the store now calls an archive unpacked only when every file the engine opens
        // is there, so a fixture with one graph in it is a half-extracted model, not a small one.
        var archive = TarGzFixture.Build(
            ("model/vocab.txt", "<blk> 0\n"),
            ("model/nemo128.onnx", "preprocessor"),
            ("model/encoder-model.int8.onnx", "encoder"),
            ("model/decoder_joint-model.int8.onnx", "decoder"));
        var model = ModelFor(archive, "model-dir", SpeechModelKind.ParakeetOnnx);
        File.WriteAllBytes(Path.Combine(_directory, model.DownloadFileName), archive);

        var server = new FakeServer(archive);
        var store = StoreFor(server);
        await store.DownloadAsync(model);

        Assert.Equal(0, server.Requests);
        Assert.True(store.IsDownloaded(model));
    }
}

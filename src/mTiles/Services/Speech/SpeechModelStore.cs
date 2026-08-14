using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace mTiles.Services.Speech;

/// <summary>
/// The downloaded models on disk: where they are, getting them, and getting rid of them.
/// </summary>
/// <remarks>
/// Downloads land in <c>&lt;file&gt;.partial</c> and are moved into place only once the digest matches,
/// so an interrupted download is resumable and a corrupt one is never loaded. Handy does the same
/// (<c>managers/model/download.rs</c>) — with files this size, "download it again" is not a recovery plan.
/// </remarks>
public sealed class SpeechModelStore
{
    private readonly string _directory;
    private readonly Func<HttpClient> _httpClientFactory;
    private readonly TimeSpan _stallTimeout;

    /// <param name="stallTimeout">Overridable so a test can watch a stalled download fail in
    /// milliseconds rather than in the minute a real one is given.</param>
    public SpeechModelStore(string? directory = null, Func<HttpClient>? httpClientFactory = null,
        TimeSpan? stallTimeout = null)
    {
        _directory = directory ?? AppPaths.GetSpeechModelsDirectory();
        _httpClientFactory = httpClientFactory ?? (() => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        _stallTimeout = stallTimeout ?? DefaultStallTimeout;
    }

    public string Directory => _directory;

    /// <summary>What the engine is given: the model file, or the directory it was unpacked into.</summary>
    public string GetPath(SpeechModel model) => Path.Combine(_directory, model.FileName);

    private string GetDownloadPath(SpeechModel model) => Path.Combine(_directory, model.DownloadFileName);

    /// <summary>
    /// True when the model is on disk and usable.
    /// <para>Answered by <see cref="SpeechEngines"/>, because "downloaded" has to mean "the engine could
    /// load this" and nothing else — a directory holding two of four graphs is not a model, however much
    /// of it arrived. Judging it here would put the list of a model's files in a class about HTTP and
    /// file names, one edit away from disagreeing with the loader that opens them.</para>
    /// </summary>
    public bool IsDownloaded(SpeechModel model) => SpeechEngines.IsComplete(model, GetPath(model));

    /// <summary>
    /// Removes a model from disk.
    /// </summary>
    /// <returns>
    /// False when it is still there afterwards — which happens for a real and unobvious reason: while a
    /// model is loaded the engine holds its files open, and Windows refuses to delete them. Swallowing
    /// that left the user clicking a button that did nothing and said nothing. Also false when a
    /// download of this model is under way and does not finish within <see cref="DeleteWait"/>.
    /// </returns>
    /// <remarks>
    /// Behind the same gate as the download, so one model's files have one operation on them at a time.
    /// Deleting into a running download otherwise removes the archive the download is about to move into
    /// place — or the <c>.partial</c> it is writing, which it cannot, so the delete reports success on a
    /// file that is still growing. It waits rather than refusing outright, because the overlap this
    /// guards against is a race of milliseconds; and it gives up rather than waiting on the download,
    /// because that one legitimately takes an hour.
    /// </remarks>
    public bool Delete(SpeechModel model)
    {
        var queue = InFlight(GetDownloadPath(model));
        if (!queue.Wait(DeleteWait))
        {
            Trace.TraceWarning("Not deleting {0}: something else is working on its files.", model.Id);
            return false;
        }

        try { return DeleteCore(model); }
        finally { queue.Release(); }
    }

    /// <summary>How long a delete waits for whatever else is holding this model's files.</summary>
    private static readonly TimeSpan DeleteWait = TimeSpan.FromSeconds(5);

    private bool DeleteCore(SpeechModel model)
    {
        FileHelper.TryDelete(GetDownloadPath(model));
        FileHelper.TryDelete(GetDownloadPath(model) + ".partial");

        if (model.IsArchive)
        {
            // The staging and set-aside directories too: an extraction that failed leaves one behind, and
            // each holds the best part of an unpacked model — hundreds of megabytes nothing will ever
            // look at again.
            foreach (var directory in new[]
                     { GetPath(model), GetPath(model) + ".unpacking", GetPath(model) + ".old" })
            {
                try
                {
                    if (System.IO.Directory.Exists(directory))
                        System.IO.Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning("Deleting {0} failed: {1}", directory, ex.Message);
                }
            }
        }

        // Whether the files are *gone*, not whether the model is still loadable. Those stopped being the
        // same question when IsDownloaded got strict: a delete that removed two graphs and then hit a
        // locked third one leaves a model that cannot be loaded and half a gigabyte still on the disk,
        // which is the exact case this return value exists to report.
        return model.IsArchive
            ? !System.IO.Directory.Exists(GetPath(model))
            : !File.Exists(GetPath(model));
    }

    /// <summary>
    /// Downloads <paramref name="model"/>, resuming a previous attempt when the server allows it.
    /// </summary>
    /// <remarks>
    /// Nothing here touches the caller's thread after the first await: every continuation is
    /// <c>ConfigureAwait(false)</c> and the unpacking runs on the thread pool. That is not tidiness —
    /// hashing half a gigabyte and expanding it to 640 MB on disk are seconds of solid work, and on the
    /// UI thread they would freeze the window during the one operation the user is watching. Progress
    /// still arrives where it is drawn: <see cref="Progress{T}"/> captures the context it was built on.
    /// </remarks>
    /// <param name="progress">Fraction downloaded, 0 to 1.</param>
    public async Task DownloadAsync(SpeechModel model, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // One download of a given model at a time, whoever asked. There are two lists of models on
        // screen — the Speech tab and the setup wizard — over this one store, each with its own row and
        // its own "already downloading" flag, so nothing between them stops both starting the same file.
        // Two writers to one .partial is not a race the digest would catch: the second cannot even open
        // the file (FileShare.None) and fails with a Windows sharing message about a path the user never
        // typed. The second caller waits and then finds the model downloaded, which is what it asked for.
        var queue = InFlight(GetDownloadPath(model));
        await queue.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DownloadCoreAsync(model, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            queue.Release();
        }
    }

    /// <summary>One gate per file on disk, so two models never wait on each other.</summary>
    /// <remarks>
    /// Never removed: there are six models in the catalogue, and dropping an entry the moment nobody
    /// holds it is how two callers end up with two different semaphores for one file.
    /// </remarks>
    private readonly Dictionary<string, SemaphoreSlim> _inFlight =
        new(StringComparer.FromComparison(FileHelper.PathComparison));

    private SemaphoreSlim InFlight(string path)
    {
        lock (_inFlight)
        {
            if (!_inFlight.TryGetValue(path, out var queue))
                _inFlight[path] = queue = new SemaphoreSlim(1, 1);
            return queue;
        }
    }

    private async Task DownloadCoreAsync(SpeechModel model, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(_directory);

        var target = GetDownloadPath(model);
        var partial = target + ".partial";
        if (IsDownloaded(model))
            return;

        // A complete archive still sitting there is a download that already happened: unpack it rather
        // than fetch half a gigabyte again. This is the path back from an unpacking that failed.
        if (model.IsArchive && new FileInfo(target) is { Exists: true } archive
            && archive.Length == model.DownloadBytes)
        {
            var existingDigest = await ComputeSha256Async(target, cancellationToken).ConfigureAwait(false);
            if (string.Equals(existingDigest, model.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await Task.Run(() => Unpack(model, target), cancellationToken).ConfigureAwait(false);
                progress?.Report(1);
                return;
            }

            FileHelper.TryDelete(target);
        }

        // A .partial that is already the full size is a download interrupted between its last byte and
        // the digest check — the file is there, whole, and was about to be accepted. Verifying it costs
        // a second of hashing; the alternative was FileMode.Create truncating it and fetching half a
        // gigabyte again, which is the same mistake as throwing away a verified archive.
        if (new FileInfo(partial) is { Exists: true } complete && complete.Length >= model.DownloadBytes)
        {
            if (await MatchesDigestAsync(partial, model, cancellationToken).ConfigureAwait(false))
            {
                await AdoptAsync(model, partial, target, cancellationToken).ConfigureAwait(false);
                progress?.Report(1);
                return;
            }

            // Whole, and wrong: a truncated mirror, a file from an older build. Nothing to resume from.
            FileHelper.TryDelete(partial);
        }

        var resumeFrom = new FileInfo(partial) is { Exists: true } existing && existing.Length < model.DownloadBytes
            ? existing.Length
            : 0;

        using var client = _httpClientFactory();

        // Declared out here because how much arrived is what decides, after the loop, whether this was a
        // finished download or an interrupted one.
        long written;
        var response = await RequestAsync(client, model, resumeFrom, cancellationToken).ConfigureAwait(false);
        try
        {
            // 416: the partial file is at or past the end of what the server will serve — a published
            // size that moved, or a leftover from a different build. Throwing here left that file in
            // place, so every future attempt sent the same impossible range and the model became
            // permanently un-downloadable, with nothing on screen to suggest deleting a file by hand.
            if (resumeFrom > 0 && response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                Trace.TraceWarning("Resuming {0} was refused (416); starting the download again.", model.Id);
                response.Dispose();
                FileHelper.TryDelete(partial);
                resumeFrom = 0;
                response = await RequestAsync(client, model, 0, cancellationToken).ConfigureAwait(false);
            }

            // Two ways a resume can go wrong, and they need different answers.
            //
            // Ignored — a plain 200 where a 206 was asked for — is easy: the body *is* the whole file, so
            // writing it from zero is exactly right. What must not happen is appending it to the partial
            // one, which makes a file of the right length out of the wrong bytes.
            if (resumeFrom > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                resumeFrom = 0;

            // Answered from somewhere else — a 206 whose Content-Range starts at an offset nobody asked
            // for, from a proxy rewriting the request or a mirror with its own idea of it — is not a
            // whole file, so there is nothing to keep and nothing to write it into. Only asking again
            // from zero can start this download; forgetting the offset and writing the fragment as if it
            // were a beginning was caught by the length check on the next line in every case a server
            // sends one, and by nothing at all in the case it does not (a chunked 206).
            if (resumeFrom > 0 && response.Content.Headers.ContentRange?.From != resumeFrom)
            {
                Trace.TraceWarning("Resuming {0} was answered from offset {1} rather than {2}; " +
                    "starting the download again.", model.Id,
                    response.Content.Headers.ContentRange?.From, resumeFrom);
                response.Dispose();
                FileHelper.TryDelete(partial);
                resumeFrom = 0;
                // Only the headers have been read so far, so this costs a round trip and no body.
                response = await RequestAsync(client, model, 0, cancellationToken).ConfigureAwait(false);
            }
            response.EnsureSuccessStatusCode();

            // A length that disagrees with the catalogue is the wrong file, and finding that out now
            // saves fetching all of it to fail the digest. Absent (chunked) is fine — the write loop
            // enforces the size either way.
            var expected = model.DownloadBytes - resumeFrom;
            if (response.Content.Headers.ContentLength is { } advertised && advertised != expected)
                throw new InvalidDataException(
                    $"The server offers {advertised} bytes for {model.Name} where {expected} were expected.");

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = new FileStream(partial,
                resumeFrom > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

            var buffer = new byte[1 << 16];
            written = resumeFrom;
            var reported = -1L;
            int read;
            while ((read = await ReadWithStallTimeoutAsync(source, buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                // The published size is the contract, and it is the only bound on this loop: a server
                // that keeps sending — broken, hostile, or serving the wrong file — would otherwise
                // write until the disk filled, with the digest that would have caught it never reached.
                // Half a gigabyte is a plausible model; the same stream not stopping is not.
                if (written + read > model.DownloadBytes)
                    throw new InvalidDataException(
                        $"The server is sending more than the published size of {model.Name} " +
                        $"({model.DownloadBytes} bytes); the download was stopped.");

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;

                // A tenth of a percent at a time. Reporting every 64 KB buffer meant ~7 300 hops to the
                // UI thread for one Parakeet download, none of which could move a progress bar by a
                // visible amount.
                var tenths = written * 1000 / Math.Max(1, model.DownloadBytes);
                if (tenths == reported)
                    continue;

                reported = tenths;
                progress?.Report(Math.Clamp((double)written / model.DownloadBytes, 0, 1));
            }
        }
        finally
        {
            response.Dispose();
        }

        // Short means the connection ended early, not that the file is wrong — a dropped route reaches
        // the read loop as a clean end of stream, indistinguishable from success without this. Falling
        // through to the digest failed it and *deleted the partial file*, so an interruption at 90%
        // threw away 90% of a download the resume logic exists to save. What is on disk stays; the next
        // attempt continues from it.
        if (written < model.DownloadBytes)
            throw new IOException(
                $"The download of {model.Name} ended early at {written} of {model.DownloadBytes} bytes; " +
                "what arrived is kept and the next attempt will resume from it.");

        if (!await MatchesDigestAsync(partial, model, cancellationToken).ConfigureAwait(false))
        {
            FileHelper.TryDelete(partial);
            throw new InvalidDataException(
                $"The downloaded file does not match the published checksum for {model.Name}.");
        }

        await AdoptAsync(model, partial, target, cancellationToken).ConfigureAwait(false);
        progress?.Report(1);
    }

    private static async Task<bool> MatchesDigestAsync(string path, SpeechModel model,
        CancellationToken cancellationToken)
    {
        var digest = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        return string.Equals(digest, model.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Moves a verified download into place, unpacking it if it is an archive.</summary>
    private async Task AdoptAsync(SpeechModel model, string verified, string target,
        CancellationToken cancellationToken)
    {
        FileHelper.TryDelete(target);
        File.Move(verified, target);

        if (model.IsArchive)
            await Task.Run(() => Unpack(model, target), cancellationToken).ConfigureAwait(false);

        Trace.WriteLine($"[speech] model {model.Id} ready at {GetPath(model)}");
    }

    /// <summary>
    /// How long a download may go without a single byte arriving before it is given up on.
    /// </summary>
    /// <remarks>
    /// The client's own timeout is infinite, and has to be: it covers the whole request, and half a
    /// gigabyte over a slow line legitimately takes an hour. That leaves nothing watching a connection
    /// that simply stops — a dropped route, a proxy holding the socket open — and the download sits at
    /// 43% for ever with a progress bar that looks like it is still working. Per read, so a slow
    /// connection is never punished: only a silent one is.
    /// </remarks>
    private static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(60);

    private async Task<int> ReadWithStallTimeoutAsync(Stream source, byte[] buffer,
        CancellationToken cancellationToken)
    {
        var read = source.ReadAsync(buffer, cancellationToken).AsTask();
        try
        {
            return await read.WaitAsync(_stallTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The read itself is still out there and will fault when the stream is closed underneath
            // it. Nobody is left to await it, so its exception would surface later as an unobserved
            // task exception — a crash-log entry with no connection to the download that caused it.
            _ = read.ContinueWith(static faulted => _ = faulted.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

            // What is on disk stays: the next attempt resumes from it, which is the entire reason the
            // download goes to a .partial file.
            throw new IOException(
                $"The download stopped responding — nothing arrived for {_stallTimeout.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>
    /// One GET, from <paramref name="from"/> bytes in. Awaited here rather than returned as a task, so
    /// the request object outlives the send.
    /// </summary>
    /// <remarks>
    /// The <b>headers</b> get the same deadline the body's reads do, and they need their own because the
    /// client's overall timeout is infinite — it has to be, since half a gigabyte over a slow line
    /// legitimately takes an hour. That left the one phase where nothing has arrived yet with nothing
    /// watching it: a server that accepts the connection and then says nothing holds the download at 0%
    /// for as long as the application runs, with a progress bar that looks like it is still working and
    /// only Cancel to get out of. Cancelling the token rather than abandoning the task, so the socket
    /// goes with it.
    /// </remarks>
    private async Task<HttpResponseMessage> RequestAsync(HttpClient client, SpeechModel model, long from,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, model.Url);
        if (from > 0)
            request.Headers.Range = new RangeHeaderValue(from, null);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_stallTimeout);

        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Ours, not the user's: the user cancelling is a cancellation and has to stay one, or the
            // row reports "the download failed" for a button the user pressed on purpose.
            throw new IOException(
                $"The server did not answer within {_stallTimeout.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>
    /// Unpacks a model archive into its own directory and removes the archive.
    /// </summary>
    /// <remarks>
    /// <para>Into a staging directory first, then moved into place, so an extraction that fails halfway
    /// cannot leave something that looks downloaded.</para>
    /// <para>The archive is deleted <b>only once the model is in place</b> — it is another half a
    /// gigabyte of exactly what now sits beside it. Never on the way out of a failure: an archive whose
    /// digest already matched is a download that took the better part of an hour, and throwing it away
    /// because the unpacking needs fixing means fetching all of it again to try the fix.</para>
    /// </remarks>
    private void Unpack(SpeechModel model, string archivePath)
    {
        var destination = GetPath(model);
        var staging = destination + ".unpacking";

        try
        {
            if (System.IO.Directory.Exists(staging))
                System.IO.Directory.Delete(staging, recursive: true);
            System.IO.Directory.CreateDirectory(staging);

            TarGzExtractor.ExtractToDirectory(archivePath, staging);

            // Archives of this shape hold a single top-level directory; the model files are inside it.
            var root = System.IO.Directory.EnumerateFileSystemEntries(staging).ToList();
            var source = root.Count == 1 && System.IO.Directory.Exists(root[0]) ? root[0] : staging;

            // The one that is there is moved aside rather than deleted, and only removed once the new one
            // is in place. Deleting first leaves a window — seconds, for a directory of this size — in
            // which a failed move means there is no model at all: the old one gone, the new one still in
            // staging, and a user who asked to re-download a model they already had left with none.
            var previous = destination + ".old";
            if (System.IO.Directory.Exists(previous))
                System.IO.Directory.Delete(previous, recursive: true);
            if (System.IO.Directory.Exists(destination))
                System.IO.Directory.Move(destination, previous);

            try
            {
                System.IO.Directory.Move(source, destination);
            }
            catch
            {
                // Put it back. Whatever was wrong with the new one, the user is no worse off than before.
                if (!System.IO.Directory.Exists(destination) && System.IO.Directory.Exists(previous))
                    System.IO.Directory.Move(previous, destination);
                throw;
            }

            if (System.IO.Directory.Exists(previous))
                System.IO.Directory.Delete(previous, recursive: true);
        }
        finally
        {
            if (System.IO.Directory.Exists(staging))
            {
                try { System.IO.Directory.Delete(staging, recursive: true); } catch { /* temporary */ }
            }
        }

        FileHelper.TryDelete(archivePath);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 16, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

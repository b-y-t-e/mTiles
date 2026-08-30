using System.Net;
using mTiles.Services.Database;

namespace mTiles.Services.Providers;

/// <summary>
/// Looking for a local model server on this machine and on this network.
/// </summary>
/// <remarks>
/// <para>Reuses <see cref="SubnetScanner"/>, with three differences from the database version that are
/// decisions rather than details.</para>
/// <para><b>On demand, never on a timer.</b> The database scan runs every half hour; sweeping a
/// corporate network on a schedule looks like reconnaissance, and a "Search the network" button is
/// enough for something somebody sets up once.</para>
/// <para><b>Verified by protocol, never by port.</b> An open 11434 is not proof of Ollama, so every
/// candidate is asked <see cref="ILocalAiProvider.IsServingAsync"/> before it is offered.</para>
/// <para><b>It will usually find nothing,</b> and whatever shows the result has to say so: Ollama binds
/// <c>127.0.0.1</c> unless <c>OLLAMA_HOST=0.0.0.0</c>, and LM Studio needs its server started and
/// "Serve on Local Network" enabled. Without that sentence the feature reads as broken.</para>
/// </remarks>
public static class LocalProviderDiscovery
{
    /// <summary>How long a port is given to answer before the address is passed over. A local network
    /// answers in single-digit milliseconds; anything slower is a firewall dropping the packet, and
    /// waiting longer only makes the sweep longer.</summary>
    private const int PortTimeoutMs = 300;

    /// <summary>How many addresses are probed at once. The same width the database discovery sweeps at:
    /// each probe is a socket waiting, so the limit is not this machine's cores.</summary>
    private const int ScanWidth = 32;

    /// <summary>
    /// Where this provider is serving, loopback first.
    /// </summary>
    /// <remarks>Loopback first because it is the answer nearly every time and it costs one call, so a
    /// user who is running the server on this machine never waits for a network sweep to finish.</remarks>
    public static async Task<IReadOnlyList<Uri>> FindAsync(ILocalAiProvider provider,
        bool includeNetwork, CancellationToken ct = default)
    {
        var found = new List<Uri>();

        foreach (var address in SubnetScanner.GetLoopbackAddresses())
            await AddIfServing(provider, address, found, ct).ConfigureAwait(false);

        if (!includeNetwork)
            return found;

        foreach (var address in await ScanNetworkAsync(provider, ct).ConfigureAwait(false))
            await AddIfServing(provider, address, found, ct).ConfigureAwait(false);

        return found;
    }

    /// <summary>Asks one address whether it is serving, and keeps it if it is.</summary>
    private static async Task AddIfServing(ILocalAiProvider provider, IPAddress address,
        List<Uri> found, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var url = new Uri($"http://{Host(address)}:{provider.DefaultPort}/");
        if (await provider.IsServingAsync(url, ct).ConfigureAwait(false))
            found.Add(url);
    }

    /// <summary>The network addresses worth asking: whatever has the port open.</summary>
    /// <remarks><para>The port scan is a filter and not the answer — it is what keeps the protocol check
    /// to a handful of calls instead of one per address in the subnet.</para>
    /// <para><b>It runs on a thread pool thread, all of it.</b> <see cref="SubnetScanner.ScanPort"/>
    /// blocks for up to <see cref="PortTimeoutMs"/> per address, and this is called from the Settings
    /// dialog. A lazy sequence would not have been enough: interleaved with an <c>await</c> that
    /// captures the UI context, every step after the first would resume on the UI thread and freeze the
    /// window.</para>
    /// <para><b>And it is scanned in parallel,</b> the way the database sweep already is: a filtered
    /// /24 is 254 addresses × <see cref="PortTimeoutMs"/>, which is over a minute per subnet in a row —
    /// so somebody who pressed a button and is watching a spinner waits minutes for an answer that is
    /// nearly always "nothing here". The waits are a socket timing out and nothing else, so the width is
    /// the same <see cref="ScanWidth"/> the database scan uses.</para>
    /// <para>The order the addresses come back in is the order of the subnet, not the order they
    /// answered in: what is offered first should not depend on which socket happened to be quickest.
    /// </para>
    /// </remarks>
    private static Task<List<IPAddress>> ScanNetworkAsync(ILocalAiProvider provider, CancellationToken ct) =>
        Task.Run(() =>
        {
            var candidates = SubnetScanner.GetLocalSubnets()
                .SelectMany(SubnetScanner.GetAddressesInSubnet)
                .ToArray();

            var isOpen = new bool[candidates.Length];

            Parallel.For(0, candidates.Length,
                new ParallelOptions { MaxDegreeOfParallelism = ScanWidth, CancellationToken = ct },
                index => isOpen[index] =
                    SubnetScanner.ScanPort(candidates[index], provider.DefaultPort, PortTimeoutMs));

            return candidates.Where((_, index) => isOpen[index]).ToList();
        }, ct);

    /// <summary>An IPv6 address has to be bracketed before it is a host in a URL.</summary>
    private static string Host(IPAddress address) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
}

using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services.Providers;

/// <summary>
/// OpenRouter — one key in front of everybody else's models, and the only provider here that answers
/// what is left on it.
/// </summary>
/// <remarks>
/// <para>Three flavors, because it fronts all three: what actually works then depends on the model
/// chosen behind it, which is a thing this application cannot know and must not pretend to. Its
/// Anthropic-compatible endpoint in particular is <b>not a drop-in</b> for everything Claude Code does
/// — cache control, token counting and parts of tool streaming — so some combinations fail, and the
/// tile has to say which rather than reporting that "the AI tool reported a failure".</para>
/// </remarks>
public sealed class OpenRouterProvider : AiProvider
{
    public override string Id => "openrouter";
    public override string DisplayName => "OpenRouter";

    public override IReadOnlyList<ApiFlavor> ApiFlavors =>
        [ApiFlavor.OpenAiChatCompletions, ApiFlavor.OpenAiResponses, ApiFlavor.Anthropic];

    public override Uri? DefaultBaseUrl => new("https://openrouter.ai/");

    /// <summary>The spelling every catalogue reads this service's key from.</summary>
    public override string? KeyEnvironmentVariable => "OPENROUTER_API_KEY";

    /// <summary>
    /// All three shapes are served under <c>api/v1</c> — but the version is part of the base address
    /// for one of them and part of the path for the other two, so the answer differs by flavor.
    /// </summary>
    /// <remarks>
    /// <para><b>Measured 2026-08-31.</b> An OpenAI-shaped client is pointed at <c>api/v1</c> and appends
    /// <c>/chat/completions</c>; Claude Code is pointed at an API <em>root</em> and appends
    /// <c>/v1/messages</c> itself — the same convention as <c>https://api.anthropic.com</c>. One answer
    /// for all three therefore sent Claude Code to
    /// <c>https://openrouter.ai/api/v1/v1/messages</c>, which is a <b>404</b> where
    /// <c>https://openrouter.ai/api/v1/messages</c> is a 401 without a key.</para>
    /// <para>What made it expensive to find is what the 404 comes back as: Claude Code reports it as
    /// <i>"There's an issue with the selected model — it may not exist or you may not have access to
    /// it"</i>, so a correct model id on a working key reads as a model that is neither. This is the
    /// reason <see cref="AiProvider.EndpointFor"/> takes a flavor at all, and <c>ZaiProvider</c> is the
    /// same rule applied to a provider whose two paths were different enough to notice.</para>
    /// </remarks>
    public override Uri? EndpointFor(ApiFlavor flavor, AiProviderInstance instance) =>
        BaseUrlFor(instance) is not { } baseUrl
            ? null
            : flavor switch
            {
                ApiFlavor.Anthropic => new Uri(baseUrl, "api"),
                ApiFlavor.OpenAiChatCompletions or ApiFlavor.OpenAiResponses =>
                    new Uri(baseUrl, "api/v1"),
                _ => null,
            };

    /// <summary>The key endpoint, which answers with the balance as well as with whether the key is a
    /// key at all.</summary>
    public override async Task<ProviderCheck> TestAsync(AiProviderInstance instance,
        CancellationToken ct = default)
    {
        if (AddressProblem(instance) is { } problem) return problem;

        using var document = await GetJsonAsync(instance, "api/v1/key", ct);
        if (document is null)
            return ProviderCheck.Failed("OpenRouter did not accept this key.");

        return ProviderCheck.Reached("key accepted", BalanceIn(document));
    }

    /// <summary>
    /// What is left on the key, or null where the answer does not carry it.
    /// </summary>
    /// <remarks>An unlimited key has no remaining figure at all, which is <c>null</c> and not zero — the
    /// distinction the whole tri-state exists for, since the two would otherwise read identically to a
    /// user whose key works perfectly well.</remarks>
    private static decimal? BalanceIn(JsonDocument document) => Money(Data(document), "limit_remaining");

    /// <summary>
    /// The <c>data</c> object of an answer, or nothing at all where there is none.
    /// </summary>
    /// <remarks><b><see cref="JsonElement.TryGetProperty(string, out JsonElement)"/> throws on anything
    /// that is not an object</b> — it is a lookup, not a test — so asking it about a root that came back
    /// as an array, a string or <c>null</c> is an <see cref="InvalidOperationException"/> out of a method
    /// documented never to throw. A proxy or a captive portal answering 200 with something that is not
    /// this service's shape is exactly the "could not be asked" case the report type exists to draw, and
    /// it was instead the case that dropped the card without a word. The kind is checked once here and
    /// every reader below is handed an element that is safe to ask.</remarks>
    private static JsonElement Data(JsonDocument? document) =>
        document?.RootElement is { ValueKind: JsonValueKind.Object } root
        && root.TryGetProperty("data", out var data)
            ? data
            : default;

    /// <summary>How much of the week the key has spent, and what is left on it.</summary>
    /// <remarks>
    /// <para><b>Money, not a percentage, and the two must not share a bar.</b> A subscription is spent
    /// out of a window; a key is spent out of a balance, and there is no rate that converts one into the
    /// other — inventing one would put a figure on screen this service never said.</para>
    /// <para><c>api/v1/key</c> carries the rolling totals and the balance where a limit is set;
    /// <c>api/v1/credits</c> carries the balance where one is not, which is the ordinary case for a
    /// topped-up key. Asked in that order and only when the first leaves the balance unknown, because a
    /// key that answers everything should not cost two calls.</para>
    /// <para><b>There is no per-day history to fetch.</b> <c>api/v1/activity</c> answers 403 for a
    /// normal key (<i>Only management keys can fetch activity</i>), so the seven days on the card come
    /// from this application's own daily snapshots — see <c>UsageHistory</c> — and the tile says as
    /// much rather than drawing empty days as free ones.</para>
    /// </remarks>
    public override async Task<AiUsageReport?> UsageAsync(AiProviderInstance instance,
        CancellationToken ct = default)
    {
        var measuredAt = DateTimeOffset.Now;
        var sourceId = UsageSourceId(instance);

        if (AddressProblem(instance) is { } addressProblem)
            return AiUsageReport.Failed(sourceId, instance.Name, addressProblem.Message, measuredAt);

        using var key = await GetJsonAsync(instance, "api/v1/key", ct);
        if (key is null)
            return AiUsageReport.Failed(sourceId, instance.Name,
                "OpenRouter did not answer for this key, so nothing here is a figure.", measuredAt);

        // An answer this reader cannot find a `data` object in is a failure and has to say so. Built as
        // a normal report it came out Answered, with three windows of nulls and no balance - a card
        // carrying an account's name, three window labels and not one figure, which is precisely the
        // "says nothing" this type exists to keep off the screen. Guarding only against the throw left
        // the quieter half of the same fault in place.
        if (Data(key) is not { ValueKind: JsonValueKind.Object } data)
            return AiUsageReport.Failed(sourceId, instance.Name,
                "OpenRouter answered in a shape this build does not recognise.", measuredAt);

        var remaining = Money(data, "limit_remaining") ?? await BalanceAsync(instance, ct);

        return new AiUsageReport(sourceId, instance.Name, Money(data, "limit") is { } limit
                ? $"limit {Currency}{limit:0.##}" : null,
            [
                new AiUsageWindow("today", TimeSpan.FromDays(1), UsedAmount: Money(data, "usage_daily")),
                new AiUsageWindow("7d", TimeSpan.FromDays(7), UsedAmount: Money(data, "usage_weekly"),
                    LimitAmount: Money(data, "limit"), ResetsAt: Reset(data)),
                new AiUsageWindow("30d", TimeSpan.FromDays(30), UsedAmount: Money(data, "usage_monthly")),
            ],
            remaining, Currency, measuredAt, Problem: null);
    }

    /// <summary>What this provider's amounts are in. OpenRouter prices in US dollars throughout.</summary>
    private const string Currency = "$";

    /// <inheritdoc />
    /// <remarks>The instance's id and not its name: nothing makes a name unique — two keys for the same
    /// service are two identically spelled rows — and a renamed row must keep its own history rather
    /// than adopting another's.</remarks>
    public override string UsageSourceId(AiProviderInstance instance) => $"openrouter:{instance.Id}";

    /// <summary>What is left where no limit is set on the key, from the credits endpoint.</summary>
    /// <remarks>Asked only when <c>limit_remaining</c> was null, which is what an unlimited key answers:
    /// the subtraction here is the only figure that is ours rather than the service's, and it is a
    /// difference of two numbers it did state.</remarks>
    private async Task<decimal?> BalanceAsync(AiProviderInstance instance, CancellationToken ct)
    {
        using var credits = await GetJsonAsync(instance, "api/v1/credits", ct);
        var data = Data(credits);

        return Money(data, "total_credits") is { } granted && Money(data, "total_usage") is { } spent
            ? granted - spent
            : null;
    }

    /// <summary>One amount out of the answer, or null where it does not carry it.</summary>
    /// <remarks>Null and never zero, for the reason the whole of <c>AiUsageWindow</c> is nullable: a
    /// field that moved is a service that did not say, and drawn as 0 it reads as a week that cost
    /// nothing.</remarks>
    private static decimal? Money(JsonElement data, string field) =>
        data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty(field, out var value)
        && value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal()
            : null;

    /// <summary>When the key's own limit window starts again, where one is set.</summary>
    private static DateTimeOffset? Reset(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty("limit_reset", out var reset)
        && reset.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(reset.GetString(), out var instant)
            ? instant
            : null;

    /// <summary>
    /// The full catalogue, with the one honest per-model effort answer any of these providers gives.
    /// </summary>
    /// <remarks>Long — 374 entries when this was written — which is why what shows it is a searchable
    /// field rather than a combo box.</remarks>
    public override async Task<IReadOnlyList<AiModelInfo>> ModelsAsync(AiProviderInstance instance,
        CancellationToken ct = default)
    {
        using var document = await GetJsonAsync(instance, "api/v1/models", ct);
        return document is null ? [] : ReadOpenAiModels(document);
    }
}

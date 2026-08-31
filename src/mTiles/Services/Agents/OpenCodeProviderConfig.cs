using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using mTiles.Models;
using mTiles.Services.Providers;

namespace mTiles.Services.Agents;

/// <summary>
/// The one file that lets opencode talk to a server it has never heard of.
/// </summary>
/// <remarks>
/// <para><b>Why a file at all.</b> opencode keeps a registry of providers and identifies them by name;
/// an address in the environment reaches it by no route whatsoever. For a hosted service that is fine —
/// the registry already has an entry and the service's own key variable selects it — but a server on
/// somebody's machine is in no registry, so the only way in is to declare one. Measured 2026-08-31:
/// with this document at <c>OPENCODE_CONFIG</c> and <c>--model lmstudio/&lt;id&gt;</c>, a request
/// reached LM Studio; without it, the same launch answered
/// <c>ProviderModelNotFoundError</c> before opening a socket.</para>
/// <para><b>Derived, per instance, and rewritten every launch</b>, exactly as
/// <c>OpenCodeSession</c>'s import document is: the path is a pure function of the instance's id, so
/// nothing here has to be remembered or migrated, and an address edited in Settings takes effect on the
/// next launch without anything having to notice it changed.</para>
/// <para><b>It is the user's own configuration with our provider added to it</b>, not a replacement:
/// <c>OPENCODE_CONFIG</c> names <em>the</em> config file rather than an extra one, so writing only our
/// block would take their default model, MCP servers, agents and instructions away from any tile on a
/// local server. Read from the paths opencode itself loads, best effort — one that cannot be parsed is
/// traced and treated as absent rather than stopping a launch.</para>
/// <para><b>Our own block carries no secret</b> — the providers that need this file are the ones with
/// no key at all, and where a provider does have one it goes into the environment through
/// <c>IAiProvider.KeyEnvironmentVariable</c> rather than into a file.</para>
/// <para><b>Nor does the provider block we add for a hosted service at a custom address</b>: its key
/// is written as <c>{env:NAME}</c>, which opencode resolves itself, so the secret stays in the process
/// environment and is not copied to a file that outlives the launch.</para>
/// <para><b>The merged half can, and that is worth knowing.</b> opencode's schema allows
/// <c>provider.&lt;id&gt;.options.apiKey</c>, so a user who keeps a key in their own
/// <c>opencode.json</c> has it copied into this generated file: a second copy, in a directory they did
/// not choose, rewritten every launch and never pruned. It is <em>not</em> stripped, deliberately —
/// removing it would leave their other providers unusable in the very tile this file exists to make
/// work, which is a broken feature in exchange for a copy that stays on the same machine. What
/// contains it: <see cref="PrivateFile"/> writes owner-only, and <c>SettingsPortability</c> exports
/// only <c>settings.json</c>, so this never travels.</para>
/// </remarks>
public static class OpenCodeProviderConfig
{
    /// <summary>Where this instance's generated config lives.</summary>
    public static string PathFor(string instanceId) =>
        Path.Combine(AppPaths.GetAppDataDirectory(), "opencode",
            $"{SafePathComponent.Of(instanceId)}.opencode.json");

    /// <summary>
    /// Whether this runtime needs one — a provider opencode's registry cannot name.
    /// </summary>
    /// <remarks>Not only the local ones. A hosted provider the user has given an address of its own —
    /// a gateway, a proxy — is equally unreachable by name, and opencode takes an address in exactly
    /// one place: this document. See <c>AgentRuntime.NeedsDeclaredEndpoint</c>, which is the same
    /// question <c>AgentAvailability</c> refuses pi on.</remarks>
    public static bool IsNeededFor(AgentRuntime runtime) =>
        runtime.NeedsDeclaredEndpoint
        && runtime.EndpointFor(ApiFlavor.OpenAiChatCompletions) is not null;

    /// <summary>
    /// Writes it and answers where it is, or null when it could not be written.
    /// </summary>
    /// <remarks>Fails soft and says so in the log: a config that cannot be written is a launch that
    /// runs on opencode's own configuration, which is wrong but recoverable — where throwing here would
    /// take down a tile that is otherwise perfectly able to start.</remarks>
    public static string? Write(AgentRuntime runtime)
    {
        if (!IsNeededFor(runtime)) return null;
        if (runtime.Provider is not { } provider) return null;
        if (runtime.EndpointFor(ApiFlavor.OpenAiChatCompletions) is not { } endpoint) return null;

        var path = PathFor(runtime.Instance.Id);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            PrivateFile.WriteAllText(path, Document(provider.CatalogueId, provider.DisplayName,
                endpoint, runtime.RequestedModel, UsersOwnConfig(),
                runtime.Provider?.KeyEnvironmentVariable));
            return path;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Could not write the opencode provider config {0}: {1}", path, ex.Message);

            // And the one from the launch before it goes, which is the whole difference between "no
            // config" and "the wrong config". This file is rewritten every launch precisely because an
            // address or a model edited in Settings has to take effect; left behind after a failed
            // write it declares the previous provider and the previous model, while the command line
            // carries the new `<provider>/<model>` - ProviderModelNotFoundError at best, and a request
            // sent to the address the user has just moved away from at worst. Falling back to
            // opencode's own configuration is wrong and visible; running on a stale one is neither.
            Discard(path);
            return null;
        }
    }

    /// <summary>
    /// The document, as opencode's own schema wants it.
    /// </summary>
    /// <remarks><para><c>@ai-sdk/openai-compatible</c> is what opencode documents for a server speaking
    /// <c>/v1/chat/completions</c>, and <c>options.baseURL</c> is where the address goes — the field
    /// this whole class exists to deliver.</para>
    /// <para>The model is listed because opencode resolves <c>provider/model</c> against what the
    /// provider declares, so a model absent from here is refused however well the server serves it. An
    /// instance naming no model declares none, and opencode is then left to its own choice within a
    /// provider that at least points at the right address.</para></remarks>
    internal static string Document(string providerId, string displayName, Uri baseUrl, string model,
        JsonObject? theirs = null, string? keyVariable = null)
    {
        var models = new JsonObject();
        if (model.Length > 0)
            models[model] = new JsonObject { ["name"] = model };

        // Theirs, with ours added — not ours instead of theirs. OPENCODE_CONFIG names *the* config
        // file rather than an extra one, so a document holding only our provider is a tile that has
        // silently lost the user's default model, their MCP servers, their agents and their
        // instructions. This is the one place this application writes a configuration file for
        // somebody else's tool, and overwriting is not what pointing an instance at a local server
        // asked for.
        var document = theirs ?? [];
        document["$schema"] ??= "https://opencode.ai/config.json";

        if (document["provider"] is not JsonObject providers)
        {
            // Their file is not validated against a schema and this class reads it best effort, so
            // `"provider": 3` is a thing that can arrive. Replaced rather than merged - there is
            // nothing to merge with - and said in the log, because it is the one case where this
            // application drops something somebody wrote.
            if (document["provider"] is not null)
            {
                Trace.TraceWarning("The opencode config has a \"provider\" that is not an object; the "
                    + "generated one for this tile replaces it.");
            }

            providers = [];
            document["provider"] = providers;
        }

        // Ours wins on its own key only: an entry the user wrote under the same name is one we would
        // otherwise be silently disagreeing with, and the address in it is the thing the instance is
        // for.
        var options = new JsonObject { ["baseURL"] = baseUrl.ToString() };

        // A reference, not the key. opencode resolves {env:NAME} itself, so the secret stays in the
        // one place it already is - the process environment, put there by ApplyProviderKey - instead of
        // being copied to a file on disk that is rewritten every launch and never pruned. Declaring the
        // provider would otherwise have moved a secret for no reason.
        if (keyVariable is { Length: > 0 }) options["apiKey"] = $"{{env:{keyVariable}}}";

        // Their own providers may carry a key of their own, and it is copied along with them - the
        // remarks above argue why it is not stripped. Noted in the log because it is a second copy of
        // somebody's secret in a directory they did not choose, and until now nothing recorded that it
        // had happened at all.
        // `entry is JsonObject` before anything is indexed: JsonNode's indexer throws on a value or an
        // array, so `"provider": { "foo": 3 }` in somebody's own config threw out of here, was caught by
        // Write, discarded the document and left the launch with no OPENCODE_CONFIG at all - the
        // ProviderModelNotFoundError this file exists to prevent, caused by the line that only logs.
        foreach (var (id, entry) in providers)
            if (id != providerId && entry is JsonObject theirEntry
                && theirEntry["options"] is JsonObject theirOptions
                && theirOptions["apiKey"] is JsonValue)
                Trace.TraceInformation(
                    "The opencode provider config carries a key from the user's own configuration for "
                    + "provider {0}; it is copied so that provider keeps working in this tile.", id);

        providers[providerId] = new JsonObject
        {
            ["npm"] = "@ai-sdk/openai-compatible",
            ["name"] = displayName,
            ["options"] = options,
            ["models"] = models,
        };

        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Removes a document this launch could not replace, best effort.</summary>
    /// <remarks>Failing that too, the log is the only thing left to say so: refusing the launch over it
    /// would take a tile away for a file the user has never heard of.</remarks>
    private static void Discard(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("The opencode provider config {0} is from an earlier launch and could "
                + "not be removed: {1}. This tile may run against the address in it.", path, ex.Message);
        }
    }

    /// <summary>Where to look for it, for a test that must not read the developer's own.</summary>
    /// <remarks>The same style of seam as <c>AppPaths.RootOverride</c> and
    /// <c>AiProvider.HandlerFactory</c>, and needed for the same reason: <c>Write</c> reads the real
    /// home directory, so every test that called <c>PrepareToLaunch</c> on a machine with opencode
    /// installed copied that machine's configuration — <c>provider.*.options.apiKey</c> included, which
    /// this class says out loud it does not strip — into a generated file. Set by <c>TempAppData</c>,
    /// so a test is isolated by the thing it already uses. While it is set, <c>XDG_CONFIG_HOME</c> is
    /// ignored too, or a developer who exports one would leak past it.</remarks>
    internal static string? HomeOverride { get; set; }

    /// <summary>
    /// The user's own opencode configuration, or null when there is none to be found.
    /// </summary>
    /// <remarks><para>The first of the paths opencode itself loads, in its own order — taken from its
    /// startup log, which names them one by one. Only the JSON ones: a <c>.jsonc</c> may carry comments
    /// and round-tripping it through a parser that does not keep them would hand the user back a file
    /// with their notes stripped out.</para>
    /// <para><b>Best effort, and silent about it by design.</b> A configuration that cannot be read is
    /// traced and then treated as absent — the alternative is refusing to launch a tile because
    /// somebody's unrelated setting is malformed. What is lost in that case is what was lost before
    /// this method existed.</para></remarks>
    private static JsonObject? UsersOwnConfig()
    {
        var home = HomeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdg = HomeOverride is null ? Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") : null;

        var candidates = new[]
        {
            xdg is { Length: > 0 } ? Path.Combine(xdg, "opencode", "opencode.json") : null,
            Path.Combine(home, ".config", "opencode", "opencode.json"),
            Path.Combine(home, ".opencode", "opencode.json"),
        };

        // A .jsonc beside a .json we did not find is a configuration this method is about to lose
        // silently — the very loss the merge exists to prevent, by the one branch it cannot cover. It
        // is not read (round-tripping it through a parser would hand the file back with the user's
        // comments stripped out), so the least that can be done is to name it in the log.
        foreach (var commented in candidates.OfType<string>()
                     .Select(candidate => Path.ChangeExtension(candidate, ".jsonc"))
                     .Where(File.Exists))
        {
            Trace.TraceWarning(
                "opencode config {0} is not merged into the generated one: this tile will run without "
                + "the settings in it. Move them to opencode.json to keep them.", commented);
        }

        foreach (var candidate in candidates.OfType<string>().Where(File.Exists))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(candidate)) is JsonObject theirs) return theirs;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Could not read the opencode config {0}: {1}", candidate, ex.Message);
            }
        }

        return null;
    }
}

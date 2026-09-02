using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// One login asked once, and two logins never mistaken for one.
/// </summary>
/// <remarks>
/// The tile has always merged the answers, so a duplicate never reached the screen; what it reached was
/// the service, which was asked twice a round for one subscription with one token — most of what the
/// Claude usage endpoint's 429s were, and those took the good row's figures down with them. The merge
/// therefore moved in front of the call. Both directions matter and only one of them is recoverable: an
/// account wrongly kept apart costs a call, an account wrongly folded away is a subscription missing
/// from the tile.
/// </remarks>
public class UsageSourceDedupTests : IDisposable
{
    private readonly TempAppData _appData = new();

    public void Dispose()
    {
        _appData.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Two sign-ins logged into one account are one source.</summary>
    [Fact]
    public void Two_rows_on_one_login_are_asked_once()
    {
        var settings = new AppSettings();
        SignIn(settings, "Max", account: "88adac0f");
        SignIn(settings, "Also Max", account: "88adac0f");

        Assert.Single(ClaudeSignInSources(settings));
    }

    /// <summary>Two sign-ins on two accounts stay two, which is the whole point of a second one.</summary>
    [Fact]
    public void Two_logins_stay_two()
    {
        var settings = new AppSettings();
        SignIn(settings, "Max", account: "88adac0f");
        SignIn(settings, "Pro", account: "64483719");

        Assert.Equal(2, ClaudeSignInSources(settings).Count);
    }

    /// <summary>
    /// A row whose login cannot be named is never merged with another.
    /// </summary>
    /// <remarks>A directory logged into whose <c>.claude.json</c> the CLI has not written yet answers
    /// nothing, and two of those are not evidence of anything. Kept apart, they cost the extra call
    /// that is being made today anyway; folded together, one of two subscriptions would be gone from
    /// the tile with nothing on screen saying so.</remarks>
    [Fact]
    public void Rows_that_cannot_name_their_login_are_kept_apart()
    {
        var settings = new AppSettings();
        SignIn(settings, "One", account: null);
        SignIn(settings, "Two", account: null);

        Assert.Equal(2, ClaudeSignInSources(settings).Count);
    }

    /// <summary>The row that survives is the one the user named and can find in Settings.</summary>
    /// <remarks>Which is <c>AccountsOf</c>'s order rather than an extra rule: sign-ins are offered
    /// before the CLI's own default account, and the first of a pair wins.</remarks>
    [Fact]
    public void The_sign_in_outlives_the_default_account_it_duplicates()
    {
        var settings = new AppSettings();
        var signIn = SignIn(settings, "Max", account: "88adac0f");

        // What a machine that exports CLAUDE_CONFIG_DIR looks like: the default account *is* the
        // sign-in's own directory, read twice under two names.
        var previous = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", AiSignInStore.DirectoryFor(signIn));
        try
        {
            var source = Assert.Single(ClaudeSources(settings));

            Assert.Equal(signIn.Id, source.SignIn?.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previous);
        }
    }

    /// <summary>A metered key has no duplicate to find, and says nothing rather than guessing.</summary>
    [Fact]
    public void A_provider_instance_names_no_login()
    {
        var settings = new AppSettings();
        settings.AiProviderInstances.Add(new AiProviderInstance
        {
            ProviderId = "openrouter",
            Name = "OpenRouter",
            ApiKey = "sk-test",
        });

        var sources = UsageSources.From(settings)
            .Where(source => source is ProviderUsageSource)
            .ToList();

        Assert.Null(Assert.Single(sources).AccountKey);
    }

    private static List<AgentUsageSource> ClaudeSources(AppSettings settings) =>
        UsageSources.From(settings)
            .OfType<AgentUsageSource>()
            .Where(source => source.Agent.Id == "claude")
            .ToList();

    /// <summary>
    /// The sign-in rows only.
    /// </summary>
    /// <remarks>The CLI's own default account is read from the developer's real home directory, which
    /// nothing here redirects, so counting it would make the assertion depend on whoever is running the
    /// test. The one case where it belongs is the test that puts it inside a sign-in's directory on
    /// purpose.</remarks>
    private static List<AgentUsageSource> ClaudeSignInSources(AppSettings settings) =>
        ClaudeSources(settings).Where(source => source.SignIn is not null).ToList();

    /// <summary>A sign-in with a directory the CLI has already written its account id into.</summary>
    private static AiSignIn SignIn(AppSettings settings, string name, string? account)
    {
        var signIn = new AiSignIn { AgentId = "claude", Name = name };
        settings.AiSignIns.Add(signIn);

        var directory = AiSignInStore.DirectoryFor(signIn);
        Directory.CreateDirectory(directory);

        if (account is not null)
            File.WriteAllText(Path.Combine(directory, ".claude.json"),
                $$"""{ "oauthAccount": { "accountUuid": "{{account}}" } }""");

        return signIn;
    }
}

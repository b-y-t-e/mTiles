using mTiles.Services.Shells;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What each shell does to a value and to an environment variable — the table that replaced the
/// <c>cmd</c>→PowerShell substitution tests.
/// </summary>
/// <remarks>
/// <para>These are the strings an agent's configuration is made of: an API key with a <c>$</c> in it, a
/// base URL, a Windows path with a <c>%</c>. Every one of them is spliced into a command line that a
/// shell then parses, so a quoting rule that is nearly right is a value silently changed — or, with a
/// quote in it, a second command. Pure functions, so the whole table runs without a shell.</para>
/// <para>Asserted as literal expected strings rather than by round-tripping through a real shell:
/// running <c>bash</c>, <c>zsh</c>, <c>fish</c> and <c>pwsh</c> would test whichever of the four the
/// machine happened to have, which on CI is a different subset per platform.</para>
/// </remarks>
public class ShellQuotingTests
{
    private static readonly IShellTerminal PowerShell = new PowerShellTerminal();
    private static readonly IShellTerminal Bash = new BashTerminal();
    private static readonly IShellTerminal Zsh = new ZshTerminal();
    private static readonly IShellTerminal GitBash = new GitBashTerminal();
    private static readonly IShellTerminal Fish = new FishTerminal();

    /// <summary>Every shell here quotes with single quotes, so the interesting cases are the quote
    /// itself and the backslash. The rest — <c>$</c>, <c>%</c>, spaces, newlines — must come through
    /// untouched, and that is most of what this table says.</summary>
    public static TheoryData<IShellTerminal, string, string> Quoting => new()
    {
        // A plain value is still quoted: leaving it bare is a rule with an edge nobody maintains.
        { PowerShell, "value", "'value'" },
        { Bash, "value", "'value'" },
        { Fish, "value", "'value'" },

        // Spaces and newlines: one argument, not several, and no line ends up run on its own.
        { PowerShell, "two words", "'two words'" },
        { Bash, "two words", "'two words'" },
        { Fish, "two words", "'two words'" },
        { PowerShell, "a\nb", "'a\nb'" },
        { Bash, "a\nb", "'a\nb'" },
        { Fish, "a\nb", "'a\nb'" },

        // The two characters a shell would otherwise read as "look this up".
        { PowerShell, "$HOME %PATH%", "'$HOME %PATH%'" },
        { Bash, "$HOME %PATH%", "'$HOME %PATH%'" },
        { Fish, "$HOME %PATH%", "'$HOME %PATH%'" },

        // A double quote is ordinary text inside single quotes — in every one of them.
        { PowerShell, "say \"hi\"", "'say \"hi\"'" },
        { Bash, "say \"hi\"", "'say \"hi\"'" },
        { Fish, "say \"hi\"", "'say \"hi\"'" },

        // The single quote, which is where they part company: PowerShell doubles it, a POSIX shell
        // closes the string to splice in an escaped one, fish escapes it in place.
        { PowerShell, "it's", "'it''s'" },
        { Bash, "it's", "'it'\\''s'" },
        { Zsh, "it's", "'it'\\''s'" },
        { GitBash, "it's", "'it'\\''s'" },
        { Fish, "it's", "'it\\'s'" },

        // The backslash: literal everywhere except fish, which still reads `\\` inside single quotes.
        { PowerShell, @"C:\Users\a", @"'C:\Users\a'" },
        { Bash, @"C:\Users\a", @"'C:\Users\a'" },
        { Fish, @"C:\Users\a", @"'C:\\Users\\a'" },

        // And both at once, which is what pins fish's ordering: doubling the backslash after escaping
        // the quote would put a stray one in front of the quote's own escape.
        { Fish, @"a\'b", @"'a\\\'b'" },
    };

    [Theory]
    [MemberData(nameof(Quoting))]
    public void A_value_is_quoted_the_way_its_shell_reads_it(IShellTerminal shell, string value,
        string expected)
        => Assert.Equal(expected, shell.Quote(value));

    public static TheoryData<IShellTerminal, string> Assignments => new()
    {
        { PowerShell, "$env:ANTHROPIC_API_KEY = 'sk-$1'" },
        { Bash, "export ANTHROPIC_API_KEY='sk-$1'" },
        { GitBash, "export ANTHROPIC_API_KEY='sk-$1'" },
        { Fish, "set -gx ANTHROPIC_API_KEY 'sk-$1'" },
    };

    /// <summary>A key is a value like any other and goes through <see cref="IShellTerminal.Quote"/> — a
    /// <c>$</c> in one is not exotic and must not be expanded away.</summary>
    [Theory]
    [MemberData(nameof(Assignments))]
    public void Setting_a_variable_quotes_its_value(IShellTerminal shell, string expected)
        => Assert.Equal(expected, shell.SetEnv("ANTHROPIC_API_KEY", "sk-$1"));

    public static TheoryData<IShellTerminal, string> Removals => new()
    {
        { PowerShell, "Remove-Item -LiteralPath Env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue" },
        { Bash, "unset ANTHROPIC_API_KEY" },
        { Zsh, "unset ANTHROPIC_API_KEY" },
        { Fish, "set -e ANTHROPIC_API_KEY" },
    };

    /// <summary>The half of the job the process environment cannot do: a variable the parent has, gone
    /// from the child. Unsetting one that is not there is the ordinary case rather than a fault — which
    /// is what PowerShell's <c>-ErrorAction SilentlyContinue</c> is for.</summary>
    [Theory]
    [MemberData(nameof(Removals))]
    public void Unsetting_a_variable_reads_as_this_shell_writes_it(IShellTerminal shell, string expected)
        => Assert.Equal(expected, shell.UnsetEnv("ANTHROPIC_API_KEY"));

    /// <summary>A null value means <em>unset</em>, which is how an agent says "not this one" in the same
    /// dictionary it says everything else in.</summary>
    [Fact]
    public void A_null_value_removes_the_variable_instead_of_setting_it()
    {
        var command = Bash.WithEnv(
            new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = null }, "claude");

        Assert.Equal("unset ANTHROPIC_API_KEY; claude", command);
    }

    [Fact]
    public void Env_statements_come_before_the_command_they_are_for()
    {
        var command = PowerShell.WithEnv(
            new Dictionary<string, string?> { ["ANTHROPIC_BASE_URL"] = "https://z.ai/api/anthropic" },
            "claude --session-id x");

        Assert.Equal("$env:ANTHROPIC_BASE_URL = 'https://z.ai/api/anthropic'; claude --session-id x",
            command);
    }

    /// <summary>Nothing to set is the command itself, not the command with a stray separator in front of
    /// it — an empty dictionary is the ordinary state of an agent with no provider configured.</summary>
    [Fact]
    public void An_empty_environment_leaves_the_command_alone()
        => Assert.Equal("claude", Bash.WithEnv(new Dictionary<string, string?>(), "claude"));

    /// <summary>
    /// A variable name is interpolated into a command rather than quoted into it, so anything that is
    /// not a name is refused instead of escaped.
    /// </summary>
    /// <remarks>Escaping it into shape would leave the caller's mistake in place and working; these
    /// names come from agent classes, and one carrying a <c>;</c> is a defect there rather than
    /// input.</remarks>
    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("X; rm -rf /")]
    [InlineData("1LEADING_DIGIT")]
    public void A_name_that_is_not_a_variable_name_is_refused(string name)
    {
        Assert.Throws<ArgumentException>(() => Bash.SetEnv(name, "v"));
        Assert.Throws<ArgumentException>(() => PowerShell.UnsetEnv(name));
    }
}

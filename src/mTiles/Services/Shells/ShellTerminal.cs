namespace mTiles.Services.Shells;

/// <summary>
/// What every shell shares: composing environment statements into a command, and refusing a variable
/// name that would not survive being written into one.
/// </summary>
/// <remarks>
/// <see cref="IShellTerminal.SetEnv"/> and <see cref="IShellTerminal.UnsetEnv"/> are sealed here and
/// delegate to <see cref="Assign"/> / <see cref="Remove"/>, so the name check cannot be forgotten by a
/// shell added later. The names come from agent classes rather than from a text box, but they are
/// interpolated straight into a shell command — the one place in this file where a mistake is not a
/// wrong string but a second command.
/// </remarks>
public abstract class ShellTerminal : IShellTerminal
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string IconId { get; }
    public abstract IReadOnlyList<string> DetectPaths();
    public abstract IReadOnlyList<string> InteractiveArgs { get; }
    public abstract IReadOnlyList<string> CommandArgs { get; }
    public abstract IReadOnlyList<string> NoProfileArgs { get; }
    public abstract string Quote(string value);

    public string SetEnv(string name, string value) => Assign(Named(name), value);

    public string UnsetEnv(string name) => Remove(Named(name));

    /// <inheritdoc />
    /// <remarks>Every shell here separates statements with <c>;</c>, so this is one implementation
    /// rather than three identical ones. A shell that does not can override it.</remarks>
    public virtual string WithEnv(IReadOnlyDictionary<string, string?> variables, string command)
    {
        if (variables.Count == 0) return command;

        var statements = variables.Select(v => v.Value is null ? UnsetEnv(v.Key) : SetEnv(v.Key, v.Value));
        return string.Join("; ", statements) + "; " + command;
    }

    /// <summary>The statement that sets <paramref name="name"/>, which has already been checked.</summary>
    protected abstract string Assign(string name, string value);

    /// <summary>The statement that removes <paramref name="name"/>, which has already been checked.</summary>
    protected abstract string Remove(string name);

    /// <summary>
    /// <paramref name="name"/> if it is an environment variable name, and an exception if it is
    /// anything else.
    /// </summary>
    /// <remarks>The POSIX rule — a letter or underscore, then letters, digits and underscores — which
    /// PowerShell's <c>$env:</c> accepts as well. Throwing rather than escaping: there is no legitimate
    /// caller with a space or a semicolon in a variable name, so a value that fails this is a defect
    /// upstream and quoting it into shape would only hide it.</remarks>
    private static string Named(string name)
    {
        if (name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_')
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            return name;

        throw new ArgumentException($"'{name}' is not an environment variable name.", nameof(name));
    }
}

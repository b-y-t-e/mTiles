using System.Diagnostics;

namespace mTiles.Tests;

/// <summary>
/// A throwaway git repository for one test.
/// </summary>
/// <remarks>
/// <para><b>Copied, not initialised.</b> <c>git init</c> plus the three <c>config</c> calls every test
/// repository needs were four processes per test, before the test had done anything; on Windows a git
/// process costs tens of milliseconds, and there were six copies of this class doing it. The repository
/// is made once per run and each test gets a copy of its directory, which is a handful of small files.
/// </para>
/// <para>The identity is set on the repository rather than relied on from the machine: a build agent has
/// no global one, and <c>commit</c> would fail there for a reason that has nothing to do with the test.
/// It is written into <c>.git/config</c> directly rather than with <c>git config</c>, for the reason
/// above.</para>
/// </remarks>
internal sealed class GitTestRepo : IDisposable
{
    private static readonly Lazy<string> Template = new(MakeTemplate);

    /// <summary>The working tree.</summary>
    public string Path { get; }

    /// <param name="init">False for a plain directory that is not a repository yet.</param>
    /// <param name="prefix">Names the directory after the test, which is what makes a leftover
    /// readable.</param>
    public GitTestRepo(bool init = true, string prefix = "repo")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mtiles-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
        if (init) CopyDirectory(Template.Value, Path);
    }

    /// <summary>Writes a file into the working tree, making the directories on the way.</summary>
    public string Write(string name, string content)
    {
        var full = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Stages everything and commits it.</summary>
    public void CommitAll(string message = "commit")
    {
        Git("add -A");
        Git($"commit -q -m \"{message}\"");
    }

    /// <summary>Runs git in the working tree and answers what it printed.</summary>
    public string Git(string arguments) => Run(Path, arguments);

    public void Dispose()
    {
        try
        {
            // Git leaves read-only files under .git/objects on Windows, which Directory.Delete refuses.
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex)
        {
            // The run's temporary directory is cleared two runs later anyway (TestTempRoot).
            Trace.TraceWarning($"Cleaning up the test repository failed: {ex.Message}");
        }
    }

    /// <summary>Runs git in <paramref name="directory"/> and answers what it printed.</summary>
    public static string Run(string directory, string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var stderr = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        stderr.GetAwaiter().GetResult();
        p.WaitForExit();
        return output;
    }

    private static string MakeTemplate()
    {
        RequiresGit.OrFail("a git repository");

        var template = System.IO.Path.Combine(TestTempRoot.Root, "git-template");
        Directory.CreateDirectory(template);
        Run(template, "init -q");
        File.AppendAllText(System.IO.Path.Combine(template, ".git", "config"),
            "[user]\n\tname = tester\n\temail = tester@localhost\n[commit]\n\tgpgsign = false\n");
        return template;
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(System.IO.Path.Combine(to, System.IO.Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, System.IO.Path.Combine(to, System.IO.Path.GetRelativePath(from, file)));
    }
}

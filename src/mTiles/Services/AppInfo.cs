using System.Reflection;

namespace mTiles.Services;

public static class AppInfo
{
    // Version wkomitowana w version.txt jest wstrzykiwana do assembly jako <Version>
    // (patrz mTiles.csproj). InformationalVersion może mieć sufiks "+<commit>" od
    // SourceLink — obcinamy go do czystego numeru.
    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrEmpty(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.0.0";
    }
}

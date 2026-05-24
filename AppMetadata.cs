using System.Reflection;

namespace Peek;

internal static class AppMetadata
{
    public const string Name = "Peek";

    public static string Version { get; } = GetVersion();

    public static string DisplayNameWithVersion =>
        string.IsNullOrWhiteSpace(Version)
            ? Name
            : $"{Name} {Version}";

    private static string GetVersion()
    {
        var assembly = typeof(AppMetadata).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var metadataSeparatorIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
            return metadataSeparatorIndex > 0
                ? informationalVersion[..metadataSeparatorIndex]
                : informationalVersion;
        }

        return assembly.GetName().Version?.ToString() ?? string.Empty;
    }
}

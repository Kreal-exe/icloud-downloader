using System.Reflection;

namespace iCloudDownloader;

public static class BuildInfo
{
    public static string RepoRoot =>
        typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepoRoot")?.Value ?? AppContext.BaseDirectory;

    public static string Version => typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "";
}

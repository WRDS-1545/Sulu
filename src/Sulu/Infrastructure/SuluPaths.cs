namespace Sulu.Infrastructure;

/// <summary>
/// Central locations for user-owned state. SULU_DATA_DIR is useful for tests and
/// portable installations; the default follows per-user Windows conventions.
/// </summary>
internal static class SuluPaths
{
    private const string FolderName = "sulu";

    public static string DataRoot
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("SULU_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(custom))
            {
                try
                {
                    return Path.GetFullPath(custom.Trim());
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // Fall through to the normal per-user location.
                }
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(localAppData)
                ? Path.Combine(Path.GetTempPath(), FolderName)
                : Path.Combine(localAppData, FolderName);
        }
    }

    public static string IndexFile => Path.Combine(DataRoot, "index.json");

    public static string BackupsRoot => Path.Combine(DataRoot, "backups");

    public static string MigrationsRoot => Path.Combine(DataRoot, "migrations");
}

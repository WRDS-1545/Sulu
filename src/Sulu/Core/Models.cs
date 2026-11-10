using Sulu.Infrastructure;

namespace Sulu.Core;

internal sealed record InstalledApp
{
    public required string Name { get; init; }

    public required RegistryLocation Location { get; init; }

    public required string KeyName { get; init; }

    public int Index { get; init; }

    public string Ref => Index > 0 ? Index.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-";

    public string Id => Location.Id;

    public string? Version { get; init; }

    public string? Publisher { get; init; }

    public string? InstallLocation { get; init; }

    public string? UninstallString { get; init; }

    public string? QuietUninstallString { get; init; }

    public string? ProductCode { get; init; }

    public string? InstallDate { get; init; }

    public long? EstimatedSizeBytes { get; init; }

    public bool IsHidden { get; init; }

    public bool NoRemove { get; init; }

    public bool IsMsi { get; init; }

    public string SourceLabel => Location.Code;

    public string DisplayVersion => string.IsNullOrWhiteSpace(Version) ? "-" : Version;

    public string DisplayPublisher => string.IsNullOrWhiteSpace(Publisher) ? "-" : Publisher;

    public string DisplayInstallLocation => string.IsNullOrWhiteSpace(InstallLocation) ? "-" : InstallLocation;
}

internal sealed record AppIdentity
{
    public required string Name { get; init; }

    public string? Publisher { get; init; }

    public string? InstallLocation { get; init; }

    public string? ProductCode { get; init; }

    public required IReadOnlySet<string> OriginalKeyPaths { get; init; }

    public static AppIdentity From(InstalledApp app) => new()
    {
        Name = app.Name,
        Publisher = app.Publisher,
        InstallLocation = app.InstallLocation,
        ProductCode = app.ProductCode,
        OriginalKeyPaths = new HashSet<string>([app.Location.Id], StringComparer.OrdinalIgnoreCase),
    };
}

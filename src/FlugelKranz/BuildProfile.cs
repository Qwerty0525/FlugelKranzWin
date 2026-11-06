using System.Reflection;
using FlugelKranz.Core;

namespace FlugelKranz;

/// <summary>Which kind of build this is: decides a few defaults for new configs.</summary>
public enum BuildProfile
{
    Release,
    Test
}

/// <summary>
/// The build profile is set at build time, like the driver's shipped driverAudit value:
/// <c>build-windows.ps1 -TestBuild</c> publishes with <c>-p:FlugelKranzBuildProfile=test</c>, and
/// the csproj stores that as assembly metadata in FlugelKranz.dll (any other build, including
/// plain <c>dotnet build</c> and the tests, is <c>release</c>). Only defaults for settings that are
/// not saved yet depend on it; values already in config.json always win.
/// </summary>
public static class BuildProfiles
{
    public const string MetadataKey = "FlugelKranzBuildProfile";

    /// <summary>The profile this FlugelKranz.dll was built with.</summary>
    public static BuildProfile Current { get; } = Read(typeof(BuildProfiles).Assembly);

    public static BuildProfile Parse(string? value) =>
        string.Equals(value?.Trim(), "test", StringComparison.OrdinalIgnoreCase) ? BuildProfile.Test : BuildProfile.Release;

    internal static BuildProfile Read(Assembly assembly) =>
        Parse(assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == MetadataKey)?.Value);

    /// <summary>Compass settings for a config that has none: on in test builds, off in release builds.</summary>
    public static CompassSettings CompassDefaults(this BuildProfile profile) =>
        CompassSettings.Default with { Enabled = profile == BuildProfile.Test };
}

using System.Reflection;

namespace FlugelKranz;

/// <summary>
/// The release number from Directory.Build.props (for example 0.9.0) and the git commit the SDK
/// appends to InformationalVersion ("0.9.0+&lt;sha&gt;"). Shown in the window title and summary.txt.
/// </summary>
public static class AppVersion
{
    private static readonly (string Number, string? Commit) Current = Read(typeof(AppVersion).Assembly);

    /// <summary>The release number, for example "0.9.0".</summary>
    public static string Number => Current.Number;

    /// <summary>The short commit this build came from, or null when the build had no git metadata.</summary>
    public static string? Commit => Current.Commit;

    /// <summary>"FlugelKranz 0.9.0", plus "(test build)" for a test build.</summary>
    public static string Title(BuildProfile profile) =>
        "FlugelKranz " + Number + (profile == BuildProfile.Test ? " (test build)" : "");

    /// <summary>For logs: "FlugelKranz 0.9.0 (release build, commit 1a2b3c4)".</summary>
    public static string Describe(BuildProfile profile) =>
        "FlugelKranz " + Number + " (" + (profile == BuildProfile.Test ? "test" : "release") + " build" +
        (Commit is null ? "" : ", commit " + Commit) + ")";

    internal static (string Number, string? Commit) Read(Assembly assembly) =>
        Parse(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assembly.GetName().Version);

    /// <summary>Splits "0.9.0+&lt;sha&gt;" into the number and a 7-character commit.</summary>
    internal static (string Number, string? Commit) Parse(string? informationalVersion, Version? fallback)
    {
        string text = informationalVersion?.Trim() ?? "";
        int plus = text.IndexOf('+');
        string number = plus >= 0 ? text[..plus] : text;
        string? commit = plus >= 0 ? text[(plus + 1)..].Trim() : null;
        if (number.Length == 0)
            number = fallback is null ? "unknown" : fallback.ToString(3);
        if (string.IsNullOrEmpty(commit)) commit = null;
        else if (commit.Length > 7) commit = commit[..7];
        return (number, commit);
    }
}

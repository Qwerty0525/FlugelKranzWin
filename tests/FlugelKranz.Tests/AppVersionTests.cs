using System.Text.RegularExpressions;
using FlugelKranz.Diagnostics;
using Xunit;

namespace FlugelKranz.Tests;

public class AppVersionTests
{
    [Fact]
    public void AppAndDriverCarryTheReleaseNumberFromDirectoryBuildProps()
    {
        string root = RepositoryRoot();
        string props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        string cmake = File.ReadAllText(Path.Combine(root, "native", "driver", "CMakeLists.txt"));
        string version = Regex.Match(props, @"<Version>\s*([^<\s]+)\s*</Version>").Groups[1].Value;
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
        Assert.Equal(version, Regex.Match(cmake, @"project\(FlugelKranzDriver VERSION (\S+)").Groups[1].Value);
        Assert.Equal(version, AppVersion.Number);
        Assert.Equal(new Version(version + ".0"), typeof(AppVersion).Assembly.GetName().Version);
        Assert.Contains("\"FlugelKranz driver version: \" FLUGELKRANZ_VERSION",
            File.ReadAllText(Path.Combine(root, "native", "driver", "Driver.cpp")));
    }

    [Theory]
    [InlineData("0.9.0+5b22c47e1f0a9d3c", "0.9.0", "5b22c47")]
    [InlineData("0.9.0+abc", "0.9.0", "abc")]
    [InlineData("0.9.0", "0.9.0", null)]
    [InlineData("0.9.0+", "0.9.0", null)]
    [InlineData(null, "1.2.3", null)]
    public void ParseSplitsTheNumberAndAShortCommit(string? informational, string number, string? commit)
    {
        Assert.Equal((number, commit), AppVersion.Parse(informational, new Version(1, 2, 3, 0)));
    }

    [Fact]
    public void TitleAndSummaryHeaderNameTheVersionAndBuildProfile()
    {
        Assert.Equal("FlugelKranz " + AppVersion.Number, AppVersion.Title(BuildProfile.Release));
        Assert.Equal("FlugelKranz " + AppVersion.Number + " (test build)", AppVersion.Title(BuildProfile.Test));
        Assert.StartsWith("FlugelKranz " + AppVersion.Number + " (test build", AppVersion.Describe(BuildProfile.Test));

        // The tests use a plain build of FlugelKranz.dll, which is the release profile.
        string summary = DiagnosticLogCollector.BuildSummary([], new DateTime(2026, 11, 7, 12, 0, 0));
        string header = summary.Split('\n')[0];
        Assert.StartsWith("Collected 20261107-120000 by FlugelKranz " + AppVersion.Number + " (release build", header);
        Assert.EndsWith("; PC name, user name and profile paths redacted", header);
        Assert.Contains(AppVersion.Describe(BuildProfile.Release), header);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FlugelKranz.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("FlugelKranz.slnx not found above the test output directory.");
    }
}

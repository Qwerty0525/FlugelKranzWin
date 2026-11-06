using System.Reflection;
using System.Text.Json.Nodes;
using FlugelKranz.Core;
using FlugelKranz.ViewModels;
using Xunit;

namespace FlugelKranz.Tests;

/// <summary>Release and test builds: the compass's default for new configs, and saved values winning.</summary>
public class BuildProfileTests
{
    [Theory]
    [InlineData("test", BuildProfile.Test)]
    [InlineData("TEST", BuildProfile.Test)]
    [InlineData(" test ", BuildProfile.Test)]
    [InlineData("release", BuildProfile.Release)]
    [InlineData("", BuildProfile.Release)]
    [InlineData(null, BuildProfile.Release)]
    [InlineData("debug", BuildProfile.Release)]
    public void ParsesTheProfile(string? value, BuildProfile expected) => Assert.Equal(expected, BuildProfiles.Parse(value));

    [Fact]
    public void PlainBuildsCarryTheReleaseProfileInTheAssembly()
    {
        // build-windows.ps1 -TestBuild passes -p:FlugelKranzBuildProfile=test; everything else is release.
        var metadata = typeof(BuildProfiles).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == BuildProfiles.MetadataKey);
        Assert.Equal("release", metadata.Value);
        Assert.Equal(BuildProfile.Release, BuildProfiles.Current);
    }

    [Fact]
    public void CompassDefaultsDifferOnlyInOnOff()
    {
        Assert.False(BuildProfile.Release.CompassDefaults().Enabled);
        Assert.True(BuildProfile.Test.CompassDefaults().Enabled);
        Assert.Equal(CompassSettings.Default, BuildProfile.Release.CompassDefaults());
        Assert.Equal(CompassSettings.Default with { Enabled = true }, BuildProfile.Test.CompassDefaults());
    }

    [Theory]
    [InlineData(BuildProfile.Release, false)]
    [InlineData(BuildProfile.Test, true)]
    public void ConfigsWithoutACompassChoiceGetTheProfileDefault(BuildProfile profile, bool expected)
    {
        string root = Directory.CreateTempSubdirectory("fk-profile-").FullName;
        try
        {
            string path = Path.Combine(root, "config.json");
            // No config yet.
            Assert.Equal(expected, new SettingsStore(path, profile).Load().Compass.Enabled);

            // A current config written before the compass existed.
            new SettingsStore(path).Save(FlugelKranzSettings.Default);
            var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            node.Remove("compass");
            File.WriteAllText(path, node.ToJsonString());
            Assert.Equal(expected, new SettingsStore(path, profile).Load().Compass.Enabled);

            // A compass object without "enabled": the rest is kept.
            node["compass"] = new JsonObject { ["hand"] = "left" };
            File.WriteAllText(path, node.ToJsonString());
            var loaded = new SettingsStore(path, profile).Load().Compass;
            Assert.Equal(expected, loaded.Enabled);
            Assert.Equal(CompassHand.Left, loaded.Hand);

            // An old flat config is replaced by this build's defaults, and saved that way.
            File.WriteAllText(path, "{ \"speed\": 2 }");
            Assert.Equal(expected, new SettingsStore(path, profile).Load().Compass.Enabled);
            Assert.Equal(expected, JsonNode.Parse(File.ReadAllText(path))!["compass"]!["enabled"]!.GetValue<bool>());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(BuildProfile.Release, true)]
    [InlineData(BuildProfile.Release, false)]
    [InlineData(BuildProfile.Test, true)]
    [InlineData(BuildProfile.Test, false)]
    public void ASavedCompassChoiceAlwaysWins(BuildProfile profile, bool saved)
    {
        string root = Directory.CreateTempSubdirectory("fk-profile-").FullName;
        try
        {
            string path = Path.Combine(root, "config.json");
            new SettingsStore(path).Save(FlugelKranzSettings.Default with { Compass = CompassSettings.Default with { Enabled = saved } });
            Assert.Equal(saved, new SettingsStore(path, profile).Load().Compass.Enabled);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(BuildProfile.Release, false)]
    [InlineData(BuildProfile.Test, true)]
    public async Task TheSettingsPanelStartsAndResetsToTheProfileDefault(BuildProfile profile, bool expected)
    {
        string root = Directory.CreateTempSubdirectory("fk-profile-vm-").FullName;
        try
        {
            string path = Path.Combine(root, "config.json");
            await using (var vm = new MainViewModel("/nonexistent/flugelkranz-test.so", path, buildProfile: profile))
            {
                Assert.Equal(profile, vm.BuildProfile);
                Assert.Equal(expected, vm.CompassEnabled);
                vm.CompassEnabled = !expected;
                Assert.Equal(!expected, JsonNode.Parse(File.ReadAllText(path))!["compass"]!["enabled"]!.GetValue<bool>());
            }
            // Reopened, the saved choice stays; reset goes back to this build's default.
            await using var reopened = new MainViewModel("/nonexistent/flugelkranz-test.so", path, buildProfile: profile);
            Assert.Equal(!expected, reopened.CompassEnabled);
            reopened.ResetCompassEnabledCommand.Execute(null);
            Assert.Equal(expected, reopened.CompassEnabled);
        }
        finally { Directory.Delete(root, true); }
    }
}

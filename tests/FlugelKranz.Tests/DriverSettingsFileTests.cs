using System.Text.Json.Nodes;
using FlugelKranz.Diagnostics;
using FlugelKranz.ViewModels;
using Xunit;

namespace FlugelKranz.Tests;

public class DriverSettingsFileTests
{
    [Fact]
    public void ShippedDriverSettingsKeepTheAuditOn()
    {
        string shipped = Path.Combine(RepositoryRoot(), "native", "driver", "resources", "settings", "default.vrsettings");
        Assert.True(DriverSettingsFile.ShippedDriverAudit);
        Assert.Equal(DriverSettingsFile.ShippedDriverAudit, DriverSettingsFile.ReadDriverAudit(shipped));
    }

    [Fact]
    public void WritingTheFlagKeepsEveryOtherKey()
    {
        string root = Directory.CreateTempSubdirectory("fk-driver-").FullName;
        try
        {
            string path = Path.Combine(root, "default.vrsettings");
            File.Copy(Path.Combine(RepositoryRoot(), "native", "driver", "resources", "settings", "default.vrsettings"), path);
            var before = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

            DriverSettingsFile.WriteDriverAudit(path, false);
            Assert.False(DriverSettingsFile.ReadDriverAudit(path));
            var after = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var section = after["driver_flugelkranz"]!.AsObject();
            Assert.Equal(before["driver_flugelkranz"]!.AsObject().Select(p => p.Key), section.Select(p => p.Key));
            Assert.Equal("driver_slimevr.dll,driver_rebocap.dll", section["physicalPoseDrivers"]!.GetValue<string>());
            Assert.True(section["correctFramePose"]!.GetValue<bool>());

            DriverSettingsFile.WriteDriverAudit(path, true);
            Assert.True(DriverSettingsFile.ReadDriverAudit(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MissingKeyOrFileReadsAsUnset()
    {
        string root = Directory.CreateTempSubdirectory("fk-driver-").FullName;
        try
        {
            string path = Path.Combine(root, "steamvr.vrsettings");
            File.WriteAllText(path, "{ \"steamvr\": { \"x\": 1 } }");
            Assert.Null(DriverSettingsFile.ReadDriverAudit(path));
            Assert.Null(DriverSettingsFile.ReadDriverAudit(Path.Combine(root, "none.vrsettings")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DriverFolderIsFoundNextToTheAppOrInOpenVrPaths()
    {
        string root = Directory.CreateTempSubdirectory("fk-driver-").FullName;
        try
        {
            string app = Path.Combine(root, "app"), registered = Path.Combine(root, "elsewhere", "flugelkranz");
            string settings = Path.Combine(registered, "resources", "settings");
            Directory.CreateDirectory(app);
            Directory.CreateDirectory(settings);
            File.WriteAllText(Path.Combine(settings, "default.vrsettings"), "{}");
            string vrpath = Path.Combine(root, "openvrpaths.vrpath");
            File.WriteAllText(vrpath, new JsonObject
            {
                ["external_drivers"] = new JsonArray(Path.Combine(root, "other"), registered)
            }.ToJsonString());

            Assert.Null(DriverSettingsFile.Locate(app, null));
            Assert.Equal(Path.Combine(settings, "default.vrsettings"), DriverSettingsFile.Locate(app, vrpath));

            string local = Path.Combine(app, "driver", "flugelkranz", "resources", "settings");
            Directory.CreateDirectory(local);
            File.WriteAllText(Path.Combine(local, "default.vrsettings"), "{}");
            Assert.Equal(Path.Combine(local, "default.vrsettings"), DriverSettingsFile.Locate(app, vrpath));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SettingWritesTheDriverFileAndResetRestoresTheShippedValue()
    {
        string root = Directory.CreateTempSubdirectory("fk-driver-").FullName;
        try
        {
            string path = Path.Combine(root, "default.vrsettings");
            File.WriteAllText(path, "{ \"driver_flugelkranz\": { \"driverAudit\": true, \"loadPriority\": 100 } }");
            await using var vm = new MainViewModel("/nonexistent/flugelkranz-test.so", Path.Combine(root, "config.json"));
            vm.Language = "en";
            vm.LoadDriverSettings(path, null);
            Assert.True(vm.HasDriverSettings);
            Assert.True(vm.DetailedDiagnostics);
            Assert.Equal("Driver settings: " + path, vm.DetailedDiagnosticsStatus);

            vm.DetailedDiagnostics = false;
            Assert.False(DriverSettingsFile.ReadDriverAudit(path));
            Assert.Equal($"Saved to {path}. Restart SteamVR to apply.", vm.DetailedDiagnosticsStatus);
            Assert.Equal(100, JsonNode.Parse(File.ReadAllText(path))!["driver_flugelkranz"]!["loadPriority"]!.GetValue<int>());

            vm.ResetDetailedDiagnosticsCommand.Execute(null);
            Assert.True(DriverSettingsFile.ReadDriverAudit(path));

            vm.LoadDriverSettings(path, false);
            Assert.EndsWith("steamvr.vrsettings sets driverAudit = false for this driver, which takes precedence over this setting.",
                vm.DetailedDiagnosticsStatus);
            vm.LoadDriverSettings(null, null);
            Assert.False(vm.HasDriverSettings);
            Assert.False(vm.DetailedDiagnostics);
            Assert.True(DriverSettingsFile.ReadDriverAudit(path), "loading never writes");
        }
        finally { Directory.Delete(root, true); }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FlugelKranz.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("FlugelKranz.slnx not found above the test output directory.");
    }
}

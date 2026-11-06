using System.IO.Compression;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using FlugelKranz.Diagnostics;
using FlugelKranz.ViewModels;
using FlugelKranz.Views;
using Xunit;

namespace FlugelKranz.Tests;

public class DiagnosticLogTests
{
    private static readonly DiagnosticRedactor Redactor = new("DESKTOP-K7Q2", "Mina", [@"C:\Users\Mina"]);

    [Theory]
    [InlineData(@"C:\Users\Mina\AppData\Roaming\x.log", @"%USERPROFILE%\AppData\Roaming\x.log")]
    [InlineData(@"c:/users/mina/AppData", @"%USERPROFILE%/AppData")]
    [InlineData(@"""path"": ""C:\\Users\\Mina\\.config\\FlugelKranz""", @"""path"": ""%USERPROFILE%\\.config\\FlugelKranz""")]
    [InlineData(@"C:\Users\MINA", @"%USERPROFILE%")]
    public void ProfilePathsAreReplacedInEverySpelling(string input, string expected) =>
        Assert.Equal(expected, Redactor.Apply(input));

    [Fact]
    public void UserAndMachineNamesAreReplacedAsWholeTokensOnly()
    {
        Assert.Equal("user <user> on <pc>", Redactor.Apply("user Mina on DESKTOP-K7Q2"));
        Assert.Equal("Computer: <pc>.local, account=<user>", Redactor.Apply("Computer: desktop-k7q2.local, account=mina"));
        // Inside another word it stays.
        Assert.Equal("Minamoto DESKTOP-K7Q23", Redactor.Apply("Minamoto DESKTOP-K7Q23"));
        // A different profile folder (C:\Users\Mina.PC) is still caught by the user name.
        Assert.Equal(@"C:\Users\<user>.PC\x", Redactor.Apply(@"C:\Users\Mina.PC\x"));
    }

    [Fact]
    public void ShortNamesAreOnlyRemovedInsideProfilePaths()
    {
        var redactor = new DiagnosticRedactor("PC", "al", [@"C:\Users\al"]);
        Assert.Equal(@"PC al %USERPROFILE%\logs", redactor.Apply(@"PC al C:\Users\al\logs"));
    }

    [Fact]
    public void ArchiveHoldsTheRedactedPlanAndSummaryAndLeavesSourcesUntouched()
    {
        string root = Directory.CreateTempSubdirectory("fk-diag-").FullName;
        try
        {
            var sources = new DiagnosticSources(
                Path.Combine(root, "Steam"), Path.Combine(root, "slime"), Path.Combine(root, "vd"),
                Path.Combine(root, "openvr", "openvrpaths.vrpath"), Path.Combine(root, "fk", "config.json"));
            string logs = Directory.CreateDirectory(sources.SteamLogs).FullName;
            Directory.CreateDirectory(Path.Combine(sources.SteamPath, "config"));
            Directory.CreateDirectory(sources.SlimeVrLogs);
            Directory.CreateDirectory(sources.VirtualDesktopData);
            Directory.CreateDirectory(Path.GetDirectoryName(sources.FlugelKranzConfig)!);
            File.WriteAllLines(Path.Combine(logs, "vrserver.txt"),
            [
                @"Fri Nov 06 2026 14:27:36.000 [Info] - Loaded server driver flugelkranz (C:\Users\Mina\vr\driver_flugelkranz.dll)",
                "Fri Nov 06 2026 14:27:36.500 [Info] - Active HMD set to oculus.WMHD000000 on DESKTOP-K7Q2",
                "Fri Nov 06 2026 14:27:36.942 [Info] - flugelkranz: FlugelKranz flags: observeFrames=1 driverAudit=1",
                FrameAudit(headAge: 12, flying: false),
                FrameAudit(headAge: 50, flying: true),
                FrameAudit(headAge: 20, flying: true),
                FrameAudit(headAge: 2000, flying: true),
                DriverAudit(new long[] { 0, 0, 2, 60, 0, 0, 0, 0 }, skewMean: 0.08, stepMean: 0.12),
                DriverAudit(new long[] { 250, 10, 0, 0, 0, 0, 0, 0 }, skewMean: 0.02, stepMean: 0.025),
            ]);
            File.WriteAllText(Path.Combine(logs, "vrclient_FlugelKranz.txt"), "client of Mina");
            File.WriteAllText(Path.Combine(sources.SteamPath, "config", "steamvr.vrsettings"),
                "{ \"path\": \"C:\\\\Users\\\\Mina\\\\Documents\" }");
            File.WriteAllLines(Path.Combine(sources.SlimeVrLogs, "slimevr-openvr-driver.log"),
            [
                "2026-11-06 14:00:00 [WARN] Tracker lost",
                "2026-11-06 14:00:01 [WARN] Tracker lost",
                "2026-11-06 14:00:02 [INFO] fine",
            ]);
            File.WriteAllText(Path.Combine(sources.VirtualDesktopData, "StreamerLog.txt"), "x|ERROR|bad\n");
            File.WriteAllText(sources.FlugelKranzConfig, "{ \"language\": \"ja\" }");
            var before = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, Hash);

            var plan = DiagnosticLogCollector.Plan(sources);
            Assert.Equal(
                new[] { "steamvr/vrserver.txt", "steamvr/vrclient_FlugelKranz.txt", "steamvr-config/steamvr.vrsettings",
                    "slimevr/slimevr-openvr-driver.log", "virtualdesktop/StreamerLog.txt", "flugelkranz/config.json" },
                plan.Select(f => f.ArchivePath));

            string zipPath = Path.Combine(root, "out", "logs.zip");
            DiagnosticLogCollector.WriteArchive(zipPath, plan, Redactor, new DateTime(2026, 11, 6, 15, 0, 0));

            Assert.Equal(before, Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(p => !p.StartsWith(Path.Combine(root, "out"))).ToDictionary(p => p, Hash));
            using var zip = ZipFile.OpenRead(zipPath);
            Assert.Equal(new[] { "summary.txt" }.Concat(plan.Select(f => f.ArchivePath)), zip.Entries.Select(e => e.FullName));
            foreach (var entry in zip.Entries)
            {
                string text = new StreamReader(entry.Open()).ReadToEnd();
                Assert.DoesNotContain("Mina", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("DESKTOP-K7Q2", text, StringComparison.OrdinalIgnoreCase);
            }
            string vrsettings = new StreamReader(zip.GetEntry("steamvr-config/steamvr.vrsettings")!.Open()).ReadToEnd();
            Assert.Contains(@"%USERPROFILE%\\Documents", vrsettings);

            string summary = new StreamReader(zip.GetEntry("summary.txt")!.Open()).ReadToEnd();
            Assert.Contains("[Info] - Loaded server driver flugelkranz (%USERPROFILE%\\vr\\driver_flugelkranz.dll)", summary);
            Assert.Contains("  FlugelKranz flags: observeFrames=1 driverAudit=1", summary);
            Assert.Contains("  all     seconds=3  headAge>=47ms=1 of 3 (33.33%)  missed=3 of 6 layers (50.00%)  held=3  identityBypass seconds=0", summary);
            Assert.Contains("  flying  seconds=2  headAge>=47ms=1 of 2 (50.00%)", summary);
            Assert.Contains("  interval 10-20 ms seconds=1  commandStepM mean=0.1200", summary);
            Assert.Contains("  interval <10 ms   seconds=1  commandStepM mean=0.0250", summary);
            Assert.Contains("      2x Tracker lost", summary);
            Assert.Contains("      1x x|ERROR|bad", summary);
            Assert.Contains(@"  steamvr\vrserver.txt", summary);
        }
        finally { Directory.Delete(root, true); }
    }

    // Plain [Fact] on the headless session, as in LocalizationTests ([AvaloniaFact] discovery is broken).
    [Fact]
    public Task SettingsSectionListsTheFilesThenSavesTheZip() =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DiagnosticLogTests).Assembly)
            .Dispatch(async () => { await ListThenSave(); return true; }, CancellationToken.None);

    private static async Task ListThenSave()
    {
        string root = Directory.CreateTempSubdirectory("fk-diag-ui-").FullName;
        string settingsPath = Path.Combine(root, "config.json");
        await using var vm = new MainViewModel("/nonexistent/flugelkranz-test.so", settingsPath);
        var window = new Window { Width = 1100, Height = 650, Content = new MainView(vm) };
        window.Show();
        try
        {
            var sources = new DiagnosticSources(Path.Combine(root, "Steam"), Path.Combine(root, "slime"), Path.Combine(root, "vd"),
                Path.Combine(root, "none.vrpath"), settingsPath);
            Directory.CreateDirectory(sources.SteamLogs);
            File.WriteAllText(Path.Combine(sources.SteamLogs, "vrserver.txt"), "Active HMD set to x\n");
            vm.DiagnosticSourcesFactory = () => sources;
            bool steamVrRunning = true;
            vm.SteamVrRunningProbe = () => steamVrRunning;
            vm.DiagnosticArchivePathFactory = _ => Path.Combine(root, "out", "logs.zip");
            vm.Language = "en";
            await vm.ToggleSettingsCommand.ExecuteAsync(null);
            window.UpdateLayout();
            Assert.False(vm.IsDiagnosticPlanVisible);

            var prepare = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PrepareDiagnosticLogs");
            Assert.Equal("Save diagnostic logs…", prepare.Content);
            prepare.Command!.Execute(null);
            window.UpdateLayout();
            Assert.True(vm.IsDiagnosticPlanVisible);
            var warning = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "DiagnosticSteamVrRunning");
            Assert.True(warning.IsEffectivelyVisible);
            Assert.StartsWith("⚠ SteamVR is still running.", warning.Text);
            steamVrRunning = false;
            prepare.Command!.Execute(null);
            window.UpdateLayout();
            Assert.False(warning.IsEffectivelyVisible);
            steamVrRunning = true;
            prepare.Command!.Execute(null);
            window.UpdateLayout();
            Assert.True(warning.IsEffectivelyVisible, "saving stays possible while SteamVR runs");
            var list = window.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.Name == "DiagnosticFileList");
            Assert.True(list.IsEffectivelyVisible);
            Assert.StartsWith("steamvr" + Path.DirectorySeparatorChar + "vrserver.txt   ", list.Text);
            Assert.Equal("These 2 files and a summary.txt go into one zip:", vm.DiagnosticPlanHeader);
            Assert.Contains("flugelkranz" + Path.DirectorySeparatorChar + "config.json", list.Text);

            await vm.SaveDiagnosticLogsCommand.ExecuteAsync(null);
            window.UpdateLayout();
            Assert.False(vm.IsDiagnosticPlanVisible);
            Assert.True(File.Exists(Path.Combine(root, "out", "logs.zip")));
            Assert.Equal("Saved: " + Path.Combine(root, "out", "logs.zip"), vm.DiagnosticStatus);
            Assert.True(vm.HasDiagnosticArchive);
        }
        finally
        {
            window.Close();
            Directory.Delete(root, true);
        }
    }

    private static string FrameAudit(int headAge, bool flying) =>
        $"Fri Nov 06 2026 14:27:38.798 [Info] - flugelkranz: FrameAudit t=1 sampled=1 headAge={headAge} prediction=-0.004884 corrected=1 " +
        "matchError=0.000000 timed=1 identityBypass=0 physicalDt=0.000000 layers=2 missed=1 held=1 layer=[1 0 0 0] " +
        $"commandQxyzw=[0.000000 0.000000 0.000000 1.000000] commandP=[{(flying ? "1.500000" : "0.000000")} 0.000000 0.000000] " +
        "physicalQxyzw=[0.000000 0.000000 0.000000 1.000000] physicalP=[0.000000 0.000000 0.000000]";

    private static string DriverAudit(long[] buckets, double skewMean, double stepMean) =>
        "Fri Nov 06 2026 14:27:39.000 [Info] - flugelkranz: DriverAudit t=2 ipc: refreshMiss=0 sampleMiss=0 ownerRevoked=0 commandInvalid=0 " +
        $"command: changes={buckets.Sum()} intervalMs[<5,<10,<15,<20,<30,<50,<100,>=100]=[{string.Join(' ', buckets)}] " +
        "virtualStepM: n=0 max=0.0000 mean=0.0000 readback: calls=0 window=0 pending=0 previous=0 ambiguous=0 residualMaxM=0.0000 overflow=0 reorderedCommits=0 " +
        "submit[forwarded,bypass,held,timed,matched,missed,fallback]=[0 0 0 0 0 0 0] pathChanges=0 jumpMaxM=0.0000 jumpMaxDeg=0.000 " +
        "headAgeMs: n=0 max=0.00 mean=0.00 " +
        FormattableString.Invariant($"skew: n=100 skewed=90 maxM=0.2000 meanM={skewMean:0.0000} commandStepM: n={buckets.Sum()} max=0.3000 mean={stepMean:0.0000} updates: hmd=90 other=300");

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

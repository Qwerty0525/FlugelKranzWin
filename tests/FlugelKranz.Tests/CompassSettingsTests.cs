using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.VisualTree;
using FlugelKranz.Core;
using FlugelKranz.ViewModels;
using FlugelKranz.Views;
using Xunit;

namespace FlugelKranz.Tests;

/// <summary>The compass settings in config.json and the settings panel.</summary>
public class CompassSettingsTests
{
    [Fact]
    public void OldConfigsGetTheDefaultsAndChangesAreSaved()
    {
        string root = Directory.CreateTempSubdirectory("fk-compass-").FullName;
        try
        {
            string path = Path.Combine(root, "config.json");
            var store = new SettingsStore(path);
            store.Save(FlugelKranzSettings.Default);
            var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal("left", node["compass"]!["hand"]!.GetValue<string>());
            Assert.Equal("whileFlying", node["compass"]!["visibility"]!.GetValue<string>());
            Assert.Equal("teal", node["compass"]!["glow"]!.GetValue<string>());

            node.Remove("compass");
            File.WriteAllText(path, node.ToJsonString());
            Assert.Equal(CompassSettings.Default, new SettingsStore(path).Load().Compass);

            node["compass"] = new JsonObject
            {
                ["enabled"] = false, ["hand"] = "right", ["visibility"] = "always", ["glow"] = "amber", ["sizeCentimetres"] = 99
            };
            File.WriteAllText(path, node.ToJsonString());
            var loaded = new SettingsStore(path).Load().Compass;
            Assert.Equal(new CompassSettings
            {
                Enabled = false, Hand = CompassHand.Right, Visibility = CompassVisibility.Always,
                Glow = CompassGlow.Amber, SizeCentimetres = CompassSettings.MaximumSizeCentimetres
            }, loaded);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public Task SettingsPanelEditsAndResetsTheCompass() =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(CompassSettingsTests).Assembly)
            .Dispatch(async () => { await EditAndReset(); return true; }, CancellationToken.None);

    private static async Task EditAndReset()
    {
        string root = Directory.CreateTempSubdirectory("fk-compass-ui-").FullName;
        string settingsPath = Path.Combine(root, "config.json");
        var vm = new MainViewModel("/nonexistent/flugelkranz-test.so", settingsPath);
        var window = new Window { Width = 1100, Height = 650, Content = new MainView(vm) };
        window.Show();
        try
        {
            vm.Language = "en";
            await vm.ToggleSettingsCommand.ExecuteAsync(null);
            window.UpdateLayout();
            ToggleButton[] Options(string name) => window.GetVisualDescendants().OfType<WrapPanel>().Single(p => p.Name == name)
                .Children.OfType<ToggleButton>().ToArray();
            var hand = Options("CompassHand");
            var glow = Options("CompassGlow");
            var status = window.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.Name == "CompassStatus");
            Assert.Equal(new bool?[] { true, false }, hand.Select(b => b.IsChecked));
            Assert.Equal("Right hand", hand[1].Content);
            Assert.Equal(3, glow.Length);
            Assert.Equal("Appears once FlugelKranz is connected to SteamVR.", status.Text);

            hand[1].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            glow[2].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new bool?[] { false, true }, hand.Select(b => b.IsChecked));
            vm.CompassVisibilityIndex = 1;
            vm.CompassSizeCentimetres = 6;
            var saved = JsonNode.Parse(File.ReadAllText(settingsPath))!["compass"]!;
            Assert.Equal("right", saved["hand"]!.GetValue<string>());
            Assert.Equal("amber", saved["glow"]!.GetValue<string>());
            Assert.Equal("always", saved["visibility"]!.GetValue<string>());
            Assert.Equal(6, saved["sizeCentimetres"]!.GetValue<double>(), 3);
            Assert.Equal("Size: 6.0 cm", vm.CompassSizeLabel);

            if (Environment.GetEnvironmentVariable("FLUGELKRANZ_TEST_SCREENSHOT_DIR") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                window.GetVisualDescendants().OfType<WrapPanel>().Single(p => p.Name == "CompassHand").BringIntoView();
                window.UpdateLayout();
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(1100, 650), new Avalonia.Vector(96, 96));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(directory, "flugelkranz-compass-settings-en.png"));
            }

            vm.CompassEnabled = false;
            Assert.Equal("Off", vm.CompassStatusText);

            vm.Language = "ja";
            Assert.Equal("右手", hand[1].Content);
            Assert.Equal("大きさ: 6.0 cm", vm.CompassSizeLabel);

            vm.ResetCompassEnabledCommand.Execute(null);
            vm.ResetCompassHandCommand.Execute(null);
            vm.ResetCompassVisibilityCommand.Execute(null);
            vm.ResetCompassGlowCommand.Execute(null);
            vm.ResetCompassSizeCommand.Execute(null);
            Assert.Equal(new bool?[] { true, false }, hand.Select(b => b.IsChecked));
            Assert.Equal(new bool?[] { true, false, false }, glow.Select(b => b.IsChecked));
            await vm.DisposeAsync();
            Assert.Equal(CompassSettings.Default, new SettingsStore(settingsPath).Load().Compass);
        }
        finally
        {
            window.Close();
            Directory.Delete(root, true);
        }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.VisualTree;
using FlugelKranz.Localization;
using FlugelKranz.ViewModels;
using FlugelKranz.Views;
using System.Globalization;
using Xunit;

namespace FlugelKranz.Tests;

/// <summary>
/// The settings panel's fixed header and the single language selector. Run through
/// HeadlessUnitTestSession.Dispatch like <see cref="LocalizationTests"/>, since [AvaloniaFact]
/// does not load with xunit.v3 4.x.
/// </summary>
public class SettingsPanelLayoutTests
{
    [Fact]
    public Task HeaderWithCloseStaysAtTheTopWhileTheSettingsScroll() =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SettingsPanelLayoutTests).Assembly)
            .Dispatch(async () => { await ScrollThePanel(); return true; }, CancellationToken.None);

    [Fact]
    public Task OnlyTheMainLanguageSelectorRemainsAndThePanelLeavesItUncovered() =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SettingsPanelLayoutTests).Assembly)
            .Dispatch(async () => { await CheckSelector(); return true; }, CancellationToken.None);

    private static async Task<(MainViewModel Model, Window Window, string Path)> OpenSettings(double width)
    {
        string settingsPath = Path.Combine(Path.GetTempPath(), $"flugelkranz-test-{Guid.NewGuid():N}.json");
        var vm = new MainViewModel("/nonexistent/flugelkranz-test.so", settingsPath) { Language = "en" };
        var window = new Window { Width = width, Height = 650, Content = new MainView(vm) };
        window.Show();
        await vm.ToggleSettingsCommand.ExecuteAsync(null);
        window.UpdateLayout();
        return (vm, window, settingsPath);
    }

    private static async Task Close(MainViewModel vm, Window window, string settingsPath)
    {
        window.Close();
        await vm.DisposeAsync();
        File.Delete(settingsPath);
    }

    private static async Task ScrollThePanel()
    {
        var (vm, window, settingsPath) = await OpenSettings(1100);
        try
        {
            var scroll = Assert.Single(window.GetVisualDescendants().OfType<ScrollViewer>());
            var close = Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Close"));
            var title = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Settings");
            // Title and Close live outside the scroll area, above it.
            Assert.DoesNotContain(scroll, close.GetVisualAncestors());
            Assert.DoesNotContain(scroll, title.GetVisualAncestors());
            var before = close.TranslatePoint(default, window)!.Value;
            Assert.True(before.Y < scroll.TranslatePoint(default, window)!.Value.Y, "Close sits above the scroll area");

            Assert.True(scroll.Extent.Height > scroll.Viewport.Height + 200, "the settings are long enough to scroll");
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            window.UpdateLayout();
            Assert.True(scroll.Offset.Y > 200, $"scrolled to {scroll.Offset.Y}");
            Assert.Equal(before, close.TranslatePoint(default, window)!.Value);
            Assert.True(close.IsEffectivelyVisible);
            Assert.InRange(before.Y, 0, 80);

            // It still closes the panel from there.
            close.Command!.Execute(null);
            for (int i = 0; i < 50 && vm.SettingsPanelWidth > 0; i++) await Task.Delay(20);
            Assert.False(vm.IsSettingsOpen);
        }
        finally { await Close(vm, window, settingsPath); }
    }

    private static async Task CheckSelector()
    {
        var (vm, window, settingsPath) = await OpenSettings(1100);
        try
        {
            ToggleButton[] Selectors() => window.GetVisualDescendants().OfType<ToggleButton>()
                .Where(b => b.Classes.Contains("language-option")).ToArray();
            var panel = Assert.Single(window.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("settings-surface"));
            // One selector, on the main surface, not inside the settings panel.
            var selectors = Selectors();
            Assert.Equal(UiStrings.Languages.Count, selectors.Length);
            Assert.All(selectors, b => Assert.DoesNotContain(panel, b.GetVisualAncestors()));

            bool Uncovered()
            {
                window.UpdateLayout();
                double panelLeft = panel.TranslatePoint(default, window)!.Value.X;
                return Selectors().All(b => b.IsEffectivelyVisible && b.TranslatePoint(default, window)!.Value.X >= -0.5 &&
                    b.TranslatePoint(new Point(b.Bounds.Width, b.Bounds.Height), window)!.Value.X <= panelLeft + 0.5);
            }
            // At the default 1100 px the panel (80 %) leaves the selector beside it, so it still switches the UI.
            Assert.True(Uncovered(), "selector beside the open panel at 1100 px");
            var japanese = Selectors().First(b => Equals(b.Tag, "ja"));
            japanese.Command!.Execute(japanese.CommandParameter);
            Assert.Equal("ja", vm.Language);

            // Narrower windows (each a fresh window: a headless window keeps its first size). The main
            // column is 20 % of the width, and at 720 px or less the panel takes the whole window.
            var covered = new List<int>();
            foreach (int width in new[] { 1000, 900, 850, 830, 820, 810, 800, 780, 760, 740, 721, 720, 600 })
            {
                var (narrowModel, narrowWindow, narrowPath) = await OpenSettings(width);
                try
                {
                    var narrowPanel = narrowWindow.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("settings-surface"));
                    double panelLeft = narrowPanel.TranslatePoint(default, narrowWindow)!.Value.X;
                    bool fits = narrowWindow.GetVisualDescendants().OfType<ToggleButton>()
                        .Where(b => b.Classes.Contains("language-option"))
                        .All(b => b.IsEffectivelyVisible &&
                            b.TranslatePoint(default, narrowWindow)!.Value.X >= -0.5 &&
                            b.TranslatePoint(new Point(b.Bounds.Width, b.Bounds.Height), narrowWindow)!.Value.X <= panelLeft + 0.5);
                    if (!fits) covered.Add(width);
                }
                finally { await Close(narrowModel, narrowWindow, narrowPath); }
            }
            if (Environment.GetEnvironmentVariable("FLUGELKRANZ_LAYOUT_REPORT") is { Length: > 0 } report)
                File.WriteAllText(report, "covered at: " + string.Join(", ", covered.Select(w => w.ToString(CultureInfo.InvariantCulture))));
            Assert.Contains(720, covered);
            Assert.Contains(600, covered);
            Assert.DoesNotContain(1000, covered);
        }
        finally { await Close(vm, window, settingsPath); }
    }
}

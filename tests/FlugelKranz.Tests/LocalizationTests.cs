using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using FlugelKranz.Localization;
using FlugelKranz.ViewModels;
using FlugelKranz.Views;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace FlugelKranz.Tests;

public class LocalizationTests
{
    private static readonly Regex Japanese = new(@"[぀-ヿ㐀-鿿＀-￯]");
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}");

    [Fact]
    public void EveryLanguageHasTheJapaneseKeysAndPlaceholders()
    {
        var source = UiStrings.Table(UiStrings.DefaultLanguage);
        Assert.NotEmpty(source);
        Assert.Contains(UiStrings.Languages, l => l.Code == "ja" && l.SelectorLabel == "JPN");
        Assert.Contains(UiStrings.Languages, l => l.Code == "en" && l.SelectorLabel == "ENG");
        foreach (var language in UiStrings.Languages.Where(l => l.Code != UiStrings.DefaultLanguage))
        {
            var table = UiStrings.Table(language.Code);
            Assert.Empty(source.Keys.Except(table.Keys).Select(k => $"{language.Code} is missing {k}"));
            Assert.Empty(table.Keys.Except(source.Keys).Select(k => $"{language.Code} has unknown {k}"));
            foreach (var (key, text) in table)
            {
                Assert.Equal(Placeholders(source[key]), Placeholders(text));
                if (language.Code == "en")
                    Assert.False(Japanese.IsMatch(text), $"en.{key} still contains Japanese: {text}");
            }
        }
    }

    [Fact]
    public void JapaneseCoreAndOpenVrMessagesAllHaveATranslation()
    {
        var english = new UiStrings("en");
        var japanese = new UiStrings("ja");
        var literals = SourceLiterals("src/FlugelKranz.Core", "src/FlugelKranz.OpenVR").ToArray();
        Assert.Contains(literals, l => l.Contains("ランタイムに接続しています"));
        foreach (string literal in literals)
        {
            Assert.Equal(literal, japanese.TranslateRuntime(literal));
            string translated = english.TranslateRuntime(literal);
            Assert.False(Japanese.IsMatch(translated), $"Untranslated runtime message: {literal} -> {translated}");
        }
    }

    [Fact]
    public void ComposedRuntimeErrorsTranslateEachPart()
    {
        var strings = new UiStrings("en");
        Assert.Equal("SteamVR connection failed: Init_HmdNotFound. Start SteamVR.",
            strings.TranslateRuntime("SteamVR 接続失敗: Init_HmdNotFound。SteamVR を起動してください。"));
        Assert.Equal("SteamVR has exited.\nRestore failed: The FlugelKranz driver stopped responding.",
            strings.TranslateRuntime("SteamVR が終了しました。\n復元に失敗しました: FlugelKranzドライバーの応答が停止しました。"));
        Assert.Equal("Invalid space transform. (Parameter 'offset')",
            strings.TranslateRuntime(new ArgumentException("空間変換が不正です。", "offset").Message));
        Assert.Equal("missing runtime: test.so", strings.TranslateRuntime("missing runtime: test.so"));
    }

    [Fact]
    public async Task LanguageIsSavedInConfigAndJapaneseIsTheDefault()
    {
        string settingsPath = TemporarySettingsPath();
        try
        {
            await using (var vm = new MainViewModel("/nonexistent/flugelkranz-test.so", settingsPath))
            {
                Assert.Equal("ja", vm.Language);
                Assert.Equal("オフ — オンにするとランタイムへ接続します。", vm.Status);
                vm.Language = "en";
                Assert.Equal("en", ReadLanguage(settingsPath));
                Assert.Equal("OFF — turn ON to connect to the runtime.", vm.Status);
                vm.Language = "xx";
                Assert.Equal("ja", vm.Language);
                vm.Language = "en";
            }
            await using (var reopened = new MainViewModel("/nonexistent/flugelkranz-test.so", settingsPath))
            {
                Assert.Equal("en", reopened.Language);
                Assert.Equal("Drag: 0.01 s", reopened.DragSmoothLabel);
            }

            // A config written before the language switch existed keeps the Japanese UI.
            var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(settingsPath))!;
            json.Remove("language");
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(json));
            await using (var legacy = new MainViewModel("/nonexistent/flugelkranz-test.so", settingsPath))
                Assert.Equal("ja", legacy.Language);
            Assert.Equal("ja", ReadLanguage(settingsPath));
        }
        finally { File.Delete(settingsPath); }
    }

    // Plain [Fact] driving the headless session directly: [AvaloniaFact] discovery is broken
    // with xunit.v3 4.x (MissingMethodException), which is why the MainView tests fail to run.
    [Fact]
    public Task SelectorSwitchesTheRenderedUiLive() =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(LocalizationTests).Assembly)
            .Dispatch(async () => { await RenderBothLanguages(); return true; }, CancellationToken.None);

    private static async Task RenderBothLanguages()
    {
        string settingsPath = TemporarySettingsPath();
        await using var vm = new MainViewModel("/nonexistent/flugelkranz-test.so", settingsPath);
        var window = new Window { Width = 1100, Height = 650, Content = new MainView(vm) };
        window.Show();
        try
        {
            await vm.ToggleSettingsCommand.ExecuteAsync(null);
            window.UpdateLayout();
            AssertText(window, "設定");
            AssertText(window, "2.00 /秒");
            AssertText(window, "送信中の STAGE オフセット: 未接続");
            Capture(window, "ja");

            var selectors = window.GetVisualDescendants().OfType<ToggleButton>()
                .Where(b => b.Classes.Contains("language-option")).ToArray();
            // Only the selector on the main surface; the settings header has none.
            Assert.Equal(UiStrings.Languages.Count, selectors.Length);
            Assert.All(selectors.Where(b => Equals(b.Tag, "ja")), b => Assert.True(b.IsChecked));
            var english = selectors.First(b => Equals(b.Content, "ENG"));
            english.Command!.Execute(english.CommandParameter);
            window.UpdateLayout();

            Assert.Equal("en", vm.Language);
            Assert.All(selectors, b => Assert.Equal(Equals(b.Tag, "en"), b.IsChecked));
            AssertText(window, "Settings");
            AssertText(window, "Free flight — Head pilot (experimental)");
            AssertText(window, "2.00 /s");
            AssertText(window, "Multiplier: 1.00");
            AssertText(window, "STAGE offset being sent: not connected");
            AssertText(window, "OFF — turn ON to connect to the runtime.");
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Close"));
            var resetPose = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                b => ReferenceEquals(vm.ResetCommand, b.Command));
            Assert.Equal("Reset to the position and orientation from when you connected", ToolTip.GetTip(resetPose));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text is { } text && Japanese.IsMatch(text) && !UiStrings.Languages.Any(l => l.NativeName == text));

            vm.Status = "SteamVR が終了しました。";
            AssertText(window, "SteamVR has exited.");
            vm.ToggleModeCommand.Execute(null);
            AssertText(window, "Switched to infinite walking mode. Release the inputs before use.");
            vm.Status = "オフ — オンにするとランタイムへ接続します。";
            AssertText(window, "OFF — turn ON to connect to the runtime.");
            Capture(window, "en");

            var japanese = selectors.First(b => Equals(b.Content, "JPN"));
            japanese.Command!.Execute(japanese.CommandParameter);
            window.UpdateLayout();
            AssertText(window, "設定");
            AssertText(window, "オフ — オンにするとランタイムへ接続します。");
        }
        finally
        {
            window.Close();
            File.Delete(settingsPath);
        }
    }

    private static void AssertText(Window window, string text) =>
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == text);

    private static void Capture(Window window, string language)
    {
        if (Environment.GetEnvironmentVariable("FLUGELKRANZ_TEST_SCREENSHOT_DIR") is not { Length: > 0 } directory)
            return;
        Directory.CreateDirectory(directory);
        window.UpdateLayout();
        using var bitmap = new RenderTargetBitmap(new PixelSize(1100, 650), new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(directory, $"flugelkranz-{language}.png"), PngBitmapEncoderOptions.Default);
    }

    private static int[] Placeholders(string text) =>
        Placeholder.Matches(text).Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order().ToArray();

    private static string? ReadLanguage(string settingsPath) =>
        JsonDocument.Parse(File.ReadAllText(settingsPath)).RootElement.GetProperty("language").GetString();

    /// <summary>Japanese string literals in the given source folders, with interpolations replaced by a sample value.</summary>
    private static IEnumerable<string> SourceLiterals(params string[] folders)
    {
        string root = RepositoryRoot();
        var literal = new Regex(@"(?<interpolated>\$)?""(?<body>(?:[^""\\]|\\.)*)""");
        foreach (string folder in folders)
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}Vendor{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;
            foreach (string line in File.ReadLines(file))
            {
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                foreach (Match match in literal.Matches(line))
                {
                    string body = match.Groups["body"].Value;
                    if (!Japanese.IsMatch(body)) continue;
                    if (match.Groups["interpolated"].Success)
                        body = Regex.Replace(body, @"\{[^{}]*\}", "Sample");
                    yield return Regex.Unescape(body);
                }
            }
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FlugelKranz.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("FlugelKranz.slnx not found above the test output directory.");
    }

    private static string TemporarySettingsPath() =>
        Path.Combine(Path.GetTempPath(), $"flugelkranz-test-{Guid.NewGuid():N}.json");
}

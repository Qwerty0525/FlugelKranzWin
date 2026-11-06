namespace FlugelKranz.Localization;

/// <summary>
/// A message kept in language-neutral form so it can be re-rendered when the UI language
/// changes: either a string-table key, or raw text from Core / OpenVR (Japanese source text,
/// translated through <see cref="UiStrings.TranslateRuntime"/> when displayed).
/// </summary>
public readonly record struct UiMessage(string? Key, string? Text)
{
    public static UiMessage FromKey(string key) => new(key, null);
    public static UiMessage FromText(string? text) => new(null, text ?? "");

    public string Resolve(UiStrings strings) => Key is not null ? strings[Key] : strings.TranslateRuntime(Text);
}

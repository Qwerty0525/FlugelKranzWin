using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlugelKranz.Localization;

/// <summary>One UI language table, loaded from an embedded <c>Localization/Languages/&lt;code&gt;.json</c>.</summary>
public sealed record UiLanguage(string Code, string SelectorLabel, string NativeName, int Order);

/// <summary>
/// UI string lookup for one window. Each language is a JSON table embedded from
/// <c>Localization/Languages/</c>; adding a file there adds a language to the selector.
/// Japanese is the source and fallback table. Status text produced by Core / OpenVR
/// (and exception messages) stays Japanese at the source, so logs are unchanged;
/// <see cref="TranslateRuntime"/> maps it back through the <c>runtime.*</c> entries
/// of the Japanese table (and any other fixed Japanese entry) for display only.
/// </summary>
public sealed class UiStrings
{
    public const string DefaultLanguage = "ja";

    public UiStrings(string? language = null) => Language = Normalize(language);

    public static IReadOnlyList<UiLanguage> Languages => Catalog.Instance.Languages;

    public string Language { get; private set; }

    public event EventHandler? LanguageChanged;

    public static string Normalize(string? language) =>
        language is not null && Catalog.Instance.Tables.ContainsKey(language) ? language : DefaultLanguage;

    public bool SetLanguage(string? language)
    {
        string normalized = Normalize(language);
        if (normalized == Language) return false;
        Language = normalized;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public string this[string key] => Lookup(Language, key);

    public string Format(string key, params object?[] args)
    {
        string format = this[key];
        try { return string.Format(CultureInfo.CurrentCulture, format, args); }
        catch (FormatException)
        {
            return string.Format(CultureInfo.CurrentCulture, Lookup(DefaultLanguage, key), args);
        }
    }

    /// <summary>Translates a Japanese status / error text from Core or OpenVR; unknown text passes through.</summary>
    public string TranslateRuntime(string? text)
    {
        if (string.IsNullOrEmpty(text) || Language == DefaultLanguage) return text ?? "";
        if (TryTranslateLine(text, out string whole)) return whole;
        if (!text.Contains('\n')) return text;
        return string.Join('\n', text.Split('\n').Select(line => TryTranslateLine(line, out string translated) ? translated : line));
    }

    private bool TryTranslateLine(string line, out string translated)
    {
        var catalog = Catalog.Instance;
        if (catalog.RuntimeExact.TryGetValue(line, out string? key))
        {
            translated = this[key];
            return true;
        }
        foreach (var (pattern, patternKey, count) in catalog.RuntimePatterns)
        {
            var match = pattern.Match(line);
            if (!match.Success) continue;
            var args = new object?[count];
            for (int i = 0; i < count; i++)
                args[i] = TranslateRuntime(match.Groups["p" + i].Value);
            try
            {
                translated = string.Format(CultureInfo.CurrentCulture, this[patternKey], args);
                return true;
            }
            catch (FormatException) { break; }
        }
        // ArgumentException appends " (Parameter 'name')" to the message.
        var parameter = ArgumentSuffix.Match(line);
        if (parameter.Success && TryTranslateLine(parameter.Groups["message"].Value, out string message))
        {
            translated = message + parameter.Groups["suffix"].Value;
            return true;
        }
        translated = line;
        return false;
    }

    private static readonly Regex ArgumentSuffix =
        new(@"^(?<message>.+?)(?<suffix> \(Parameter '[^']*'\))$", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static string Lookup(string language, string key)
    {
        var tables = Catalog.Instance.Tables;
        if (tables.TryGetValue(language, out var table) && table.TryGetValue(key, out string? value)) return value;
        if (tables.TryGetValue(DefaultLanguage, out var source) && source.TryGetValue(key, out value)) return value;
        return key;
    }

    /// <summary>The raw entries of one language table (for tests and tooling).</summary>
    public static IReadOnlyDictionary<string, string> Table(string language) =>
        Catalog.Instance.Tables.TryGetValue(language, out var table) ? table : new Dictionary<string, string>();

    private sealed class Catalog
    {
        private const string ResourcePrefix = "FlugelKranz.Localization.Languages.";
        private static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.CultureInvariant);

        public static Catalog Instance { get; } = new();

        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Tables { get; }
        public IReadOnlyList<UiLanguage> Languages { get; }
        public IReadOnlyDictionary<string, string> RuntimeExact { get; }
        public IReadOnlyList<(Regex Pattern, string Key, int Count)> RuntimePatterns { get; }

        private Catalog()
        {
            var assembly = typeof(UiStrings).Assembly;
            var tables = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
            var languages = new List<UiLanguage>();
            foreach (string name in assembly.GetManifestResourceNames())
            {
                if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                    !name.EndsWith(".json", StringComparison.Ordinal))
                    continue;
                string code = name[ResourcePrefix.Length..^".json".Length];
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null) continue;
                var (language, table) = Read(code, stream);
                tables[code] = table;
                languages.Add(language);
            }
            Tables = tables;
            Languages = languages.OrderBy(l => l.Order).ThenBy(l => l.Code, StringComparer.Ordinal).ToArray();

            var exact = new Dictionary<string, string>(StringComparer.Ordinal);
            var patterns = new List<(Regex, string, int)>();
            if (tables.TryGetValue(DefaultLanguage, out var source))
            {
                foreach (var (key, text) in source)
                {
                    bool runtime = key.StartsWith("runtime.", StringComparison.Ordinal);
                    var parts = Placeholder.Split(text);
                    if (parts.Length == 1)
                    {
                        // Any fixed Japanese UI text can be mapped back; runtime.* wins on duplicates.
                        if (runtime || !exact.ContainsKey(text)) exact[text] = key;
                        continue;
                    }
                    if (!runtime) continue;
                    // Split keeps the captured index at odd positions.
                    var regex = new StringBuilder("^");
                    int count = 0;
                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (i % 2 == 0) regex.Append(Regex.Escape(parts[i]));
                        else
                        {
                            int index = int.Parse(parts[i], CultureInfo.InvariantCulture);
                            regex.Append("(?<p").Append(index).Append(">.*?)");
                            count = Math.Max(count, index + 1);
                        }
                    }
                    regex.Append('$');
                    patterns.Add((new Regex(regex.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant), key, count));
                }
            }
            RuntimeExact = exact;
            RuntimePatterns = patterns;
        }

        private static (UiLanguage, IReadOnlyDictionary<string, string>) Read(string code, Stream stream)
        {
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            var root = document.RootElement;
            string label = code.ToUpperInvariant();
            string nativeName = code;
            int order = int.MaxValue;
            if (root.TryGetProperty("_language", out var meta) && meta.ValueKind == JsonValueKind.Object)
            {
                if (meta.TryGetProperty("selectorLabel", out var value) && value.ValueKind == JsonValueKind.String)
                    label = value.GetString()!;
                if (meta.TryGetProperty("nativeName", out value) && value.ValueKind == JsonValueKind.String)
                    nativeName = value.GetString()!;
                if (meta.TryGetProperty("order", out value) && value.ValueKind == JsonValueKind.Number)
                    order = value.GetInt32();
            }
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("strings", out var strings) && strings.ValueKind == JsonValueKind.Object)
                foreach (var entry in strings.EnumerateObject())
                    if (entry.Value.ValueKind == JsonValueKind.String)
                        table[entry.Name] = entry.Value.GetString()!;
            return (new UiLanguage(code, label, nativeName, order), table);
        }
    }
}

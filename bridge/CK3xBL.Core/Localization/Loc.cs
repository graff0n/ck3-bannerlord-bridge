// Player-facing texts of the battle loop (title card, Bannerlord objective and host names, module result footer, result card,
// battle notices) in the language the player chose. One flat JSON file per language in bridge/data/i18n, embedded as
// CK3xBL.i18n.<lang>.json; keys starting with "_" (such as "_meta") are not texts. English is the reference: a key missing in
// another language, empty there, or with other placeholder names than in English falls back to English, key by key. A language
// marked "_meta": { "reviewed": false } is a community translation: it is shown anyway (Reviewed says so for the journal).
// Logs stay English: callers use En() for what is journaled and T() for what the player sees.
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CK3xBL.Core.Localization;

public static partial class Loc
{
    public const string English = "en";

    /// <summary>One loaded language: its texts and whether a native speaker reviewed it. Immutable.</summary>
    private sealed record Table(string Language, bool Reviewed, IReadOnlyDictionary<string, string> Texts);

    private sealed record State(Table Current, Table English);

    private static readonly Table EnglishTable = Embedded(English) ?? new Table(English, true, new Dictionary<string, string>());
    private static readonly Dictionary<string, Table> Loaded = new(StringComparer.OrdinalIgnoreCase);   // LoadFrom (tests)
    private static readonly object Gate = new();
    private static State _state = new(EnglishTable, EnglishTable);   // swapped in one assignment

    /// <summary>The language in use ("en" until <see cref="Use"/> picks another).</summary>
    public static string Language => Volatile.Read(ref _state).Current.Language;

    /// <summary>False for a community translation not yet reviewed ("_meta": { "reviewed": false }); true for English.</summary>
    public static bool Reviewed => Volatile.Read(ref _state).Current.Reviewed;

    /// <summary>
    /// Switches to <paramref name="language"/> and returns the language in effect: English when that language has no file or its
    /// file is unreadable.
    /// </summary>
    public static string Use(string? language)
    {
        var table = Find(language) ?? EnglishTable;
        Volatile.Write(ref _state, new State(table, EnglishTable));
        return table.Language;
    }

    /// <summary>The text of <paramref name="key"/> in the current language (per-key English fallback); "[key]" when English lacks it.</summary>
    public static string T(string key, params (string Name, object? Value)[] args)
    {
        var state = Volatile.Read(ref _state);
        if (!state.English.Texts.TryGetValue(key, out var english)) return $"[{key}]";
        string template = english;
        if (!ReferenceEquals(state.Current, state.English) && state.Current.Texts.TryGetValue(key, out var own) && own.Length > 0 &&
            PlaceholderNames(own).SetEquals(PlaceholderNames(english)))
            template = own;
        return Format(template, args);
    }

    /// <summary>The English text of <paramref name="key"/>, whatever the current language (for the journal).</summary>
    public static string En(string key, params (string Name, object? Value)[] args) =>
        Volatile.Read(ref _state).English.Texts.TryGetValue(key, out var english) ? Format(english, args) : $"[{key}]";

    /// <summary>True when English has <paramref name="key"/>.</summary>
    public static bool Has(string key) => Volatile.Read(ref _state).English.Texts.ContainsKey(key);

    /// <summary>The text of <paramref name="key"/> split on '|'; an empty text is an empty list.</summary>
    public static IReadOnlyList<string> List(string key)
    {
        string value = T(key);
        return value.Length == 0 ? [] : value.Split('|');
    }

    /// <summary>A whole number with the group separator of the language ("number.group_separator"): 2,224 / 2 224.</summary>
    public static string Number(int n) => n.ToString("N0", CultureInfo.InvariantCulture).Replace(",", T("number.group_separator"));

    /// <summary>Tests: registers <paramref name="json"/> as the file of <paramref name="language"/>. False when it is unreadable
    /// (the language then stays unavailable, so <see cref="Use"/> falls back to English).</summary>
    internal static bool LoadFrom(string language, string json)
    {
        var table = Parse(language, json);
        lock (Gate)
        {
            if (table is null) Loaded.Remove(language);
            else Loaded[language] = table;
        }
        return table is not null;
    }

    /// <summary>Tests: uses <paramref name="language"/> until disposed, then restores the language that was in use.</summary>
    internal static IDisposable Scope(string language)
    {
        var previous = Volatile.Read(ref _state);
        Use(language);
        return new Restore(previous);
    }

    private sealed class Restore(State previous) : IDisposable
    {
        public void Dispose() => Volatile.Write(ref _state, previous);
    }

    // ------------------------------------------------------------------------------------------------------------------ loading

    private static Table? Find(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        string lang = language.Trim().ToLowerInvariant();
        if (lang == English) return EnglishTable;
        lock (Gate)
            if (Loaded.TryGetValue(lang, out var loaded)) return loaded;
        return Embedded(lang);
    }

    private static Table? Embedded(string language)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"CK3xBL.i18n.{language}.json");
            if (stream is null) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return Parse(language, reader.ReadToEnd());
        }
        catch (IOException) { return null; }
    }

    private static Table? Parse(string language, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            bool reviewed = true;
            var texts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name.StartsWith('_'))
                {
                    if (p.Name == "_meta" && p.Value.ValueKind == JsonValueKind.Object &&
                        p.Value.TryGetProperty("reviewed", out var r) && r.ValueKind == JsonValueKind.False)
                        reviewed = false;
                    continue;
                }
                if (p.Value.ValueKind == JsonValueKind.String) texts[p.Name] = p.Value.GetString()!;
            }
            return new Table(language.ToLowerInvariant(), reviewed, texts);
        }
        catch (JsonException) { return null; }
    }

    // --------------------------------------------------------------------------------------------------------------- formatting

    /// <summary>The placeholder names of a template ("{place|de}" counts as "place").</summary>
    internal static HashSet<string> PlaceholderNames(string template) =>
        Placeholder().Matches(template).Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);

    private static string Format(string template, (string Name, object? Value)[] args)
    {
        if (args.Length == 0 || template.IndexOf('{') < 0) return template;
        return Placeholder().Replace(template, m =>
        {
            string name = m.Groups["name"].Value;
            foreach (var (n, v) in args)
            {
                if (n != name) continue;
                string value = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
                return m.Groups["filter"].Value switch
                {
                    "" => value,
                    "cap" => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..],
                    "lower" => value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..],
                    "de" => De(value),
                    _ => m.Value,   // an unknown filter is left as it is
                };
            }
            return m.Value;   // an unknown placeholder is left as it is
        });
    }

    /// <summary>French "de X", elided before a vowel: "d'Angers", "d'Évreux", "de Paris".</summary>
    internal static string De(string value)
    {
        if (value.Length == 0) return "de ";
        char first = char.ToLowerInvariant(value.Normalize(NormalizationForm.FormD)[0]);
        return "aeiouy".Contains(first) ? "d'" + value : "de " + value;
    }

    [GeneratedRegex(@"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\|(?<filter>[a-z]+))?\}")]
    private static partial Regex Placeholder();
}

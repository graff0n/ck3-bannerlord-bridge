// The battle loop's languages (Localization/Loc.cs, LanguageChoice.cs, bridge/data/i18n): files, fallbacks, keys used by the code,
// French elision, the CK3 mod's option names, the result card's style and the ux.language setting.
using System.Text.Json;
using System.Text.RegularExpressions;
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Ck3;
using CK3xBL.Core.Config;
using CK3xBL.Core.Localization;
using CK3xBL.Ux;
using CK3xBL.Ux.Rendering;

namespace CK3xBL.SelfTest;

internal static class I18nTests
{
    public static void Register(TestRegistry r)
    {
        r.Add("i18n.languages_match_english", LanguagesMatchEnglish);
        r.Add("i18n.fallback_to_english", FallbackToEnglish);
        r.Add("i18n.unreviewed_language_is_shown", UnreviewedLanguageIsShown);
        r.Add("i18n.code_keys_exist", CodeKeysExist);
        r.Add("i18n.de_elision", DeElision);
        r.Add("i18n.ck3_texts_match_mod", Ck3TextsMatchMod);
        r.Add("i18n.french_battle_loop_smoke", MappingTests.FrenchBattleLoopSmoke);
        r.Add("ux.result_card_kind_from_state", ResultCardKindFromState);
        r.Add("config.ux_language_defaults", UxLanguageDefaults);
    }

    private static readonly Lazy<BridgePaths> Paths = new(() => BridgePaths.Detect());
    private static string I18nDir => Path.Combine(Paths.Value.DataDir, "i18n");
    private static string Repo => Directory.GetParent(Paths.Value.BridgeRoot)!.FullName;

    /// <summary>The texts of a language file (keys starting with "_" are not texts).</summary>
    private static Dictionary<string, string> Texts(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return doc.RootElement.EnumerateObject().Where(p => !p.Name.StartsWith('_')).ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
    }

    private static Dictionary<string, string> EnglishTexts => Texts(Path.Combine(I18nDir, "en.json"));

    private static void LanguagesMatchEnglish()
    {
        var en = EnglishTexts;
        var files = Directory.GetFiles(I18nDir, "*.json");
        Expect.True(files.Length >= 2, $"{files.Length} language files in {I18nDir}");
        foreach (var file in files)
        {
            var own = Texts(file);
            string lang = Path.GetFileNameWithoutExtension(file);
            Expect.SequenceEqual(en.Keys.Order(StringComparer.Ordinal), own.Keys.Order(StringComparer.Ordinal), $"{lang}: the English keys");
            foreach (var (key, text) in en)
                Expect.True(Loc.PlaceholderNames(own[key]).SetEquals(Loc.PlaceholderNames(text)), $"{lang} {key}: placeholders of \"{own[key]}\" vs \"{text}\"");
        }
    }

    private static void FallbackToEnglish()
    {
        const string json = """
            { "_meta": { "language": "zz", "reviewed": true },
              "result.victory": "Sieg bei {place}",
              "result.defeat": "",
              "detail.near": "{ground} bei {somewhere}" }
            """;
        Expect.True(Loc.LoadFrom("zz", json));
        using (Loc.Scope("zz"))
        {
            Expect.Equal("zz", Loc.Language);
            Expect.Equal("Sieg bei Angers", Loc.T("result.victory", ("place", "Angers")), "the language's own text");
            Expect.Equal("Defeat at Angers", Loc.T("result.defeat", ("place", "Angers")), "an empty value: English");
            Expect.Equal("Hills near Angers", Loc.T("detail.near", ("ground", "Hills"), ("county", "Angers")), "other placeholder names: English");
            Expect.Equal("Victory at Angers", Loc.En("result.victory", ("place", "Angers")), "En is always English");
            Expect.Equal("Our losses 1 · Enemy losses 2", Loc.T("result.losses_fallback", ("mine", 1), ("theirs", 2)), "a key the language lacks: English");
            Expect.Equal("[no.such.key]", Loc.T("no.such.key"), "a key English lacks");
        }
        Expect.False(Loc.LoadFrom("zy", "{ this is not json"), "an unreadable file is refused");
        using (Loc.Scope("zy")) Expect.Equal("en", Loc.Language, "an unreadable language: English");
        Expect.Equal("en", Loc.Use("xx-none"), "no file: English");
        Expect.Equal("en", Loc.Language);
        Expect.Equal("Victory at {place}", Loc.T("result.victory"), "a placeholder without a value is left as it is");
    }

    private static void UnreviewedLanguageIsShown()
    {
        Expect.True(Loc.LoadFrom("xx", """{ "_meta": { "reviewed": false }, "result.victory": "Vittoria a {place}" }"""));
        using (Loc.Scope("xx"))
        {
            Expect.Equal("xx", Loc.Language, "a community translation is shown");
            Expect.False(Loc.Reviewed);
            Expect.Equal("Vittoria a Orangallu", Loc.T("result.victory", ("place", "Orangallu")));
            Expect.Equal("Defeat at Orangallu", Loc.T("result.defeat", ("place", "Orangallu")), "per-key fallback stays on");
        }
        Expect.True(Loc.Reviewed, "English is reviewed");
    }

    private static void CodeKeysExist()
    {
        var en = EnglishTexts;
        var call = new Regex(@"(?:Loc\.(?:T|En|List|Has)|Said\.Of)\(\s*(?<arg>[^,()]*(?:\([^()]*\)[^,()]*)*)");
        var literal = new Regex(@"(?<!\$)""(?<key>[a-z0-9_]+(?:\.[a-z0-9_]+)+)""");
        var used = new SortedSet<string>(StringComparer.Ordinal);
        var sources = Directory.EnumerateFiles(Paths.Value.BridgeRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}CK3xBL.SelfTest{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        foreach (var file in sources)
            foreach (Match c in call.Matches(File.ReadAllText(file)))
                foreach (Match l in literal.Matches(c.Groups["arg"].Value))
                    used.Add(l.Groups["key"].Value);
        Expect.True(used.Count > 80, $"{used.Count} keys found in the code");
        foreach (var key in used) Expect.True(en.ContainsKey(key), $"the code uses '{key}', which en.json lacks");

        // Keys built at run time: steer.done.<reason>, terrain.<pool terrain>, season.<season>.
        foreach (int reason in Enumerable.Range(1, 6)) Expect.True(en.ContainsKey($"steer.done.{reason}"), $"steer.done.{reason}");
        Expect.True(en.ContainsKey("steer.done.other"));
        using var terrains = JsonDocument.Parse(File.ReadAllText(Path.Combine(Paths.Value.DataDir, "terrain_scenes.json")));
        foreach (var t in terrains.RootElement.GetProperty("terrain").EnumerateObject())
            Expect.True(en.ContainsKey($"terrain.{t.Name}"), $"terrain.{t.Name}");
        foreach (var season in new[] { "spring", "summer", "fall", "winter" }) Expect.True(en.ContainsKey($"season.{season}"), $"season.{season}");
    }

    private static void DeElision()
    {
        Expect.Equal("d'Angers", Loc.De("Angers"));
        Expect.Equal("d'Évreux", Loc.De("Évreux"));
        Expect.Equal("de Paris", Loc.De("Paris"));
        Expect.Equal("de Hepung", Loc.De("Hepung"));
        Expect.True(Loc.LoadFrom("zx", """{ "title.fallback": "Bataille {place|de}", "joins.fresh_troops": "Renforts pour l'{host|lower}" }"""));
        using (Loc.Scope("zx"))
        {
            Expect.Equal("Bataille d'Évreux", Loc.T("title.fallback", ("place", "Évreux")), "{place|de}");
            Expect.Equal("Renforts pour l'ost de Gunda", Loc.T("joins.fresh_troops", ("host", "Ost de Gunda")), "{host|lower}");
        }
    }

    private static void Ck3TextsMatchMod()
    {
        var en = EnglishTexts;
        var fr = Texts(Path.Combine(I18nDir, "fr.json"));
        Expect.Equal(Ck3Texts.DustSettlesOption, en["ck3.option.dust_settles"], "en.json names the option as the bridge constant");
        string yml = Path.Combine(Repo, "ck3_mod", "ck3xbl", "localization", "french", "ck3xbl_l_french.yml");
        var line = Regex.Match(File.ReadAllText(yml), @"(?m)^\s*ck3xbl_battle\.0002\.a:\d*\s+""(?<text>[^""]*)""");
        Expect.True(line.Success, $"ck3xbl_battle.0002.a not found in {yml}");
        Expect.Equal(line.Groups["text"].Value, fr["ck3.option.dust_settles"], "fr.json names the option as the French mod");
        Expect.Equal(Ck3WindowModeGuard.WindowedNotice, en["notice.window_mode.text"]);
    }

    private static void ResultCardKindFromState()
    {
        Expect.Equal(CardKind.Defeat, TransitionUx.ResultKind(new TitleCard { Title = "Défaite à X", Defeat = true }));
        Expect.Equal(CardKind.Victory, TransitionUx.ResultKind(new TitleCard { Title = "Victoire à X", Defeat = false }));
        Expect.Equal(CardKind.Victory, TransitionUx.ResultKind(new TitleCard { Title = "Defeat at X", Defeat = false }), "the state wins over the text");
        Expect.Equal(CardKind.Defeat, TransitionUx.ResultKind(new TitleCard { Title = "Defeat at X" }), "no state: read from the title");
        Expect.Equal(CardKind.Victory, TransitionUx.ResultKind(new TitleCard { Title = "Victory at X" }));
        Expect.Equal(CardKind.Victory, TransitionUx.ResultKind(null));
    }

    private static void UxLanguageDefaults()
    {
        Expect.Equal("auto", new UxSettings().Language);
        // The real format of pdx_settings.txt (CK3 1.20), anonymised: the "language" block inside "System".
        const string sample = "\"System\"={\n\t\"display_mode\"={\n\t\tversion=0\n\t\tvalue=\"fullscreen\"\n\t}\n\t\"language\"={\n\t\tversion=0\n\t\tvalue=\"l_french\"\n\t}\n}\n\"Audio\"={\n}\n";
        Expect.Equal("l_french", LanguageChoice.ParseCk3Language(sample));
        Expect.Null(LanguageChoice.ParseCk3Language("garbage { value=\"l_french\" }"), "no language block");
        Expect.Null(LanguageChoice.ParseCk3Language(""));
        string nowhere = Path.Combine(Path.GetTempPath(), "ck3xbl-no-such-ck3-user-dir-" + Guid.NewGuid().ToString("N"));
        Expect.Equal("en", LanguageChoice.Resolve("auto", nowhere, out var ck3), "auto without pdx_settings.txt: English");
        Expect.Null(ck3);
        Expect.Equal("fr", LanguageChoice.Resolve("fr", nowhere, out _), "an explicit language");
        Expect.Equal("en", LanguageChoice.Resolve("xx", nowhere, out _), "an unknown value: CK3's language, here unknown: English");
        Expect.Equal(("fr", "de", "es", "en", "en"),
            (LanguageChoice.FromCk3("l_french"), LanguageChoice.FromCk3("l_german"), LanguageChoice.FromCk3("l_spanish"),
             LanguageChoice.FromCk3("l_russian"), LanguageChoice.FromCk3(null)));
    }
}

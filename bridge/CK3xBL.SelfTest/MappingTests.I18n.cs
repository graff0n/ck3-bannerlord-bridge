// The battle loop in French (Localization/Loc.cs): the golden scenarios planned, a reinforcement clash, the result line and the result
// card, under Loc.Scope("fr"). Registered by I18nTests as i18n.french_battle_loop_smoke.
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Json;
using CK3xBL.Core.Localization;
using CK3xBL.Core.Model;
using CK3xBL.Core.Orchestration;
using CK3xBL.Mapping.Outcome;
using CK3xBL.Mapping.Planning;

namespace CK3xBL.SelfTest;

internal static partial class MappingTests
{
    /// <summary>English words that must not survive in the French battle loop.</summary>
    private static readonly string[] EnglishLeftovers = [" against ", "Host of", "Victory", "Defeat", "Rout ", " near ", "Click '", "Reinforcements"];

    internal static void FrenchBattleLoopSmoke()
    {
        // English first: no module texts, and the request carries "texts": null (an older module reads it as absent).
        var english = Mapper.Plan(Scenarios.IndianProbe3(), new MapperOptions()).Request;
        Expect.Null(english.Presentation.Texts, "English: no module texts");
        Expect.True(ProtocolJson.Serialize(english).Contains("\"texts\":null", StringComparison.Ordinal), "English request.json: texts null");
        Expect.Null(PresentationWriter.ModuleTexts(), "English: ModuleTexts() is null");

        // CK3's own names (armies, characters, places) come from the fixtures in English: they are CK3's words, not the bridge's,
        // and are taken out before the leftover check.
        var ck3Names = new HashSet<string>(StringComparer.Ordinal);
        void AddNames(BattleSnapshot b)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(b, Json));
            void Walk(System.Text.Json.JsonElement e)
            {
                if (e.ValueKind == System.Text.Json.JsonValueKind.String && e.GetString() is { Length: > 2 } s)
                {
                    ck3Names.Add(Text.SanitizeName(s));
                    ck3Names.Add(System.Text.RegularExpressions.Regex.Replace(Text.SanitizeName(s), @"^\d+(st|nd|rd|th)\s+", ""));
                }
                else if (e.ValueKind == System.Text.Json.JsonValueKind.Object) foreach (var p in e.EnumerateObject()) Walk(p.Value);
                else if (e.ValueKind == System.Text.Json.JsonValueKind.Array) foreach (var x in e.EnumerateArray()) Walk(x);
            }
            Walk(doc.RootElement);
        }

        var shown = new List<(string What, string Text)>();
        using (Loc.Scope("fr"))
        {
            Expect.Equal("fr", Loc.Language);
            var module = Expect.NotNull(PresentationWriter.ModuleTexts(), "French: ModuleTexts() is set");
            Expect.Equal((Loc.T("module.the_battle"), Loc.T("module.outcome_won"), Loc.T("module.outcome_lost"), Loc.T("module.outcome_over"),
                    Loc.T("module.countdown_footer"), Loc.T("module.wait_footer")),
                (module.TheBattle, module.OutcomeWon, module.OutcomeLost, module.OutcomeOver, module.CountdownFooter, module.WaitFooter),
                "the six module.* templates");

            foreach (var (name, build) in Scenarios.All)
            {
                var battle = build();
                AddNames(battle);
                var req = Mapper.Plan(battle, new MapperOptions()).Request;
                var p = req.Presentation;
                Expect.NotNull(p.Texts, $"{name}: module texts sent");
                foreach (var (what, text) in new[] { ("title", p.Title), ("subtitle", p.Subtitle), ("detail", p.Detail), ("objective", p.Objective),
                             ("attacker", req.Attacker.Name), ("defender", req.Defender.Name) })
                    shown.Add(($"{name} {what}", text));

                string place = PresentationWriter.PlaceName(battle);
                var won = new Ck3Outcome { OutcomeCode = 1, PlayerWon = true, MyLost = 1234, EnemyLost = 5678, MyKills = 3 };
                var lost = new Ck3Outcome { OutcomeCode = 2, PlayerWon = false, MyLost = 4321, EnemyLost = 87, MyKills = 1 };
                foreach (var (what, o) in new[] { ("victory", won), ("defeat", lost) })
                {
                    o.Summary = OutcomeBuilder.SummaryLine(o.OutcomeCode, o, place);
                    shown.Add(($"{name} summary {what}", o.Summary));
                    var card = BattleOrchestrator.ResultCardFor(new BattleOrchestrator.Battle(battle), o);
                    Expect.Equal(!o.PlayerWon, card.Defeat, $"{name} {what}: card style from the outcome");
                    shown.Add(($"{name} result title {what}", card.Title));
                    shown.Add(($"{name} result detail {what}", card.Detail));
                }
            }

            // A reinforcement clash: title, subtitle and the result line over both clashes.
            var parent = ParentClash();
            var live = ReinforcementLive(7, 2, 2);
            AddNames(live);
            AddNames(parent.Planned);
            var plan = Mapper.BuildContinuation(live, parent, new MapperOptions());
            shown.Add(("continuation title", plan.Request.Presentation.Title));
            shown.Add(("continuation subtitle", plan.Request.Presentation.Subtitle));
            Expect.NotNull(plan.Request.Presentation.Texts, "continuation: module texts copied");
            var second = new Ck3Outcome { OutcomeCode = 1, PlayerWon = true, MyLost = 2000, EnemyLost = 3000, MyKills = 2, Clash = 2 };
            shown.Add(("continuation summary", OutcomeBuilder.SummaryLine(1, second, "Angers", new Ck3Outcome { MyLost = 500, EnemyLost = 700 })));
        }
        Expect.Equal(Loc.English, Loc.Language, "English restored after the scope");

        var namesLongestFirst = ck3Names.Where(n => n.Length > 2).OrderByDescending(n => n.Length).ToList();
        foreach (var (what, text) in shown)
        {
            string wording = namesLongestFirst.Aggregate(text, (t, n) => t.Replace(n, "<CK3>", StringComparison.Ordinal));
            foreach (var word in EnglishLeftovers)
                Expect.False(wording.Contains(word, StringComparison.Ordinal), $"{what}: '{word}' in \"{text}\" (bridge wording: \"{wording}\")");
        }
        Expect.True(shown.Any(s => s.Text.Contains(" contre ", StringComparison.Ordinal)), "a French subtitle (' contre ')");
        Expect.True(shown.Any(s => s.Text.Contains("Ost ", StringComparison.Ordinal)), "a French host name ('Ost ')");

        // Samples for the diag (IndianProbe3: presentation, result line, result card).
        foreach (var (what, text) in shown.Where(s => s.What.StartsWith("indian_probe3 ", StringComparison.Ordinal) || s.What.StartsWith("continuation", StringComparison.Ordinal)))
            Console.WriteLine($"      fr {what}: {text}");
    }
}

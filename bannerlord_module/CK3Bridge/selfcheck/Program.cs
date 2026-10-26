using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using CK3xBL.Protocol;
using Newtonsoft.Json.Linq;

namespace CK3Bridge.SelfCheck
{
    /// <summary>Headless checks of src/Core. Exit code 0 when every check passes.</summary>
    internal static class Program
    {
        private static int _passed;
        private static readonly List<string> Failures = new List<string>();

        private static int Main()
        {
            // Newtonsoft.Json is referenced with Private=false: load it from the Bannerlord install, as the game does.
            string blBin = Meta("BannerlordBin");
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                string path = Path.Combine(blBin, new AssemblyName(args.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            RunAll();
            Console.WriteLine($"{_passed} checks passed, {Failures.Count} failed");
            foreach (string failure in Failures) Console.WriteLine("FAIL " + failure);
            return Failures.Count == 0 ? 0 : 1;
        }

        // Kept out of Main so Newtonsoft types are only JIT-resolved after the resolver is installed.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunAll()
        {
            Run(nameof(ProtocolRequestParses), ProtocolRequestParses);
            Run(nameof(ResultKeepsGroupKeysVerbatim), ResultKeepsGroupKeysVerbatim);
            Run(nameof(ExtrasAreOptional), ExtrasAreOptional);
            Run(nameof(Commands), Commands);
            Run(nameof(TitleInEngineRouting), TitleInEngineRouting);
            Run(nameof(RequestIds), RequestIds);
            Run(nameof(NamesAndIds), NamesAndIds);
            Run(nameof(BannerCodes), BannerCodes);
            Run(nameof(NormalisationRules), NormalisationRules);
            Run(nameof(LedgerSpreadsSharedTroops), LedgerSpreadsSharedTroops);
            Run(nameof(AccountingCountsAndHeroes), AccountingCountsAndHeroes);
            Run(nameof(HeroKillsCountEnemiesOnly), HeroKillsCountEnemiesOnly);
            Run(nameof(BuffClamps), BuffClamps);
            Run(nameof(BuffHitProduct), BuffHitProduct);
            Run(nameof(BuffSpawnValues), BuffSpawnValues);
            Run(nameof(BuffFieldsAbsentAreNeutral), BuffFieldsAbsentAreNeutral);
            Run(nameof(BuffFieldsRoundTrip), BuffFieldsRoundTrip);
            Run(nameof(GroupBuffKeys), GroupBuffKeys);
            Run(nameof(CaptainOfParse), CaptainOfParse);
            Run(nameof(FileTransport), FileTransport);
            Run(nameof(CardWording), CardWording);
            Run(nameof(CardTextsFromPresentation), CardTextsFromPresentation);
            Run(nameof(ModuleTextsInResultFooter), ModuleTextsInResultFooter);
            Run(nameof(GlyphFoldingWithGameFont), GlyphFoldingWithGameFont);
            Run(nameof(PngHeaders), PngHeaders);
            Run(nameof(HostGateDecisions), HostGateDecisions);
            Run(nameof(GameVersionFile), GameVersionFile);
            Run(nameof(VersionsAgree), VersionsAgree);
            Run(nameof(GameBindings), () => GameBindings.Run(Meta("ModuleDir"), Meta("BannerlordBin"), (ok, what) => Check(ok, what, nameof(GameBindings))));
            Run(nameof(GuiResources), () => GuiResources.Run(Meta("ModuleDir"), Meta("BannerlordBin"), (ok, what) => Check(ok, what, nameof(GuiResources))));
        }

        private static string Meta(string key) =>
            typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == key).Value;

        private static void Run(string name, Action check)
        {
            try
            {
                check();
            }
            catch (Exception e)
            {
                Failures.Add($"{name}: threw {e.GetType().Name}: {e.Message}");
            }
        }

        private static void Check(bool condition, string what, [CallerMemberName] string caller = null)
        {
            if (condition) _passed++;
            else Failures.Add($"{caller}: {what}");
        }

        private static void Equal<T>(T expected, T actual, string what, [CallerMemberName] string caller = null) =>
            Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{what}: expected '{expected}', got '{actual}'", caller);

        /// <summary>Float equality up to rounding (products of several factors).</summary>
        private static void Near(float expected, float actual, string what, [CallerMemberName] string caller = null) =>
            Check(Math.Abs(expected - actual) < 1e-4f, $"{what}: expected {expected:R}, got {actual:R}", caller);

        // ------------------------------------------------------------------ checks

        /// <summary>The PROTOCOL.md §3.2 example (plus extension fields) deserializes into the shared DTOs.</summary>
        private static void ProtocolRequestParses()
        {
            const string json = @"{
              ""protocol"": 2, ""id"": ""b17_ab12"", ""kind"": ""field"", ""scene"": ""battle_terrain_029"",
              ""season"": ""summer"", ""timeOfDay"": 12, ""playerSide"": ""defender"", ""playerIsGeneral"": true,
              ""presentation"": { ""preload"": true, ""title"": ""The Battle of Orangallu"", ""returnCountdownSeconds"": 10 },
              ""scale"": { ""menPerAgent"": 6.2, ""battleSize"": 600 },
              ""attacker"": { ""name"": ""Raiders"", ""culture"": ""khuzait"", ""general"": ""e1"", ""heroes"": [], ""troops"": [] },
              ""defender"": {
                ""name"": ""Host of Gunda Kakatiya"", ""culture"": ""aserai"",
                ""banner"": { ""code"": ""11.110.110.1528.1528.764.764.0.0.0"", ""source"": ""title:c_orangallu"" },
                ""general"": ""h0"",
                ""heroes"": [ {
                  ""key"": ""h0"", ""ck3Id"": 10811, ""tag"": 3, ""role"": ""player"", ""isPlayer"": true, ""name"": ""Gunda"",
                  ""female"": false, ""age"": 71, ""level"": 30, ""formation"": ""Cavalry"", ""captain"": true, ""deathChance"": 0.1,
                  ""appearance"": { ""source"": ""meta_portrait"", ""weight"": 0.43, ""hair"": { ""index"": 0, ""tags"": [], ""bald"": true },
                                   ""beard"": { ""index"": null, ""tags"": [""Short""] }, ""morphs"": { ""face_width"": -0.12 }, ""bodyProperties"": null },
                  ""equipment"": { ""roster"": ""ase_king_template_bat_m"", ""overrides"": { ""Item4"": ""banner_of_the_knight_t3"" } },
                  ""skills"": { ""OneHanded"": 116, ""Riding"": 140 }, ""healthMultiplier"": 1.28 } ],
                ""troops"": [ { ""id"": ""aserai_mameluke_cavalry"", ""count"": 30, ""group"": ""maa:armored_footmen#16706"" } ] },
              ""location"": { ""atmosphere"": { ""atmospherePreset"": ""TOD_12_00_HeavyRain"", ""rain"": 0.9, ""fogDensity"": ""2"" },
                              ""battlefield"": { ""sceneLevels"": """" } },
              ""casualties"": { ""troopDeathChance"": 0.25, ""heroDeathChance"": 1.7 },
              ""someFutureField"": { ""x"": 1 }
            }";
            JObject root = ProtocolJson.ParseObject(json);
            BattleRequest r = ProtocolJson.ToObject<BattleRequest>(root);
            Equal(2, r.Protocol, "protocol");
            Equal("b17_ab12", r.Id, "id");
            Equal("defender", r.PlayerSide, "playerSide");
            Equal(600, r.Scale.BattleSize, "scale.battleSize");
            Equal(10, r.Presentation.ReturnCountdownSeconds, "returnCountdownSeconds");
            HeroSpec h = r.Defender.Heroes[0];
            Equal(10811L, h.Ck3Id, "ck3Id");
            Equal(true, h.IsPlayer, "isPlayer");
            Equal(-0.12f, h.Appearance.Morphs["face_width"], "morph");
            Equal(0, h.Appearance.Hair.Index ?? -1, "hair index");
            Equal(true, h.Appearance.Hair.Bald, "bald");
            Equal(false, h.Appearance.Beard.Index.HasValue, "beard index null");
            Equal("banner_of_the_knight_t3", h.Equipment.Overrides["Item4"], "override");
            Equal(140, h.Skills["Riding"], "skill");
            Equal("maa:armored_footmen#16706", r.Defender.Troops[0].Group, "troop group");

            RequestExtras extras = RequestExtras.From(root);
            Equal("TOD_12_00_HeavyRain", extras.AtmospherePreset, "atmosphere preset");
            Equal(0.9f, extras.Rain ?? -1f, "rain");
            Equal(false, extras.Snow.HasValue, "snow absent");
            Equal(2f, extras.FogDensity ?? -1f, "fog density from string");
            Equal(null, extras.SceneLevels, "empty sceneLevels is null");
            Equal(600, extras.BattleSize ?? -1, "scale.battleSize seen in the raw JSON");
            Equal(true, extras.Preload, "explicit preload: true holds the battle");
            Equal(0.25f, extras.TroopDeathChance, "troop death chance");
            Equal(1f, extras.HeroDeathChance, "hero death chance clamped");
            Equal(0.1f, extras.DeathChanceForHero("h0"), "per-hero death chance");
            Equal(1f, extras.DeathChanceForHero("e9"), "unknown hero uses the side default");
        }

        private static void ResultKeepsGroupKeysVerbatim()
        {
            var accounting = new BattleAccounting("b1");
            accounting.ExpectTroops(BattleSide.Attacker, "Levy#16777235", 10);
            string json = ProtocolJson.Serialize(accounting.Snapshot("finished", "attacker", false, 12.34f, null));
            Check(json.Contains("\"Levy#16777235\""), "group key keeps its case");
            Check(json.Contains("\"durationSeconds\": 12.3"), "member names are camelCase and duration is rounded");
            Check(json.Contains("\"playerRetreated\": false"), "bool member present");
            Check(!json.Contains("\"Requested\""), "no PascalCase members");
            BattleResult back = ProtocolJson.ToObject<BattleResult>(ProtocolJson.ParseObject(json));
            Equal(10, back.Attacker.Groups["Levy#16777235"].Requested, "round trip");

            string status = ProtocolJson.Serialize(new BannerlordStatus { Status = "idle", T = 1, Pid = 2, Hwnd = 3 });
            Check(status.Contains("\"status\": \"idle\"") && status.Contains("\"hwnd\": 3") && status.Contains("\"battleId\": null"),
                "status file shape");
        }

        private static void ExtrasAreOptional()
        {
            RequestExtras extras = RequestExtras.From(ProtocolJson.ParseObject("{\"id\":\"x\"}"));
            Equal(null, extras.AtmospherePreset, "no preset");
            Equal(false, extras.BattleSize.HasValue, "absent scale.battleSize keeps the user's option");
            Equal(RequestExtras.DefaultTroopDeathChance, extras.TroopDeathChance, "default troop chance");
            Equal(RequestExtras.DefaultHeroDeathChance, extras.DeathChanceForHero("h0"), "default hero chance");
            Equal(false, extras.Preload, "no presentation: not held (protocol 1 behaviour)");
            Equal(false, RequestExtras.From(ProtocolJson.ParseObject("{\"presentation\":{\"title\":\"T\"}}")).Preload, "presentation without preload: not held");
            Equal(false, RequestExtras.From(ProtocolJson.ParseObject("{\"presentation\":null}")).Preload, "presentation null: not held");
            Equal(false, RequestExtras.From(ProtocolJson.ParseObject("{\"presentation\":{\"preload\":false}}")).Preload, "preload false");
            Equal(true, RequestExtras.From(ProtocolJson.ParseObject("{\"presentation\":{\"preload\":\"true\"}}")).Preload, "preload as a string");
            BattleRequest r = ProtocolJson.ToObject<BattleRequest>(ProtocolJson.ParseObject("{\"id\":\"x\"}"));
            Equal(ProtocolInfo.Version, r.Protocol, "protocol defaults to 2 (protocol 1 requests are accepted)");
        }

        private static void Commands()
        {
            List<BridgeCommand> list = BridgeCommand.ParseAll("go\r\n\r\n  handoff_to 0x1A2B \nHANDOFF_TO 12345\nhandoff_to nope\nreturn_now\nfly");
            Equal(6, list.Count, "command count");
            Equal(BridgeCommandKind.Go, list[0].Kind, "go");
            Equal(BridgeCommandKind.HandoffTo, list[1].Kind, "hex handoff");
            Equal(0x1A2BL, list[1].Hwnd, "hex hwnd");
            Equal(12345L, list[2].Hwnd, "decimal hwnd, case-insensitive verb");
            Equal(BridgeCommandKind.Unknown, list[3].Kind, "handoff without a valid hwnd");
            Equal(BridgeCommandKind.ReturnNow, list[4].Kind, "return_now");
            Equal(BridgeCommandKind.Unknown, list[5].Kind, "unknown verb");
            Equal(BridgeCommandKind.Background, BridgeCommand.Parse("bg").Kind, "bg");
            Equal(BridgeCommandKind.Foreground, BridgeCommand.Parse("fg").Kind, "fg");
            Equal(0, BridgeCommand.ParseAll("").Count, "empty file");

            // "cancel|abort [<battleId>]" (PROTOCOL.md §8.3): a named command applies to that battle only.
            BridgeCommand bare = BridgeCommand.Parse("cancel");
            BridgeCommand named = BridgeCommand.Parse("cancel  b17_ab12 ");
            BridgeCommand abort = BridgeCommand.Parse("ABORT b18_cd34");
            Equal(BridgeCommandKind.Cancel, named.Kind, "cancel with an id");
            Equal("b17_ab12", named.BattleId, "cancel id");
            Equal(null, bare.BattleId, "bare cancel names no battle");
            Equal(BridgeCommandKind.Abort, abort.Kind, "abort with an id");
            Equal("b18_cd34", abort.BattleId, "abort id");
            Check(bare.AppliesTo("b17_ab12") && bare.AppliesTo(null), "bare cancel applies to any battle and to a request without id");
            Check(named.AppliesTo("b17_ab12"), "named cancel applies to its battle");
            Check(!named.AppliesTo("b18_cd34") && !named.AppliesTo("B17_AB12") && !named.AppliesTo(null), "named cancel leaves other battles alone");
            Equal(null, BridgeCommand.Parse("go b17").BattleId, "only cancel and abort carry an id");

            // PROTOCOL.md §13.6 fallback: the bridge could not show its own title card.
            Equal(BridgeCommandKind.TitleInEngine, BridgeCommand.Parse("title_in_engine").Kind, "title_in_engine");
            Equal(BridgeCommandKind.TitleInEngine, BridgeCommand.Parse(" TITLE_IN_ENGINE ").Kind, "title_in_engine, case-insensitive");
            Equal(BridgeCommandKind.Unknown, BridgeCommand.Parse("title_in_engines").Kind, "only the exact verb");
        }

        /// <summary>Where "title_in_engine" goes (PROTOCOL.md §13.6): the battle behind the loading card, or the waiting request once.</summary>
        private static void TitleInEngineRouting()
        {
            Check(TitleInEngineLatch.CanShow(BattlePhase.Loading, false, false) && TitleInEngineLatch.CanShow(BattlePhase.Held, false, false),
                "a loading or held battle can take the card");
            Check(!TitleInEngineLatch.CanShow(BattlePhase.Visible, false, false) && !TitleInEngineLatch.CanShow(BattlePhase.Fighting, false, false) &&
                  !TitleInEngineLatch.CanShow(BattlePhase.Black, false, false), "a visible or later battle cannot");
            Check(!TitleInEngineLatch.CanShow(BattlePhase.Held, true, false) && !TitleInEngineLatch.CanShow(BattlePhase.Loading, false, true),
                "a stopping or ended battle cannot");

            // The current battle takes it; request.json is not even read.
            var latch = new TitleInEngineLatch();
            bool read = false;
            Equal(TitleInEngineTarget.Current, latch.Receive("b1", true, () => { read = true; return "b2"; }), "held battle takes the card");
            Check(!read && latch.PendingFor == null, "nothing latched when the current battle takes it");

            // Same poll as its request (commands are read first): latched for that id, applied once.
            Equal(TitleInEngineTarget.Pending, latch.Receive(null, false, () => "b2"), "command before its request");
            Equal("b2", latch.PendingFor, "latched for the waiting request");
            Check(latch.TakeFor("b2"), "the waiting request gets it");
            Check(!latch.TakeFor("b2"), "only once");
            Check(!latch.TakeFor("b3"), "a later request gets nothing");

            // The previous battle is still ending: the next request is latched; another id is not applied.
            Equal(TitleInEngineTarget.Pending, latch.Receive("b1", false, () => "b4"), "previous battle ending, next request waiting");
            Check(!latch.TakeFor("b5"), "a different request is not applied");
            Check(!latch.TakeFor("b4"), "and the latch is cleared by any taken request");
            Equal(TitleInEngineTarget.Pending, latch.Receive(null, false, () => "b6"), "latched again");
            Check(!latch.TakeFor(null), "a request without an id is not applied");

            // Nothing to apply it to.
            Equal(TitleInEngineTarget.TooLate, latch.Receive("b1", false, () => null), "visible battle, no waiting request");
            Equal(TitleInEngineTarget.NoBattle, latch.Receive(null, false, () => null), "no battle, no request");
            Equal(TitleInEngineTarget.Pending, latch.Receive(null, false, () => "b7"), "latched");
            Equal(TitleInEngineTarget.NoBattle, latch.Receive(null, false, () => null), "request withdrawn");
            Check(!latch.TakeFor("b7"), "a withdrawn request's latch is dropped");
        }

        /// <summary>The id the drop rule compares: the request's own top-level id, not the first "id" in the text.</summary>
        private static void RequestIds()
        {
            const string troopsFirst = @"{ ""attacker"": { ""troops"": [ { ""id"": ""vlandian_recruit"", ""count"": 3 } ] }, ""id"": ""b17_ab12"" }";
            Equal("b17_ab12", ProtocolJson.ReadId(troopsFirst), "top-level id after nested ids");
            Equal(null, ProtocolJson.ReadId(@"{ ""attacker"": { ""troops"": [ { ""id"": ""vlandian_recruit"" } ] } }"), "no top-level id");
            Equal(null, ProtocolJson.ReadId(@"{ ""id"": 17 }"), "non-string id");
            Equal("b9", ProtocolJson.ReadId(@"{ ""id"": ""b9"", ""broken"": "), "broken JSON falls back to the first id string");
            Equal(null, ProtocolJson.ReadId(""), "empty text");
        }

        private static void NamesAndIds()
        {
            Equal("ck3xbl_b17_ab_12_h_0", Naming.HeroObjectId("b17.ab-12", "h.0"), "hero id has no dots");
            Equal("Gunda of Orangallu", Naming.SanitizeDisplayName("{=abc}Gunda.  of\nOrangallu ", "x"), "markup, dots, whitespace");
            Equal("fallback", Naming.SanitizeDisplayName(" {}. ", "fallback"), "nothing printable");
            Equal(Naming.MaxDisplayNameLength, Naming.SanitizeDisplayName(new string('a', 200), "x").Length, "length cap");
            var names = new UniqueNames();
            Equal("Sir Nachana", names.Claim("Sir Nachana"), "first claim");
            Equal("sir nachana (2)", names.Claim("sir nachana"), "case-insensitive duplicate keeps its own spelling");
            Equal("Sir Nachana (3)", names.Claim("Sir Nachana"), "third claim");
        }

        private static void BannerCodes()
        {
            Check(BannerCode.TryParse("11.110.110.1528.1528.764.764.0.0.0.400.149.149.400.400.764.764.0.0.0", out var layers, out _), "two layers parse");
            Equal(2, layers.Count, "layer count");
            Equal(400, layers[1].Mesh, "icon mesh");
            Equal(149, layers[1].Color, "icon colour");
            Check(!BannerCode.TryParse("11.110.110.1528.1528", out _, out _), "token count not a multiple of 10");
            Check(!BannerCode.TryParse("11.110.110.1528.1528.764.764.0.0.x", out _, out _), "non-integer token");
            Check(!BannerCode.TryParse("", out _, out _), "empty");
            string tooMany = string.Join(".", Enumerable.Repeat("1.1.1.1.1.1.1.0.0.0", 34));
            Check(!BannerCode.TryParse(tooMany, out _, out string error) && error.Contains("34"), "34 layers rejected");
        }

        private static void NormalisationRules()
        {
            Equal("fall", RequestRules.Season("Autumn"), "autumn");
            Equal("summer", RequestRules.Season(null), "null season");
            Equal("winter", RequestRules.Season("winter"), "winter");
            Equal(12, RequestRules.PresetHour(13f), "13h");
            Equal(15, RequestRules.PresetHour(14f), "14h");
            Equal(22, RequestRules.PresetHour(23.5f), "23.5h");
            Equal(22, RequestRules.PresetHour(-1f), "-1h wraps to 23h");
            Equal(6, RequestRules.PresetHour(9f), "tie goes to the earlier preset");
            Equal(3, RequestRules.BattleSizeIndex(500), "500 agents");
            Equal(3, RequestRules.BattleSizeIndex(450), "tie goes to the larger size");
            Equal(6, RequestRules.BattleSizeIndex(5000), "above max");
            Equal(0, RequestRules.BattleSizeIndex(1), "below min");
            Equal(5, RequestRules.BattleSizeIndex(750), "750 agents");
            Equal(false, RequestRules.IsPlayerAttacker("Defender"), "defender side");
            Equal(true, RequestRules.IsPlayerAttacker(null), "default side");
            Equal(0.0, RequestRules.ColorDistance(0xFF123456, 0xFF123456), "same colour");
            Check(RequestRules.ColorDistance(0xFF000000, 0xFFFFFFFF) > 700, "black vs white");
            Equal(0.5f, RequestRules.Probability(float.NaN, 0.5f), "NaN probability");
        }

        private static void LedgerSpreadsSharedTroops()
        {
            var ledger = new GroupLedger<(int, string)>();
            ledger.Add((1, "recruit"), "levy#1", 2);
            ledger.Add((1, "recruit"), "levy#2", 1);
            ledger.Add((0, "recruit"), "levy#9", 1);
            ledger.Add((1, "general"), null, 1);
            var taken = new List<string>();
            while (ledger.TryTake((1, "recruit"), out string g)) taken.Add(g);
            Equal("levy#1,levy#2,levy#1", string.Join(",", taken), "least-filled reservation first, ties in request order");
            Check(ledger.TryTake((0, "recruit"), out string other) && other == "levy#9", "sides are separate");
            Check(ledger.TryTake((1, "general"), out string none) && none == null, "ungrouped slot");
            Check(!ledger.TryTake((1, "general"), out _), "a filled reservation is not taken again");
            Check(!ledger.TryTake((1, "knight"), out _), "unrequested troop");

            // Two armies' levies of one culture, battle size 200 (100 agents per side): the first wave is split evenly.
            var even = new GroupLedger<string>();
            even.Add("vlandian_recruit", "levy#1", 120);
            even.Add("vlandian_recruit", "levy#2", 120);
            Equal("50/50", Spread(even, "vlandian_recruit", 100, "levy#1", "levy#2"), "equal groups share the first 100 spawns");

            // Unequal groups (90 + 30): spawns follow the 3:1 ratio, and every reserved man is eventually handed out.
            var ratio = new GroupLedger<string>();
            ratio.Add("t", "maa#1", 90);
            ratio.Add("t", "maa#2", 30);
            Equal("30/10", Spread(ratio, "t", 40, "maa#1", "maa#2"), "40 spawns of 90 + 30");
            Equal("60/20", Spread(ratio, "t", 80, "maa#1", "maa#2"), "the remaining 80 complete both groups");
            Check(!ratio.TryTake("t", out _), "nothing left after 120");
        }

        /// <summary>Takes <paramref name="count"/> agents and returns "a/b", how many went to each group.</summary>
        private static string Spread(GroupLedger<string> ledger, string key, int count, string a, string b)
        {
            int toA = 0, toB = 0;
            for (int i = 0; i < count; i++)
            {
                if (!ledger.TryTake(key, out string group)) break;
                if (group == a) toA++;
                else if (group == b) toB++;
            }
            return toA + "/" + toB;
        }

        private static void AccountingCountsAndHeroes()
        {
            var a = new BattleAccounting("b2");
            a.ExpectHero(BattleSide.Defender, new HeroSpec { Key = "h0", Ck3Id = 10811, Tag = 3 });
            a.ExpectHero(BattleSide.Attacker, new HeroSpec { Key = "e1", Ck3Id = 500, Tag = 4 });
            a.ExpectHero(BattleSide.Attacker, new HeroSpec { Key = "e2", Ck3Id = 501, Tag = 5 });
            a.ExpectTroops(BattleSide.Attacker, "levy#1", 3);
            a.ExpectTroops(BattleSide.Attacker, "levy#1", 2);   // same regiment split over two tiers
            a.ExpectUngrouped(BattleSide.Defender);

            a.OnSpawned(BattleSide.Defender, null, "h0");
            a.OnSpawned(BattleSide.Defender, null, null);
            a.OnSpawned(BattleSide.Attacker, null, "e1");
            for (int i = 0; i < 4; i++) a.OnSpawned(BattleSide.Attacker, "levy#1", null);
            BattleResult provisional = a.Snapshot("result", "defender", false, 1f, null);

            a.OnRemoved(BattleSide.Attacker, "levy#1", null, AgentFate.Killed, "h0");
            a.OnRemoved(BattleSide.Attacker, "levy#1", null, AgentFate.Wounded, "h0");
            a.OnRemoved(BattleSide.Attacker, "levy#1", null, AgentFate.Routed, null);
            a.OnRemoved(BattleSide.Attacker, null, "e1", AgentFate.Killed, "h0");
            BattleResult r = a.Snapshot("finished", "defender", false, 2f, null);

            SideResult att = r.Attacker;
            Equal(7, att.Requested, "attacker requested = 5 troops + 2 heroes");
            Equal(5, att.Spawned, "attacker spawned");
            Equal(2, att.Killed, "attacker killed");
            Equal(1, att.Wounded, "attacker wounded");
            Equal(1, att.Routed, "attacker routed");
            Equal(5, att.Groups["levy#1"].Requested, "group requested sums both troop entries");
            Equal(4, att.Groups["levy#1"].Spawned, "group spawned");
            Equal(1, att.Groups["levy#1"].Killed, "group killed (hero excluded)");
            Equal(2, r.Defender.Requested, "defender requested = hero + stock general");
            HeroResult h0 = r.Heroes.Single(h => h.Key == "h0");
            HeroResult e1 = r.Heroes.Single(h => h.Key == "e1");
            HeroResult e2 = r.Heroes.Single(h => h.Key == "e2");
            Equal("alive", h0.State, "player alive");
            Equal(3, h0.Kills, "kills count killed + wounded, not routed");
            Equal("killed", e1.State, "enemy hero killed");
            Equal("h0", e1.KilledBy, "killer key");
            Equal("unspawned", e2.State, "never spawned");
            Equal(500L, e1.Ck3Id, "ck3 id echoed");
            Equal(0, provisional.Attacker.Killed, "earlier snapshot is not mutated");
            Equal("alive", provisional.Heroes.Single(h => h.Key == "e1").State, "earlier hero snapshot is not mutated");

            Equal("attacker", BattleAccounting.WinnerFrom("AttackerVictory", false, true, false), "attacker victory");
            Equal("attacker", BattleAccounting.WinnerFrom("DefenderPullBack", false, false, true), "siege pull back");
            Equal("defender", BattleAccounting.WinnerFrom("DefenderVictory", true, false, true), "defender victory");
            Equal("attacker", BattleAccounting.WinnerFrom("None", true, false, true), "fallback to player victory");
            Equal("attacker", BattleAccounting.WinnerFrom("None", false, true, false), "fallback to player defeat");
            Equal("none", BattleAccounting.WinnerFrom("None", false, false, true), "undecided");
        }

        /// <summary>Kills earn CK3 prestige and glory, so a hero is credited for enemies only.</summary>
        private static void HeroKillsCountEnemiesOnly()
        {
            var a = new BattleAccounting("b3");
            a.ExpectHero(BattleSide.Attacker, new HeroSpec { Key = "k1", Ck3Id = 1, Tag = 1 });
            a.ExpectHero(BattleSide.Attacker, new HeroSpec { Key = "k2", Ck3Id = 2, Tag = 2 });
            a.ExpectHero(BattleSide.Defender, new HeroSpec { Key = "e1", Ck3Id = 3, Tag = 3 });
            a.ExpectTroops(BattleSide.Attacker, "levy#1", 2);
            a.ExpectTroops(BattleSide.Defender, "levy#2", 2);
            foreach (string key in new[] { "k1", "k2" }) a.OnSpawned(BattleSide.Attacker, null, key);
            a.OnSpawned(BattleSide.Defender, null, "e1");
            for (int i = 0; i < 2; i++)
            {
                a.OnSpawned(BattleSide.Attacker, "levy#1", null);
                a.OnSpawned(BattleSide.Defender, "levy#2", null);
            }

            a.OnRemoved(BattleSide.Defender, "levy#2", null, AgentFate.Killed, "k1");   // an enemy troop
            a.OnRemoved(BattleSide.Attacker, "levy#1", null, AgentFate.Killed, "k1");   // his own man, trampled
            a.OnRemoved(BattleSide.Attacker, null, "k2", AgentFate.Wounded, "k1");      // a friendly knight, struck by mistake
            a.OnRemoved(BattleSide.Defender, null, "e1", AgentFate.Killed, "k1");       // the enemy knight
            BattleResult r = a.Snapshot("finished", "attacker", false, 1f, null);

            HeroResult k1 = r.Heroes.Single(h => h.Key == "k1");
            HeroResult k2 = r.Heroes.Single(h => h.Key == "k2");
            Equal(2, k1.Kills, "an enemy troop and an enemy hero count; his own man and a friendly hero do not");
            Equal("wounded", k2.State, "the friendly hero is down");
            Equal("k1", k2.KilledBy, "killedBy still names the friend who downed him");
            Equal(1, r.Attacker.Killed, "own losses are still counted for the side");
            Equal(1, r.Attacker.Groups["levy#1"].Killed, "and for the group");
        }

        /// <summary>The module's safety clamps (PROTOCOL.md §13.4 item 7): wide ranges, NaN and infinities neutral.</summary>
        private static void BuffClamps()
        {
            Equal(4f, BuffMath.Mult(9f), "mult upper clamp");
            Equal(0.25f, BuffMath.Mult(0f), "mult lower clamp");
            Equal(1.3f, BuffMath.Mult(1.3f), "mult inside the range");
            Equal(1f, BuffMath.Mult(float.NaN), "NaN mult is neutral");
            Equal(1f, BuffMath.Mult(float.PositiveInfinity), "infinite mult is neutral");
            Equal(-50f, BuffMath.MoraleAdd(-80f), "morale add lower clamp");
            Equal(0f, BuffMath.MoraleAdd(float.NegativeInfinity), "infinite morale add is neutral");
            Equal(3f, BuffMath.HeroDamageMultiplier(5f), "hero damageMultiplier upper clamp");
            Equal(0.25f, BuffMath.HeroDamageMultiplier(-1f), "hero damageMultiplier lower clamp");
            Equal(1f, BuffMath.HeroDamageMultiplier(float.NaN), "NaN damageMultiplier is neutral");

            var side = new SideBuffs { DamageDealtMult = 7f, DamageTakenMult = float.NaN, InitialMoraleAdd = 99f, ChargeDamageMult = 0.1f, TroopDeathChanceMult = 2f };
            SideBuffs s = BuffMath.Sanitize(side);
            Equal(4f, s.DamageDealtMult, "side dealt clamped");
            Equal(1f, s.DamageTakenMult, "side taken NaN neutral");
            Equal(50f, s.InitialMoraleAdd, "side morale clamped");
            Equal(0.25f, s.ChargeDamageMult, "side charge clamped");
            Equal(2f, s.TroopDeathChanceMult, "side troop death mult kept");
            Equal(7f, side.DamageDealtMult, "the request object itself is left as read");

            GroupBuffs g = BuffMath.Sanitize(new GroupBuffs { DamageDealtMult = 1.25f, DamageTakenMult = 5f, HealthMult = float.NegativeInfinity, InitialMoraleAdd = -60f });
            Equal(1.25f, g.DamageDealtMult, "group dealt kept");
            Equal(4f, g.DamageTakenMult, "group taken clamped");
            Equal(1f, g.HealthMult, "group health -inf neutral");
            Equal(-50f, g.InitialMoraleAdd, "group morale clamped");

            HeroBuffs h = BuffMath.Sanitize(new HeroBuffs
            {
                DamageDealtMeleeMult = 1.1f, DamageDealtRangedMult = 0f, DamageDealtVsHeroMult = float.NaN, DamageTakenMult = 0.1f,
                ChargeDamageMult = 10f, InitialMoraleAdd = 25f, NoPanic = true, StartHealthFraction = 0f, SpeedMult = float.PositiveInfinity,
                SwingSpeedMult = 1.05f, RangedInaccuracyMult = 6f, KnockdownResistanceAdd = 2f,
            });
            Equal(1.1f, h.DamageDealtMeleeMult, "hero melee kept");
            Equal(0.25f, h.DamageDealtRangedMult, "hero ranged clamped");
            Equal(1f, h.DamageDealtVsHeroMult, "hero vs-hero NaN neutral");
            Equal(0.25f, h.DamageTakenMult, "hero taken clamped");
            Equal(4f, h.ChargeDamageMult, "hero charge clamped");
            Equal(25f, h.InitialMoraleAdd, "hero morale kept");
            Equal(true, h.NoPanic, "noPanic kept");
            Equal(0.05f, h.StartHealthFraction, "start health fraction lower clamp");
            Equal(1f, h.SpeedMult, "hero speed +inf neutral");
            Equal(1.05f, h.SwingSpeedMult, "hero swing kept");
            Equal(4f, h.RangedInaccuracyMult, "hero inaccuracy clamped");
            Equal(0.5f, h.KnockdownResistanceAdd, "knockdown add clamped");
            Equal(1f, BuffMath.Sanitize(new HeroBuffs { StartHealthFraction = 3f }).StartHealthFraction, "start health fraction upper clamp");

            Check(BuffMath.Sanitize((SideBuffs)null) == null && BuffMath.Sanitize((GroupBuffs)null) == null && BuffMath.Sanitize((HeroBuffs)null) == null,
                "null buffs stay null");
        }

        /// <summary>The per-hit product of PROTOCOL.md §13.4 item 4 and its 0.25..3 clamp.</summary>
        private static void BuffHitProduct()
        {
            var side = new SideBuffs { DamageDealtMult = 1.2f, ChargeDamageMult = 1.1f, DamageTakenMult = 0.9f };
            var knightBuffs = new HeroBuffs { DamageDealtMeleeMult = 1.1f, DamageDealtRangedMult = 1.3f, DamageDealtVsHeroMult = 1.5f, ChargeDamageMult = 1.2f, DamageTakenMult = 0.8f };
            var knight = new AgentBuffs(side, 1.25f, knightBuffs, null);
            var retinue = new AgentBuffs(side, 1f, null, new GroupBuffs { DamageDealtMult = 1.25f, DamageTakenMult = 0.9f });
            var enemy = new AgentBuffs(new SideBuffs { DamageTakenMult = 1.1f }, 1f, null, new GroupBuffs { DamageTakenMult = 0.8f });

            Near(1.2f * 1.25f * 1.1f * 1.1f * 0.8f, BuffMath.HitMultiplier(knight, enemy, missile: false, charge: false, victimIsHero: false), "knight on foot, melee on a troop");
            Near(1.2f * 1.25f * 1.3f * 1.1f * 0.8f, BuffMath.HitMultiplier(knight, enemy, missile: true, charge: false, victimIsHero: false), "knight's missile on a troop");
            Near(1.2f * 1.25f * 1.1f * 1.5f * 0.9f * 0.8f, BuffMath.HitMultiplier(knight, knight, missile: false, charge: false, victimIsHero: true),
                "melee against a hero: vs-hero factor, his side's and his own taken");
            Near(1.2f * 1.25f * 1.3f * 0.9f * 0.8f, BuffMath.HitMultiplier(knight, knight, missile: true, charge: false, victimIsHero: true), "missiles get no vs-hero factor");
            Near(1.2f * 1.1f * 1.25f * 1.1f * 1.2f * 1.1f * 0.8f, BuffMath.HitMultiplier(knight, enemy, missile: false, charge: true, victimIsHero: false),
                "a charge adds the side's and the hero's charge factors");
            Near(1.2f * 1.25f * 0.9f * 0.9f, BuffMath.HitMultiplier(retinue, retinue, missile: false, charge: false, victimIsHero: false), "group dealt and taken");
            Equal(1f, BuffMath.HitMultiplier(default(AgentBuffs), default(AgentBuffs), missile: false, charge: true, victimIsHero: true), "unbuffed agents: 1");

            var huge = new AgentBuffs(new SideBuffs { DamageDealtMult = 4f }, 3f, null, null);
            var tiny = new AgentBuffs(new SideBuffs { DamageTakenMult = 0.25f }, 1f, new HeroBuffs { DamageTakenMult = 0.25f }, null);
            Equal(BuffMath.MaxHitMultiplier, BuffMath.HitMultiplier(huge, default(AgentBuffs), false, false, false), "product clamped to 3");
            Equal(BuffMath.MinHitMultiplier, BuffMath.HitMultiplier(default(AgentBuffs), tiny, false, false, true), "product clamped to 0.25");
        }

        /// <summary>Spawn-time values: initial morale, troop death chance, resistances, a wounded hero's start health.</summary>
        private static void BuffSpawnValues()
        {
            var brave = new AgentBuffs(new SideBuffs { InitialMoraleAdd = 8f }, 1f, new HeroBuffs { InitialMoraleAdd = 25f }, null);
            Equal(73f, BuffMath.InitialMorale(40f, brave), "side + hero morale added");
            Equal(40f, BuffMath.InitialMorale(40f, default(AgentBuffs)), "unbuffed morale unchanged");

            Near(0.6f, BuffMath.TroopDeathChance(0.3f, new SideBuffs { TroopDeathChanceMult = 2f }), "troop death chance scaled");
            Equal(1f, BuffMath.TroopDeathChance(0.3f, new SideBuffs { TroopDeathChanceMult = 4f }), "troop death chance clamped to 1");
            Equal(0.3f, BuffMath.TroopDeathChance(0.3f, null), "no side buffs: unchanged");

            Near(0.9f, BuffMath.Resistance(0.4f, 0.5f), "resistance raised");
            Equal(1f, BuffMath.Resistance(0.7f, 0.5f), "resistance capped at 1");
            Equal(0f, BuffMath.Resistance(0.2f, -0.5f), "resistance floored at 0");

            Check(BuffMath.TryStartHealth(200f, new HeroBuffs { StartHealthFraction = 0.5f }, out float half) && half == 100f, "a wounded_3 hero starts at half his pool");
            Check(BuffMath.TryStartHealth(10f, new HeroBuffs { StartHealthFraction = 0.05f }, out float least) && least == 1f, "never below 1 health");
            Check(!BuffMath.TryStartHealth(200f, new HeroBuffs(), out _), "full health by default");
            Check(!BuffMath.TryStartHealth(200f, null, out _), "no hero buffs: full health");
        }

        /// <summary>A request without the §13.4 members (an older bridge, a hand-dropped file) is today's battle.</summary>
        private static void BuffFieldsAbsentAreNeutral()
        {
            const string json = @"{ ""protocol"": 2, ""id"": ""b5"", ""presentation"": { ""title"": ""T"" },
              ""attacker"": { ""heroes"": [ { ""key"": ""h0"", ""healthMultiplier"": 1.5 } ], ""troops"": [ { ""id"": ""t"", ""count"": 5, ""group"": ""levy#1"" } ] },
              ""defender"": { ""heroes"": [], ""troops"": [] } }";
            BattleRequest r = ProtocolJson.ToObject<BattleRequest>(ProtocolJson.ParseObject(json));
            HeroSpec h = r.Attacker.Heroes[0];
            Equal(true, r.Presentation.InEngineTitle, "inEngineTitle defaults to true");
            Check(r.Attacker.Buffs == null && r.Defender.Buffs == null, "no side buffs");
            Check(r.Attacker.GroupBuffs != null && r.Attacker.GroupBuffs.Count == 0, "no group buffs");
            Equal(1f, h.DamageMultiplier, "damageMultiplier defaults to 1");
            Equal(null, h.CaptainOf, "no captainOf");
            Equal(false, h.DeathChance.HasValue, "no deathChance");
            Check(h.Buffs == null, "no hero buffs");

            SideBuffs side = BuffMath.Sanitize(r.Attacker.Buffs);
            var hero = new AgentBuffs(side, BuffMath.HeroDamageMultiplier(h.DamageMultiplier), BuffMath.Sanitize(h.Buffs), null);
            Dictionary<string, GroupBuffs> groups = BuffMath.GroupBuffsOf(r.Attacker, null);
            var troop = new AgentBuffs(side, 1f, null, groups.TryGetValue("levy#1", out GroupBuffs levy) ? levy : null);
            var agents = new[] { hero, troop, default(AgentBuffs) };
            bool allNeutral = true;
            foreach (AgentBuffs attacker in agents)
                foreach (AgentBuffs victim in agents)
                    for (int flags = 0; flags < 8; flags++)
                        allNeutral &= BuffMath.HitMultiplier(attacker, victim, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0) == 1f;
            Check(allNeutral, "every hit between hero, troop and stock agents is unchanged");
            Equal(52f, BuffMath.InitialMorale(52f, hero), "initial morale unchanged");
            Equal(RequestExtras.DefaultTroopDeathChance, BuffMath.TroopDeathChance(RequestExtras.DefaultTroopDeathChance, side), "troop death chance unchanged");
            Check(!BuffMath.TryStartHealth(150f, hero.Hero, out _), "the hero starts at full health");
            Check(!BuffMath.TryParseCaptainOf(h.CaptainOf, out _), "stock captains");
        }

        /// <summary>The PROTOCOL.md §13.4 example reads into the shared DTOs and survives the module's JSON settings unchanged.</summary>
        private static void BuffFieldsRoundTrip()
        {
            const string json = @"{ ""protocol"": 2, ""id"": ""b6"", ""presentation"": { ""inEngineTitle"": false },
              ""attacker"": {
                ""buffs"": { ""damageDealtMult"": 1.2, ""damageTakenMult"": 1.0, ""initialMoraleAdd"": 8, ""chargeDamageMult"": 1.1, ""troopDeathChanceMult"": 1.0 },
                ""groupBuffs"": {
                  ""knight-retinue#5"": { ""damageDealtMult"": 1.25, ""damageTakenMult"": 1.0, ""healthMult"": 1.25, ""initialMoraleAdd"": 0 },
                  ""maa:bowmen#12#16706"": { ""damageDealtMult"": 1.2, ""damageTakenMult"": 1.0, ""healthMult"": 1.1, ""initialMoraleAdd"": 6 } },
                ""heroes"": [ { ""key"": ""h1"", ""healthMultiplier"": 2.4, ""damageMultiplier"": 1.25, ""captainOf"": ""Ranged"", ""deathChance"": 0.6,
                  ""buffs"": { ""damageDealtMeleeMult"": 1.1, ""damageDealtRangedMult"": 1.0, ""damageDealtVsHeroMult"": 1.0, ""damageTakenMult"": 1.05,
                               ""chargeDamageMult"": 1.0, ""initialMoraleAdd"": 25, ""noPanic"": true, ""startHealthFraction"": 1.0,
                               ""speedMult"": 1.0, ""swingSpeedMult"": 1.0, ""rangedInaccuracyMult"": 1.0, ""knockdownResistanceAdd"": 0.0 } } ],
                ""troops"": [ { ""id"": ""t"", ""count"": 9, ""group"": ""maa:bowmen#12#16706"" } ] } }";
            JObject root = ProtocolJson.ParseObject(json);
            BattleRequest r = ProtocolJson.ToObject<BattleRequest>(root);
            string first = ProtocolJson.Serialize(r);
            BattleRequest back = ProtocolJson.ToObject<BattleRequest>(ProtocolJson.ParseObject(first));
            Equal(first, ProtocolJson.Serialize(back), "serialise -> read -> serialise is stable");
            foreach (BattleRequest x in new[] { r, back })
            {
                HeroSpec h = x.Attacker.Heroes[0];
                Equal(false, x.Presentation.InEngineTitle, "inEngineTitle");
                Equal(1.2f, x.Attacker.Buffs.DamageDealtMult, "side dealt");
                Equal(8f, x.Attacker.Buffs.InitialMoraleAdd, "side morale");
                Equal(1.1f, x.Attacker.Buffs.ChargeDamageMult, "side charge");
                Equal(1.25f, x.Attacker.GroupBuffs["knight-retinue#5"].HealthMult, "retinue health");
                Equal(6f, x.Attacker.GroupBuffs["maa:bowmen#12#16706"].InitialMoraleAdd, "group key kept verbatim");
                Equal(1.25f, h.DamageMultiplier, "damageMultiplier");
                Equal("Ranged", h.CaptainOf, "captainOf");
                Equal(0.6f, h.DeathChance ?? -1f, "deathChance");
                Equal(true, h.Buffs.NoPanic, "noPanic");
                Equal(25f, h.Buffs.InitialMoraleAdd, "hero morale");
                Equal(1.05f, h.Buffs.DamageTakenMult, "hero taken");
            }
            Check(first.Contains("\"knight-retinue#5\"") && first.Contains("\"inEngineTitle\": false") && first.Contains("\"captainOf\": \"Ranged\""),
                "member names camelCase, dictionary keys verbatim");
            Equal(0.6f, RequestExtras.From(root).DeathChanceForHero("h1"), "the raw deathChance read of older module stages agrees");

            // Newtonsoft reads NaN and Infinity literals; the clamps make them neutral.
            BattleRequest odd = ProtocolJson.ToObject<BattleRequest>(ProtocolJson.ParseObject(
                @"{ ""id"": ""b7"", ""attacker"": { ""buffs"": { ""damageDealtMult"": NaN, ""initialMoraleAdd"": -Infinity }, ""groupBuffs"": null,
                    ""heroes"": [ { ""key"": ""h"", ""damageMultiplier"": Infinity } ] } }"));
            Equal(1f, BuffMath.Sanitize(odd.Attacker.Buffs).DamageDealtMult, "NaN from JSON is neutral");
            Equal(0f, BuffMath.Sanitize(odd.Attacker.Buffs).InitialMoraleAdd, "-Infinity from JSON is neutral");
            Equal(1f, BuffMath.HeroDamageMultiplier(odd.Attacker.Heroes[0].DamageMultiplier), "Infinity damageMultiplier is neutral");
            Equal(0, BuffMath.GroupBuffsOf(odd.Attacker, null).Count, "groupBuffs: null is empty");
        }

        /// <summary>groupBuffs keys must name a requested group (the ledger's key: the group, else the troop id).</summary>
        private static void GroupBuffKeys()
        {
            var side = new SideRequest
            {
                Troops = new List<TroopRequest>
                {
                    new TroopRequest { Id = "vlandian_recruit", Count = 10, Group = "Levy#1" },
                    new TroopRequest { Id = "vlandian_crossbowman", Count = 5, Group = "" },
                    null,
                },
                GroupBuffs = new Dictionary<string, GroupBuffs>
                {
                    { "Levy#1", new GroupBuffs { HealthMult = 9f } },
                    { "vlandian_crossbowman", new GroupBuffs { DamageDealtMult = 1.2f } },
                    { "levy#1", new GroupBuffs() },
                    { "maa:gone#3", new GroupBuffs() },
                    { "knight-retinue#4", null },
                },
            };
            var unknown = new List<string>();
            Dictionary<string, GroupBuffs> groups = BuffMath.GroupBuffsOf(side, unknown.Add);
            Equal(2, groups.Count, "two keys match requested groups");
            Equal(4f, groups["Levy#1"].HealthMult, "kept verbatim and clamped");
            Equal(1.2f, groups["vlandian_crossbowman"].DamageDealtMult, "an empty group falls back to the troop id");
            Equal("levy#1,maa:gone#3", string.Join(",", unknown), "keys are case-sensitive, unknown keys are reported, a null entry is skipped");
            Equal(0, BuffMath.GroupBuffsOf(new SideRequest { GroupBuffs = null }, unknown.Add).Count, "null dictionary");
            Equal(0, BuffMath.GroupBuffsOf(null, unknown.Add).Count, "no side");
            Equal("Levy#1", RequestRules.GroupKey(side.Troops[0]), "group key");
            Equal("vlandian_crossbowman", RequestRules.GroupKey(side.Troops[1]), "troop id when the group is empty");
        }

        /// <summary>heroes[].captainOf: the four field formation classes, case-insensitive; anything else is no captaincy.</summary>
        private static void CaptainOfParse()
        {
            Check(BuffMath.TryParseCaptainOf("ranged", out CaptainFormation lower) && lower == CaptainFormation.Ranged, "\"ranged\"");
            Check(BuffMath.TryParseCaptainOf("Ranged", out CaptainFormation exact) && exact == CaptainFormation.Ranged, "\"Ranged\"");
            Check(BuffMath.TryParseCaptainOf(" HorseArcher ", out CaptainFormation ha) && ha == CaptainFormation.HorseArcher, "\" HorseArcher \"");
            Check(BuffMath.TryParseCaptainOf("INFANTRY", out CaptainFormation inf) && inf == CaptainFormation.Infantry, "\"INFANTRY\"");
            Check(BuffMath.TryParseCaptainOf("cavalry", out CaptainFormation cav) && cav == CaptainFormation.Cavalry, "\"cavalry\"");
            foreach (string bad in new[] { "General", "Bodyguard", "x", "", "  ", null, "1", "Skirmisher", "Ranged,Cavalry" })
                Check(!BuffMath.TryParseCaptainOf(bad, out _), $"\"{bad ?? "null"}\" is no captaincy");
        }

        private static void CardWording()
        {
            Equal("The Battle of St. Albans", CardText.Clean("  The Battle of {=x}St. Albans\r\n", 96), "markup and whitespace removed, dots kept");
            Equal("", CardText.Clean(null, 96), "null");
            Equal("a b", CardText.Clean("a\tb\u0007", 96), "control characters");
            string cut = CardText.Clean("The Battle of the Very Long Name of a Place Far Away", 24);
            Check(cut.Length <= 24 && cut.EndsWith("…", StringComparison.Ordinal) && !cut.Contains("{"), "long text is cut with an ellipsis: " + cut);
            Equal("The Battle of Orangallu is won", CardText.Outcome("The Battle of Orangallu", true, false), "won");
            Equal("The Battle of Orangallu is lost", CardText.Outcome("The Battle of Orangallu", false, true), "lost");
            Equal("The battle is over", CardText.Outcome("", false, false), "undecided, no title");
            Equal("The battle is won", CardText.Outcome("Reinforcements arrive!", true, false), "second clash (exclamation title)");
            Equal("The battle is lost", CardText.Outcome("Will the line hold?", false, true), "question title");
            Equal(10, CardText.SecondsShown(9.2f), "rounded up");
            Equal(1, CardText.SecondsShown(0.01f), "last second");
            Equal(0, CardText.SecondsShown(-3f), "never negative");
            Equal("X is won  ·  Returning to your realm in 7  ·  [Enter] now  ·  [Space] stay on the field",
                CardText.CountdownFooter("X is won", 7), "countdown footer");
            Equal("X is lost  ·  [Enter] Return to your realm", CardText.WaitFooter("X is lost"), "wait footer");
            Equal("Defeat the enemy host", CardText.DefaultObjective(""), "objective without a name");
        }

        /// <summary>presentation.texts (PROTOCOL.md §3.2): the result footer in the player's language, English without it.</summary>
        private static void ModuleTextsInResultFooter()
        {
            // The request: the field absent (an older bridge), null (an English request), or set (another language).
            const string head = @"{ ""protocol"": 2, ""id"": ""b1_t"", ""presentation"": { ""title"": ""X""";
            Check(ProtocolJson.ToObject<BattleRequest>(ProtocolJson.ParseObject(head + " } }")).Presentation.Texts == null, "texts absent: null");
            Check(ProtocolJson.ToObject<BattleRequest>(ProtocolJson.ParseObject(head + @", ""texts"": null } }")).Presentation.Texts == null, "texts null: null");
            var sent = ProtocolJson.ToObject<BattleRequest>(ProtocolJson.ParseObject(head + @", ""texts"": { ""outcomeWon"": ""{title} est gagnée"" } } }"));
            Equal("{title} est gagnée", sent.Presentation.Texts?.OutcomeWon, "texts read");
            // A module without the field ignores the member (MissingMemberHandling.Ignore): read into a type that lacks it.
            Equal(7, ProtocolJson.ToObject<ScaleInfo>(ProtocolJson.ParseObject(@"{ ""battleSize"": 7, ""texts"": { ""outcomeWon"": ""x"" } }")).BattleSize,
                "an unknown member is ignored");

            // No texts: exactly the English of CardWording.
            Equal("The Battle of Orangallu is won", CardText.Outcome("The Battle of Orangallu", true, false, null), "null texts: won");
            Equal("The battle is lost", CardText.Outcome("Will the line hold?", false, true, null), "null texts: question title");
            Equal("X is won  ·  Returning to your realm in 7  ·  [Enter] now  ·  [Space] stay on the field",
                CardText.CountdownFooter("X is won", 7, null), "null texts: countdown footer");
            Equal("X is lost  ·  [Enter] Return to your realm", CardText.WaitFooter("X is lost", null), "null texts: wait footer");

            // The French templates of bridge/data/i18n/fr.json (module.*).
            var fr = new ModuleTexts
            {
                TheBattle = "La bataille",
                OutcomeWon = "{title} est gagnée",
                OutcomeLost = "{title} est perdue",
                OutcomeOver = "{title} est terminée",
                CountdownFooter = "{outcome}  ·  Retour à votre royaume dans {seconds}  ·  [Entrée] maintenant  ·  [Espace] rester sur le terrain",
                WaitFooter = "{outcome}  ·  [Entrée] Retour à votre royaume",
            };
            Equal("Bataille d'Angers est gagnée", CardText.Outcome("Bataille d'Angers", true, false, fr), "French: won");
            Equal("Bataille d'Angers est perdue", CardText.Outcome("Bataille d'Angers", false, true, fr), "French: lost");
            Equal("La bataille est terminée", CardText.Outcome("", false, false, fr), "French: undecided, no title");
            Equal("La bataille est gagnée", CardText.Outcome("Des renforts arrivent !", true, false, fr), "French: second clash uses TheBattle");
            Equal("Bataille d'Angers est gagnée  ·  Retour à votre royaume dans 7  ·  [Entrée] maintenant  ·  [Espace] rester sur le terrain",
                CardText.CountdownFooter("Bataille d'Angers est gagnée", 7, fr), "French: countdown footer with the seconds");
            Equal("Bataille d'Angers est perdue  ·  [Entrée] Retour à votre royaume", CardText.WaitFooter("Bataille d'Angers est perdue", fr), "French: wait footer");

            // A template without its required placeholder (or empty) keeps the English.
            var broken = new ModuleTexts { OutcomeWon = "gagnée", CountdownFooter = "Retour dans {seconds}", WaitFooter = "", TheBattle = "" };
            Equal("The Battle of Orangallu is won", CardText.Outcome("The Battle of Orangallu", true, false, broken), "no {title}: English");
            Equal("The battle is lost", CardText.Outcome("Reinforcements arrive!", false, true, broken), "empty template: English");
            Equal("X is won  ·  Returning to your realm in 7  ·  [Enter] now  ·  [Space] stay on the field",
                CardText.CountdownFooter("X is won", 7, broken), "no {outcome}: English countdown");
            Equal("X is lost  ·  [Enter] Return to your realm", CardText.WaitFooter("X is lost", broken), "empty wait footer: English");
        }

        private static void CardTextsFromPresentation()
        {
            var p = new Presentation { Title = "The Battle of Orangallu", Subtitle = "Gunda against {Raiders}", Detail = null, Objective = "Rout the Raiders of Hepung" };
            CardTexts t = CardTexts.From(p, "Host of Gunda", "Raiders of Hepung", s => s?.ToUpperInvariant());
            Equal("THE BATTLE OF ORANGALLU", t.Title, "fold applied to the title");
            Equal("GUNDA AGAINST", t.Subtitle, "markup removed from the subtitle");
            Equal("", t.Detail, "missing detail stays empty");
            Equal("ROUT THE RAIDERS OF HEPUNG", t.Objective, "objective from the request");

            // A second clash (PROTOCOL.md §14.6) is an ordinary request; its title passes unchanged.
            CardTexts clash2 = CardTexts.From(new Presentation { Title = "Reinforcements arrive!", Subtitle = "Ulf joins the Host of Gunda · Summer 870",
                Detail = "Fields before South Seaxe" }, "Host of Gunda", "Raiders of Hepung", null);
            Equal("Reinforcements arrive!", clash2.Title, "second-clash title kept as sent");
            Equal("Ulf joins the Host of Gunda · Summer 870", clash2.Subtitle, "second-clash subtitle kept as sent");

            CardTexts bare = CardTexts.From(null, "Host of Gunda", "Raiders of Hepung", null);
            Equal(CardText.DefaultTitle, bare.Title, "default title");
            Equal("Host of Gunda against Raiders of Hepung", bare.Subtitle, "subtitle from the side names");
            Equal("Defeat Raiders of Hepung", bare.Objective, "objective from the enemy name");
        }

        /// <summary>Folding against the installed Galahad font (read-only), the title font of the cards.</summary>
        private static void GlyphFoldingWithGameFont()
        {
            string fnt = Path.GetFullPath(Path.Combine(Meta("BannerlordBin"), "..", "..", "GUI", "GauntletUI", "Fonts", "Galahad", "Galahad.fnt"));
            Check(File.Exists(fnt), "Galahad.fnt exists in the install");
            if (!File.Exists(fnt)) return;
            HashSet<int> glyphs = GlyphFolding.ParseBmFontCharIds(File.ReadAllText(fnt));
            Check(glyphs.Count > 500, $"Galahad glyph table parsed ({glyphs.Count})");
            Check(glyphs.Contains('·') && glyphs.Contains('[') && glyphs.Contains('…'), "card punctuation is in the font");
            Equal("Onundr of Horðaland", GlyphFolding.Fold("Ǫnundr of Hǫrðaland", glyphs), "Old Norse ogonek folded, eth kept");
            Equal("Stefan cel Mare si Sfânt", GlyphFolding.Fold("Stefan cel Mare și Sfânt", glyphs), "s-comma folded, circumflex kept");
            Equal("Gunda of Orangallu · Summer 870", GlyphFolding.Fold("Gunda of Orangallu · Summer 870", glyphs), "supported text unchanged");
            Equal("Sa'ad", GlyphFolding.Fold("Saʿad", glyphs), "ayin becomes an apostrophe");
            Equal("Reinforcements arrive!", GlyphFolding.Fold("Reinforcements arrive!", glyphs), "the second-clash title is drawable as is");
            Equal("ab", GlyphFolding.Fold("a😀b", glyphs), "unsupported astral character dropped");
            Equal("xǫ", GlyphFolding.Fold("xǫ", null), "no font table: identity");
        }

        /// <summary>The module takes over only a game the bridge started: CK3XBL_HOST, else a fresh launch lease naming a live bridge.</summary>
        private static void HostGateDecisions()
        {
            DateTime now = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
            long t = new DateTimeOffset(now).ToUnixTimeMilliseconds();
            Func<int, bool> alive = pid => pid == 4242;
            Check(HostGate.Decide("4242", null, now, alive).Active, "the environment variable activates");
            Check(!HostGate.Decide(null, null, now, alive).Active, "neither variable nor lease: passive (the player's own game)");
            Check(!HostGate.Decide("  ", null, now, alive).Active, "a blank variable is no proof");
            Check(HostGate.Decide(null, $"{{\"pid\":4242,\"t\":{t - 30000}}}", now, alive).Active, "a fresh lease naming a live bridge activates");
            Check(!HostGate.Decide(null, $"{{\"pid\":4242,\"t\":{t - 600000}}}", now, alive).Active, "a lease from an earlier launch does not");
            Check(!HostGate.Decide(null, $"{{\"pid\":5151,\"t\":{t}}}", now, alive).Active, "a lease whose bridge is gone does not");
            Check(!HostGate.Decide(null, $"{{\"pid\":4242,\"t\":{t + 600000}}}", now, alive).Active, "a lease from the future does not");
            Check(!HostGate.Decide(null, "{ broken", now, alive).Active, "an unreadable lease does not");
            Check(!HostGate.Decide(null, "[1,2]", now, alive).Active, "a lease of the wrong shape does not");
            Equal("CK3XBL_HOST", HostGate.Variable, "the name the bridge sets (BannerlordLink.HostVariable)");
            Equal("bannerlord_launch.json", HostGate.LeaseFile, "the file the bridge writes (BannerlordLink.LaunchLeaseFile)");
        }

        private static void GameVersionFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ck3xbl_selfcheck_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "Version.xml");
                File.WriteAllText(file, "<Version>\n\t<Singleplayer Value=\"v1.4.8\"/>\n</Version>");
                Equal("v1.4.8", GameVersionInfo.ReadGameVersion(file), "Version.xml read");
                File.WriteAllText(file, "<Version><Singleplayer");
                Equal(null, GameVersionInfo.ReadGameVersion(file), "a broken Version.xml is unknown, not a crash");
                Equal(null, GameVersionInfo.ReadGameVersion(Path.Combine(dir, "none.xml")), "a missing Version.xml is unknown");
                Check(GameVersionInfo.IsTarget("v1.4.8") && GameVersionInfo.IsTarget("v1.4.9") && GameVersionInfo.IsTarget(null), "v1.4.x is the target series");
                Check(!GameVersionInfo.IsTarget("v1.5.0") && !GameVersionInfo.IsTarget("v1.40.0"), "other versions are flagged");
                string installed = GameVersionInfo.ReadGameVersion(Path.Combine(Meta("BannerlordBin"), "Version.xml"));
                Console.WriteLine($"installed Bannerlord: {installed ?? "(unknown)"}");
                Check(installed != null, "the installed game's Version.xml is readable");
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        /// <summary>SubModule.xml, the built CK3Bridge.dll and the game version the module targets agree.</summary>
        private static void VersionsAgree()
        {
            string moduleDir = Meta("ModuleDir");
            var subModule = System.Xml.Linq.XDocument.Load(Path.Combine(moduleDir, "SubModule.xml")).Root;
            string declared = subModule?.Element("Version")?.Attribute("value")?.Value;
            Equal("v0.9.0", declared, "SubModule.xml Version");
            string dll = Path.Combine(moduleDir, "bin", "Win64_Shipping_Client", "CK3Bridge.dll");
            if (File.Exists(dll)) Equal(declared, "v" + AssemblyName.GetAssemblyName(dll).Version.ToString(3), "CK3Bridge.dll carries the SubModule.xml version");
            else Check(false, "CK3Bridge.dll not built: build ../src/CK3Bridge.csproj first");
            foreach (var dep in subModule.Descendants("DependedModule"))
                Equal(GameVersionInfo.Target, dep.Attribute("DependentVersion")?.Value, $"{dep.Attribute("Id")?.Value} DependentVersion");
        }

        private static void PngHeaders()
        {
            byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 1, 0, 0, 0, 0, 200 };
            Check(PngHeader.TryRead(png, out int w, out int h) && w == 256 && h == 200, "PNG IHDR size read");
            byte[] jpeg = (byte[])png.Clone();
            jpeg[0] = 0xFF;
            Check(!PngHeader.TryRead(jpeg, out _, out _), "wrong signature rejected");
            Check(!PngHeader.TryRead(new byte[10], out _, out _), "short data rejected");
            byte[] noIhdr = (byte[])png.Clone();
            noIhdr[12] = (byte)'X';
            Check(!PngHeader.TryRead(noIhdr, out _, out _), "first chunk must be IHDR");
        }

        private static void FileTransport()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ck3xbl_selfcheck_" + Guid.NewGuid().ToString("N"));
            string saved = BridgeIo.Folder;
            try
            {
                BridgeIo.Folder = dir;
                BridgeIo.WriteJsonAtomic(BridgeIo.StatusPath, new BannerlordStatus { Status = "idle" });
                BridgeIo.WriteJsonAtomic(BridgeIo.StatusPath, new BannerlordStatus { Status = "loading" });   // replace path
                string text = File.ReadAllText(BridgeIo.StatusPath);
                Check(text.Contains("\"loading\""), "atomic replace wrote the new content");
                Check(!File.Exists(BridgeIo.StatusPath + ".tmp"), "no temp file left behind");
                byte[] bytes = File.ReadAllBytes(BridgeIo.StatusPath);
                Check(bytes.Length > 3 && !(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), "UTF-8 without BOM");

                File.WriteAllText(BridgeIo.CommandPath, "go\n");
                Equal("go\n", BridgeIo.TryTake(BridgeIo.CommandPath), "take returns content");
                Check(!File.Exists(BridgeIo.CommandPath), "take deletes the file");
                Check(!File.Exists(BridgeIo.CommandPath + ".taken"), "no claim file left behind");
                Equal(null, BridgeIo.TryTake(BridgeIo.CommandPath), "nothing to take");

                // title_in_engine reads a pending request without taking it.
                File.WriteAllText(BridgeIo.RequestPath, @"{ ""id"": ""b4_x"" }");
                Equal("b4_x", ProtocolJson.ReadId(BridgeIo.TryRead(BridgeIo.RequestPath)), "pending request id read");
                Check(File.Exists(BridgeIo.RequestPath), "reading leaves the request in place");
                File.Delete(BridgeIo.RequestPath);
                Equal(null, BridgeIo.TryRead(BridgeIo.RequestPath), "no pending request");

                // Drop rule: a named cancel takes request.json only when it carries that battle's id.
                string request = @"{ ""protocol"": 2, ""id"": ""b2_x"" }";
                File.WriteAllText(BridgeIo.RequestPath, request);
                BridgeCommand lateCancel = BridgeCommand.Parse("cancel b1_x");
                Equal(null, BridgeIo.TryTake(BridgeIo.RequestPath, json => lateCancel.AppliesTo(ProtocolJson.ReadId(json))),
                    "a late cancel of b1 does not take b2's request");
                Equal(request, File.Exists(BridgeIo.RequestPath) ? File.ReadAllText(BridgeIo.RequestPath) : null, "b2's request is back in place");
                BridgeCommand ownCancel = BridgeCommand.Parse("cancel b2_x");
                Equal(request, BridgeIo.TryTake(BridgeIo.RequestPath, json => ownCancel.AppliesTo(ProtocolJson.ReadId(json))),
                    "cancel b2 takes b2's request");
                Check(!File.Exists(BridgeIo.RequestPath), "taken request is gone");

                // A newer request written while a rejected one is claimed is kept, never overwritten by the put-back.
                File.WriteAllText(BridgeIo.RequestPath, request);
                const string newer = @"{ ""id"": ""b3_x"" }";
                Equal(null, BridgeIo.TryTake(BridgeIo.RequestPath, json =>
                {
                    File.WriteAllText(BridgeIo.RequestPath, newer);   // the bridge writes the next battle meanwhile
                    return false;
                }), "rejected request");
                Equal(newer, File.ReadAllText(BridgeIo.RequestPath), "the newer request survives the put-back");
                Check(!File.Exists(BridgeIo.RequestPath + ".taken"), "superseded claim removed");

                // A reader without FileShare.Delete blocks the replace: one attempt fails at once, the next write works.
                using (new FileStream(BridgeIo.StatusPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    bool threw = false;
                    try
                    {
                        BridgeIo.WriteJsonAtomic(BridgeIo.StatusPath, new BannerlordStatus { Status = "held" }, attempts: 1);
                    }
                    catch (IOException)
                    {
                        threw = true;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        threw = true;
                    }
                    Check(threw, "single-attempt write fails while a reader blocks the replace");
                    Check(!BridgeIo.TryWriteJsonAtomic(BridgeIo.StatusPath, new BannerlordStatus { Status = "held" }, attempts: 1, logFailure: false),
                        "TryWriteJsonAtomic reports the failure");
                }
                BridgeIo.WriteJsonAtomic(BridgeIo.StatusPath, new BannerlordStatus { Status = "visible" }, attempts: 1);
                Check(File.ReadAllText(BridgeIo.StatusPath).Contains("\"visible\""), "write works once the reader is gone");

                BridgeIo.Log("self-check");
                Check(File.ReadAllText(BridgeIo.LogPath).Contains("self-check"), "log line written");
            }
            finally
            {
                BridgeIo.Folder = saved;
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (IOException)
                {
                    // temp folder cleanup is best effort
                }
            }
        }
    }
}

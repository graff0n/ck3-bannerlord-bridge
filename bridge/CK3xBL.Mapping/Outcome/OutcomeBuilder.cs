// Bannerlord result -> what CK3 applies (PROTOCOL §2.1, §6.3).
//   Outcome: status cancelled/error -> 4 (CK3 decides natively), aborted -> 3 (casualties only), result -> 1/2 by winner (none -> 3).
//   Armies:  per army tag, the levy loss in permille of its levies, and survivor shares (of max) for up to 6 MaA types, largest
//            first. Types beyond the 6 (or unknown to ck3_maa_index.json) fold into the levy fraction: deplete_army_by_percentage
//            cuts every regiment proportionally, so the folded fraction keeps the army's fatal total exact.
//            Only what changes CK3 is listed (every entry costs keypad chords): armies without losses are left out, and when
//            nothing is depleted a MaA type without dead keeps its regiments untouched (no pair).
//   People:  Bannerlord hero states -> CK3 fates (FateResolver), only for heroes whose fate is not "unharmed". The killer is the
//            enemy hero who struck the blow, else the enemy general (friendly fire never makes an ally the killer). The CK3 player
//            standing in for a commander-less host while not with the armies (PROTOCOL §15) was not there: always unharmed, no
//            personal prestige and no awards (his kills still count in the summary, the user fought them).
//   Battle clock (PROTOCOL §14): payload 4 unless MapperOptions.BattleClock rules it out. CK3's own combat then inflicts the dead
//            over several days, steered to a kill target per side (the enemy's fatal losses, cumulative over the clashes of the CK3
//            combat, like r4..r6), and one day after the combat each listed army is set to its end state: the absolute levy
//            survivors and the MaA pairs. The mod returns every indexed type without a pair to its steering-start state and leaves
//            unindexed types with CK3's own losses (PROTOCOL §14.4), so the levy survivors give up what that restore puts
//            back: the dead of indexed folded types (unindexed ones are CK3's to inflict), and in a reinforcement clash also the
//            earlier clashes' losses of those types. An army of an earlier clash keeps a pair for every type (its slot replaces that
//            clash's end state as a whole) and is listed only when it lost men in this clash. The v3 levy permille stays filled for
//            the delivery's fallback to an old mod. The per-group dead (GroupLosses) cap a later reinforcement clash
//            (ContinuationSnapshot).
//   Knights: with the knights pass (KnightBuffPlanner, gated as in PROTOCOL §13.1) the casualty conversion and the fate weights
//            take the knights' and commanders' CK3 factors, and every hero's prestige, glory and prowess rolls become awards
//            (AwardBuilder, PROTOCOL §13.5).

using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Delivery;
using CK3xBL.Core.Localization;
using CK3xBL.Core.Model;
using CK3xBL.Mapping.Buffs;
using CK3xBL.Mapping.Data;
using CK3xBL.Mapping.Planning;
using CK3xBL.Mapping.Resolution;
using CK3xBL.Protocol;

namespace CK3xBL.Mapping.Outcome;

public sealed class OutcomeBuilder
{
    public const int MaaSlotsPerArmy = 6;

    private readonly MappingTables _t;
    private readonly BattleSnapshot _battle;
    private readonly BattleRequest _request;
    private readonly BattleResult _result;
    private readonly MapperOptions _options;
    private readonly LocationFacts _facts;
    private readonly KnightBuffPlanner? _knights;
    private readonly ParentLink? _parent;

    /// <param name="battle">The snapshot the request was planned from (the capped one of a reinforcement clash).</param>
    /// <param name="knights">The knights pass of the same snapshot; null for a mod without the knights facts (today's outcome).</param>
    /// <param name="parent">The clash this one continues (PROTOCOL §14.6); null for a first clash.</param>
    public OutcomeBuilder(MappingTables tables, BattleSnapshot battle, BattleRequest request, BattleResult result, MapperOptions options, LocationFacts facts,
        KnightBuffPlanner? knights, ParentLink? parent)
    {
        _t = tables;
        _battle = battle;
        _request = request;
        _result = result;
        _options = options;
        _facts = facts;
        _knights = knights;
        _parent = parent;
    }

    public Ck3Outcome Build()
    {
        int code = OutcomeCode(_result);
        var playerSide = _request.PlayerSide == "attacker" ? SideKey.Attacker : SideKey.Defender;
        var outcome = new Ck3Outcome { Bid = _battle.Bid, ProvinceId = _battle.Location.ProvinceId, OutcomeCode = code };
        var clock = _options.BattleClock;
        bool steer = clock.Enabled && clock.MaxPayloadVersion >= KeypadLayout.SteerPayloadVersion;
        outcome.PayloadVersion = steer ? KeypadLayout.SteerPayloadVersion : KeypadLayout.PayloadVersion;
        outcome.PaceDays = steer ? Math.Clamp(clock.PaceDays, KeypadLayout.MinPaceDays, KeypadLayout.MaxPaceDays) : 0;
        outcome.ParentBid = _parent?.Outcome.Bid ?? _battle.ParentBid;
        outcome.Clash = _parent is null ? Math.Max(1, _battle.Clash) : Math.Max(_battle.Clash, _parent.Outcome.Clash + 1);
        string place = PresentationWriter.PlaceName(_battle);
        if (code == 4)
        {
            outcome.Summary = Loc.T("outcome.decided_in_ck3", ("place", place));
            return outcome;
        }

        var fatal = FatalLosses(code);
        outcome.GroupLosses = fatal.Select(d => new GroupLoss { Side = d.Side, GroupKey = d.Id, Dead = d.Dead }).ToList();
        outcome.Armies = ArmyLosses(fatal, steer);
        outcome.Characters = CharacterFates(code);

        outcome.PlayerWon = (code is 1 or 2) && (code == 1) == (playerSide == SideKey.Attacker);
        var avatar = AllHeroes().FirstOrDefault(h => h.Spec.IsPlayer);
        var avatarResult = avatar.Spec is null ? null : HeroResultOf(avatar.Spec);
        int myKills = avatarResult is null ? 0 : Math.Max(0, avatarResult.Kills - FriendlyHeroesDownedBy(avatar.Side, avatar.Spec.Key));
        // Personal valour, only when the CK3 player fought in person and the battle was decided; per clash (each apply adds it).
        bool fought = avatar.Spec?.Role == "player" && avatarResult is not null && avatarResult.State != "unspawned" && !IsAbsentPlayer(avatar.Side, avatar.Spec);
        outcome.PrestigeBonus = fought && (code is 1 or 2) ? Math.Min(200, 2 * myKills) + (outcome.PlayerWon ? 25 : 0) : 0;
        // Kill targets and the summary values r4..r6 count the whole CK3 combat: this clash plus the clashes before it.
        var earlier = _parent?.Outcome;
        outcome.AttackerKillTarget = SideFatal(fatal, SideKey.Defender) + (earlier?.AttackerKillTarget ?? 0);
        outcome.DefenderKillTarget = SideFatal(fatal, SideKey.Attacker) + (earlier?.DefenderKillTarget ?? 0);
        outcome.MyLost = SideFatal(fatal, playerSide) + (earlier?.MyLost ?? 0);
        outcome.EnemyLost = SideFatal(fatal, Other(playerSide)) + (earlier?.EnemyLost ?? 0);
        outcome.MyKills = myKills + (earlier?.MyKills ?? 0);
        if (_knights is not null) outcome.Awards = AwardBuilder.Build(AwardHeroes(playerSide), outcome.Characters);
        outcome.Summary = SummaryLine(code, outcome, place, earlier);
        if (AwardBuilder.RenownLine(outcome.Awards) is { } renown) outcome.Summary += "\n" + renown;
        return outcome;
    }

    public static int OutcomeCode(BattleResult result) => (result.Status ?? "").ToLowerInvariant() switch
    {
        "aborted" => 3,
        "result" or "finished" => result.Winner switch { "attacker" => 1, "defender" => 2, _ => 3 },
        _ => 4,      // cancelled, error, started (never finished), unknown
    };

    private static SideKey Other(SideKey k) => k == SideKey.Attacker ? SideKey.Defender : SideKey.Attacker;

    private SideRequest RequestSide(SideKey k) => (k == SideKey.Attacker ? _request.Attacker : _request.Defender) ?? new SideRequest();

    private SideResult? ResultSide(SideKey k) => k == SideKey.Attacker ? _result.Attacker : _result.Defender;

    // ---- casualties --------------------------------------------------------------------------------------------------

    /// <summary>CK3 dead of one troop group; <paramref name="Id"/> is TroopRequest.Group verbatim.</summary>
    private sealed record Fatal(SideKey Side, GroupKey Key, string Id, int Dead);

    private List<Fatal> FatalLosses(int code)
    {
        var groups = new Dictionary<SideKey, List<(GroupKey Key, CasualtyGroup Group)>>();
        var stats = new Dictionary<SideKey, Dictionary<string, GroupStats>>();
        foreach (var side in new[] { SideKey.Attacker, SideKey.Defender })
        {
            stats[side] = GroupResults.Resolve(RequestSide(side), ResultSide(side));
            groups[side] = stats[side]
                .Select(kv => GroupKey.TryParse(kv.Key, out var key) && key.Kind != GroupKind.Retinue ? CasualtyGroupFor(side, key, kv.Key, kv.Value) : null)
                .Where(x => x is not null).Select(x => x!.Value).ToList();
        }

        var settings = new CasualtySettings
        {
            Terrain = _facts.Terrain,
            HarshWinter = _facts.HarshWinter,
            WinnerConversion = _options.KilledToCk3DeadShareWinner,
            LoserConversion = _options.KilledToCk3DeadShareLoser,
            PursuitBase = _options.RoutedToCk3DeadShareLoser,
        };
        // The side commanders' casualty traits (knights pass): factors on a side's fatal share and on its pursuit losses.
        CasualtySettings SidesOf(SideKey winner, SideKey loser) => _knights is null ? settings : settings with
        {
            WinnerFatalShareMult = _knights.SideCasualtiesFor(winner).FatalShareMult,
            LoserFatalShareMult = _knights.SideCasualtiesFor(loser).FatalShareMult,
            LoserRetreatLossMult = _knights.SideCasualtiesFor(loser).RetreatLossMult,
        };

        var dead = new List<Fatal>();
        void Collect(SideKey side, IReadOnlyList<GroupCasualties> cas)
        {
            var bySide = groups[side].ToDictionary(g => g.Group.Id, StringComparer.Ordinal);
            foreach (var c in cas)
            {
                var g = bySide[c.Id];
                dead.Add(new Fatal(side, g.Key, g.Group.Id, Math.Clamp(c.Fatal, 0, g.Group.Soldiers)));
            }
        }

        if (code is 1 or 2)
        {
            var winner = code == 1 ? SideKey.Attacker : SideKey.Defender;
            var loser = Other(winner);
            var loserStats = stats[loser].Values.ToList();
            bool annihilated = loserStats.Count > 0 && loserStats.Sum(g => g.Requested) > 0 && loserStats.All(g => g.Downed >= g.Requested);
            var r = CasualtyModel.Convert(_t.Mapping, groups[winner].Select(g => g.Group).ToList(), groups[loser].Select(g => g.Group).ToList(),
                SidesOf(winner, loser) with { Annihilated = annihilated, LoserIsDefender = loser == SideKey.Defender });
            Collect(winner, r.Winner);
            Collect(loser, r.Loser);
        }
        else
        {
            // No winner (aborted or drawn): only main-phase losses, no pursuit; each side converts at the winner's (milder) share.
            foreach (var side in new[] { SideKey.Attacker, SideKey.Defender })
            {
                var r = CasualtyModel.Convert(_t.Mapping, groups[side].Select(g => g.Group).ToList(), Array.Empty<CasualtyGroup>(),
                    SidesOf(side, Other(side)) with { LoserIsDefender = side == SideKey.Attacker });
                Collect(side, r.Winner);
            }
        }
        return dead;
    }

    private (GroupKey Key, CasualtyGroup Group)? CasualtyGroupFor(SideKey side, GroupKey key, string id, GroupStats s)
    {
        var army = _battle.Side(side).Armies.FirstOrDefault(a => a.Tag == key.ArmyTag);
        if (army is null) return null;
        int soldiers;
        double toughness, pursuit, screen;
        if (key.Kind == GroupKind.Levy)
        {
            soldiers = army.Levies;
            toughness = _t.Ck3.Levy.Toughness; pursuit = _t.Ck3.Levy.Pursuit; screen = _t.Ck3.Levy.Screen;
        }
        else
        {
            var reg = army.Regiments.FirstOrDefault(r => r.RegimentId == key.RegimentId && r.MaaKey == key.MaaKey)
                      ?? army.Regiments.FirstOrDefault(r => r.MaaKey == key.MaaKey);
            if (reg is null) return null;
            soldiers = reg.Current;
            var stats = _t.Ck3.MaaTypes.GetValueOrDefault(key.MaaKey!);
            toughness = stats?.Toughness ?? _t.Mapping.MaaTier.GetValueOrDefault(key.MaaKey!)?.Toughness ?? 10;
            pursuit = stats?.Pursuit ?? 0;
            screen = stats?.Screen ?? 0;
        }
        if (_knights?.GroupCasualtiesFor(side, key) is { } mods)
        {
            pursuit = mods.Pursuit(pursuit);
            screen = mods.Screen(screen);
        }
        return (key, new CasualtyGroup
        {
            Id = id, Soldiers = soldiers, Agents = s.Requested, Killed = s.Killed, Unconscious = s.Wounded, Fled = s.Routed,
            Toughness = toughness, Pursuit = pursuit, Screen = screen,
        });
    }

    private static int SideFatal(List<Fatal> dead, SideKey side) => dead.Where(d => d.Side == side).Sum(d => d.Dead);

    /// <param name="steer">Payload 4: the end state replaces the deplete, so an army is listed when its levy survivors change, and
    /// a reinforcement clash takes the steering-start state of the earlier clashes' armies into account.</param>
    private List<ArmyLoss> ArmyLosses(List<Fatal> dead, bool steer)
    {
        var start = steer && _parent is not null ? SteeringStart(_parent) : new Dictionary<int, ArmySnapshot>();
        var losses = new List<(ArmyLoss Loss, int Dead, int Size)>();
        foreach (var side in _battle.Sides)
        {
            foreach (var army in side.Armies.Where(a => a.Tag > 0))
            {
                var mine = dead.Where(d => d.Side == side.Key && d.Key.ArmyTag == army.Tag).ToList();
                int levyDead = mine.Where(d => d.Key.Kind == GroupKind.Levy).Sum(d => d.Dead);
                var types = army.Regiments
                    .GroupBy(r => r.MaaKey)
                    .Select(g => (Key: g.Key, Current: g.Sum(r => Math.Max(0, r.Current)), Max: g.Sum(r => Math.Max(0, r.Max)),
                                  Dead: mine.Where(d => d.Key.Kind == GroupKind.Maa && d.Key.MaaKey == g.Key).Sum(d => d.Dead)))
                    .OrderByDescending(x => x.Current).ThenBy(x => x.Key, StringComparer.Ordinal)
                    .ToList();
                // An army of an earlier clash of this CK3 combat: the mod keeps its steering-start state from the first clash.
                start.TryGetValue(army.Tag, out var before);

                var loss = new ArmyLoss { ArmyTag = army.Tag };
                int foldedMen = 0, foldedDead = 0;
                double restored = 0;
                foreach (var t in types)
                {
                    bool indexed = _t.MaaIndex.TryGetValue(t.Key, out int index);
                    if (loss.Maa.Count >= MaaSlotsPerArmy || t.Max <= 0 || !indexed)
                    {
                        foldedMen += t.Current;
                        foldedDead += t.Dead;
                        restored += RestoredByTheFix(t.Current, t.Max, t.Dead, indexed, before?.Regiments.Where(r => r.MaaKey == t.Key).ToList());
                        continue;
                    }
                    int survivors = Math.Max(0, t.Current - t.Dead);
                    loss.Maa.Add(new MaaLoss
                    {
                        MaaKey = t.Key,
                        MaaIndex = index,
                        SurvivorSharePermille = Math.Clamp(JsCompat.RoundToInt(1000.0 * survivors / t.Max), 0, 1000),
                    });
                }
                int levyMen = Math.Max(0, army.Levies) + foldedMen;
                loss.LevyLossPermille = levyMen > 0 ? Math.Clamp(JsCompat.RoundToInt(1000.0 * (levyDead + foldedDead) / levyMen), 0, 1000) : 0;
                // Payload 4 end state: the levies left, absolute, less what the fix gives back to the types without a pair, so the
                // army's total is right (its composition is approximate for folded types).
                loss.LevySurvivors = Math.Max(0, army.Levies - levyDead - JsCompat.RoundToInt(restored));
                if (before is not null)
                {
                    // Every pair stays: the slot overwrites the earlier clash's six pairs, and a type left out would return to its
                    // state before the first clash. Without losses in this clash the army keeps the earlier end state, unlisted.
                    if (mine.Sum(d => d.Dead) == 0) continue;
                }
                else
                {
                    // Without a deplete, a pair for a type that lost nobody would only shuffle men between its regiments (each gets the
                    // type's share of its max). With a deplete the pair is needed: it restores what the proportional cut took.
                    if (loss.LevyLossPermille == 0)
                        loss.Maa.RemoveAll(m => types.First(t => t.Key == m.MaaKey).Dead == 0);
                    bool survivorsChange = steer && loss.LevySurvivors != Math.Max(0, army.Levies);
                    if (loss.LevyLossPermille == 0 && loss.Maa.Count == 0 && !survivorsChange) continue;
                }
                losses.Add((loss, mine.Sum(d => d.Dead), army.Size));
            }
        }
        // Largest losses first: the keypad has 8 army slots, so the smallest losses are the ones left out if there are more.
        return losses.OrderByDescending(x => x.Dead).ThenByDescending(x => x.Size).ThenBy(x => x.Loss.ArmyTag).Select(x => x.Loss).ToList();
    }

    /// <summary>The armies by tag as the mod stored their steering-start state (once, at the ST|BEGIN of the first
    /// clash an army fights): the first clash's planned snapshot (<see cref="ParentLink.Root"/>, or the parent's for clash 2), then the
    /// parent's for an army that first fought there. Exact up to clash 3; from clash 4 on, an army that first fought in a middle clash
    /// other than the parent is read from the parent's capped snapshot.</summary>
    private static Dictionary<int, ArmySnapshot> SteeringStart(ParentLink parent)
    {
        var start = new Dictionary<int, ArmySnapshot>();
        foreach (var snapshot in parent.Root is { } root ? new[] { root, parent.Planned } : new[] { parent.Planned })
            foreach (var army in snapshot.Sides.SelectMany(s => s.Armies).Where(a => a.Tag > 0))
                start.TryAdd(army.Tag, army);
        return start;
    }

    /// <summary>The men the fix (PROTOCOL §14.4) gives back to a MaA type without a pair, above its Bannerlord survivors;
    /// payload 4 takes them from the levies. An unindexed type keeps CK3's own losses: nothing is given back, and its Bannerlord dead
    /// are CK3's to inflict. An indexed type returns to its steering-start state: for an army new in this clash that is this snapshot
    /// (its dead come back), for an army of an earlier clash the first clash's share of max (its dead and the earlier clashes'
    /// losses come back). A type without regiments at the steering start has no such state and keeps CK3's losses.</summary>
    /// <param name="startRegiments">The type's regiments at the steering start; null for an army new in this clash.</param>
    private static double RestoredByTheFix(int current, int max, int dead, bool indexed, List<RegimentSnapshot>? startRegiments)
    {
        if (!indexed || max <= 0) return 0;
        if (startRegiments is null) return dead;
        int startMax = startRegiments.Sum(r => Math.Max(0, r.Max));
        if (startMax <= 0) return 0;
        return (double)startRegiments.Sum(r => Math.Max(0, r.Current)) * max / startMax - (current - dead);
    }

    // ---- characters --------------------------------------------------------------------------------------------------

    private IEnumerable<(SideKey Side, HeroSpec Spec)> AllHeroes() =>
        (_request.Attacker?.Heroes ?? new List<HeroSpec>()).Select(h => (SideKey.Attacker, h))
        .Concat((_request.Defender?.Heroes ?? new List<HeroSpec>()).Select(h => (SideKey.Defender, h)));

    private HeroResult? HeroResultOf(HeroSpec spec) =>
        _result.Heroes?.FirstOrDefault(r => r.Key == spec.Key)
        ?? (spec.Tag > 0 ? _result.Heroes?.FirstOrDefault(r => r.Tag == spec.Tag) : null);

    /// <summary>The CK3 player put on the field as the avatar although he is not with the armies (PROTOCOL §15,
    /// HeroPlanner.IsAbsent): the outcome never harms or rewards him.</summary>
    private bool IsAbsentPlayer(SideKey side, HeroSpec? spec) =>
        spec is { IsPlayer: true, Role: "player" }
        && _battle.Side(side).Characters.FirstOrDefault(c => (spec.Tag > 0 && c.Tag == spec.Tag) || (spec.Ck3Id != 0 && c.Ref.Ck3Id == spec.Ck3Id)) is { } character
        && HeroPlanner.IsAbsent(character);

    internal static bool IsDowned(HeroResult? r) => (r?.State ?? "").ToLowerInvariant() is "killed" or "wounded";

    /// <summary>Heroes of <paramref name="side"/> (other than the striker) that the hero <paramref name="key"/> downed: friendly fire,
    /// which the module counts in <c>kills</c> like any other blow.</summary>
    private int FriendlyHeroesDownedBy(SideKey side, string? key) =>
        key is null ? 0 : AllHeroes().Count(h => h.Side == side && h.Spec.Key != key && HeroResultOf(h.Spec) is { } r && IsDowned(r) && r.KilledBy == key);

    private List<CharacterFate> CharacterFates(int code)
    {
        var heroes = AllHeroes().ToList();
        var byKey = heroes.Where(h => !string.IsNullOrEmpty(h.Spec.Key)).GroupBy(h => h.Spec.Key).ToDictionary(g => g.Key, g => g.First());
        // An absent stand-in player (PROTOCOL §15.1) was not in the CK3 combat, so he is never named as killer: not for the blows
        // his avatar struck, and not as his side's general either (the victim then dies in battle with no killer, as when
        // CK3's side has no commander).
        var absentKeys = heroes.Where(h => !string.IsNullOrEmpty(h.Spec.Key) && IsAbsentPlayer(h.Side, h.Spec)).Select(h => h.Spec.Key).ToHashSet();
        int EnemyTag(string? key, SideKey victimSide) =>
            key is not null && !absentKeys.Contains(key) && byKey.TryGetValue(key, out var h) && h.Side != victimSide && h.Spec.Tag > 0 ? h.Spec.Tag : 0;
        int Strength(SideKey k) { var s = _battle.Side(k); return s.Size > 0 ? s.Size : s.Armies.Sum(a => a.Size); }
        bool atWar = !string.IsNullOrWhiteSpace(_battle.WarName);
        SideKey? loser = code switch { 1 => SideKey.Defender, 2 => SideKey.Attacker, _ => null };

        var fates = new List<(CharacterFate Fate, bool Ck3Player)>();
        foreach (var (side, spec) in heroes)
        {
            if (spec.Tag <= 0) continue;
            var hr = HeroResultOf(spec);
            if (!IsDowned(hr)) continue;      // alive, routed or never spawned: unharmed, nothing for CK3 to apply
            if (IsAbsentPlayer(side, spec)) continue;      // not in the CK3 combat (PROTOCOL §15): unharmed, like PlayerDeathIsReal = false
            var character = _battle.Side(side).Characters.FirstOrDefault(c => c.Tag == spec.Tag)
                            ?? new CharacterSnapshot { Tag = spec.Tag, Ref = new CharacterRef { Ck3Id = spec.Ck3Id } };
            bool ck3Player = spec.IsPlayer && spec.Role == "player";
            bool asCommander = character.IsCommander || spec.Role == "commander" || RequestSide(side).General == spec.Key;
            int fate = FateResolver.Downed(new FateContext
            {
                Bid = _battle.Bid, Character = character, AsCommander = asCommander,
                IsCk3Player = ck3Player, PlayerDeathIsReal = _options.PlayerDeathIsReal,
                OnLosingSide = loser == side, AtWar = atWar, OwnStrength = Strength(side), EnemyStrength = Strength(Other(side)),
                Factors = _knights?.FateFactorsFor(side, character, asCommander),
            });
            // CK3 credits battle deaths to the enemy commander when no named enemy struck the blow.
            int killer = EnemyTag(hr!.KilledBy, side);
            if (killer == 0) killer = EnemyTag(RequestSide(Other(side)).General, side);
            fates.Add((new CharacterFate { Tag = spec.Tag, Ck3Id = spec.Ck3Id, Fate = fate, KillerTag = killer, Kills = hr.Kills }, ck3Player));
        }
        // The keypad has 20 character slots: the player first, then the gravest fates (killed, captured, maimed, wounded).
        return fates.OrderByDescending(f => f.Ck3Player).ThenByDescending(f => f.Fate.Fate).ThenBy(f => f.Fate.Tag).Select(f => f.Fate).ToList();
    }

    /// <summary>Every hero of the request with his result and CK3 character, for the awards.</summary>
    private List<AwardHero> AwardHeroes(SideKey playerSide) =>
        AllHeroes()
            .Where(h => !IsAbsentPlayer(h.Side, h.Spec))
            .Select(h => (h.Side, h.Spec, Result: HeroResultOf(h.Spec)))
            .Where(h => h.Result is not null)
            .Select(h => new AwardHero(h.Side, h.Spec, h.Result!, _battle.Side(h.Side).Characters.FirstOrDefault(c => c.Tag == h.Spec.Tag && h.Spec.Tag > 0),
                                       h.Side == playerSide))
            .ToList();

    // ---- text --------------------------------------------------------------------------------------------------------

    /// <summary>The result line. A reinforcement clash (<paramref name="earlier"/> = the clash it continues) leads with this clash's
    /// numbers and adds the whole battle's (r4/r5 are cumulative), so the card never shows the totals as if one clash had cost them.</summary>
    public static string SummaryLine(int code, Ck3Outcome o, string place, Ck3Outcome? earlier = null)
    {
        int myLost = o.MyLost - (earlier?.MyLost ?? 0), enemyLost = o.EnemyLost - (earlier?.EnemyLost ?? 0);
        int myKills = o.MyKills - (earlier?.MyKills ?? 0);
        string mine = Loc.Number(myLost), theirs = Loc.Number(enemyLost);
        string key = code == 3 ? "outcome.broken_off" : o.PlayerWon ? "outcome.victory" : "outcome.defeat";
        string line = Loc.T(key, ("place", place), ("mine", mine), ("theirs", theirs));
        if (myKills > 0)
            line += " " + (myKills == 1 ? Loc.T("outcome.kills_one") : Loc.T("outcome.kills_many", ("count", Loc.Number(myKills))));
        if (earlier is not null)
        {
            string clashes = o.Clash <= 2 ? Loc.T("outcome.clashes_both") : Loc.T("outcome.clashes_all", ("count", Loc.Number(o.Clash)));
            line += " " + Loc.T("outcome.over_clashes", ("clashes", clashes), ("mine", Loc.Number(o.MyLost)), ("theirs", Loc.Number(o.EnemyLost)));
        }
        return line;
    }
}

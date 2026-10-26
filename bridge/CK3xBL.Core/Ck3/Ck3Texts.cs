// Texts of the CK3 mod that the bridge shows the player, so the two always name the same thing (PROTOCOL §13.7).
using CK3xBL.Core.Localization;

namespace CK3xBL.Core.Ck3;

public static class Ck3Texts
{
    /// <summary>The option of the mod's event 0002 that applies the outcome (ck3xbl_battle.0002.a in
    /// ck3_mod/ck3xbl/localization/english/ck3xbl_l_english.yml; an integration test keeps them equal). English, for the logs and
    /// the tray.</summary>
    public const string DustSettlesOption = "The dust settles...";

    /// <summary><see cref="DustSettlesOption"/> as the player's CK3 shows it ("ck3.option.dust_settles").</summary>
    public static string DustSettles => Loc.T("ck3.option.dust_settles");

    /// <summary>The option that lets CK3 fight the battle without the player ("ck3.option.run_course").</summary>
    public static string RunCourse => Loc.T("ck3.option.run_course");

    /// <summary>The option that leaves the battle to the commanders ("ck3.option.commanders_decide").</summary>
    public static string CommandersDecide => Loc.T("ck3.option.commanders_decide");
}

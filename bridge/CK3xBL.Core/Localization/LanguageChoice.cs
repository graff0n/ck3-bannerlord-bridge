// Which language the battle loop speaks (ux.language): en | fr | de | es, or auto (default) = CK3's own language, read from
// pdx_settings.txt in the CK3 user folder. That file nests it in the "System" block:
//   "language"={
//       version=0
//       value="l_french"
//   }
// l_english -> en, l_french -> fr, l_german -> de, l_spanish -> es; any other CK3 language, a missing or unreadable file -> en.
using System.Text.RegularExpressions;

namespace CK3xBL.Core.Localization;

public static partial class LanguageChoice
{
    public const string Auto = "auto";
    public static readonly IReadOnlyList<string> Supported = ["en", "fr", "de", "es"];

    /// <summary>The language for <paramref name="uxLanguage"/>: itself when supported, else CK3's (auto, or an unknown value).</summary>
    /// <param name="ck3Language">CK3's language code ("l_french"), or null when it could not be read.</param>
    public static string Resolve(string? uxLanguage, string? ck3UserDir, out string? ck3Language)
    {
        ck3Language = ReadCk3Language(ck3UserDir);
        string wanted = (uxLanguage ?? "").Trim().ToLowerInvariant();
        return Supported.Contains(wanted) ? wanted : FromCk3(ck3Language);
    }

    /// <summary>CK3's l_xxx code as a language of the bridge.</summary>
    public static string FromCk3(string? ck3Language) => ck3Language switch
    {
        "l_english" => "en",
        "l_french" => "fr",
        "l_german" => "de",
        "l_spanish" => "es",
        _ => "en",
    };

    /// <summary>The language CK3 runs in (pdx_settings.txt), or null when the file is missing or says nothing readable.</summary>
    public static string? ReadCk3Language(string? ck3UserDir)
    {
        if (string.IsNullOrWhiteSpace(ck3UserDir)) return null;
        try
        {
            string file = Path.Combine(ck3UserDir, "pdx_settings.txt");
            return File.Exists(file) ? ParseCk3Language(File.ReadAllText(file)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The value of the "language" block of a pdx_settings.txt text ("l_french"), or null.</summary>
    public static string? ParseCk3Language(string? settings)
    {
        if (string.IsNullOrEmpty(settings)) return null;
        var m = LanguageBlock().Match(settings);
        return m.Success ? m.Groups["code"].Value : null;
    }

    [GeneratedRegex(@"""language""\s*=\s*\{[^{}]*?\bvalue\s*=\s*""(?<code>l_[a-z_]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageBlock();
}

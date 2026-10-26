using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CK3xBL.Protocol;

namespace CK3Bridge
{
    /// <summary>The four lines of the CK3 title card (loading card, in-engine title card and mission objective).</summary>
    public sealed class CardTexts
    {
        public string Title = "";
        public string Subtitle = "";
        public string Detail = "";
        public string Objective = "";

        /// <summary>
        /// Builds the card from request.presentation (PROTOCOL.md §3.2). Every line is cleaned (<see cref="CardText.Clean"/>)
        /// and folded to the game font (<paramref name="fold"/>, identity when null). Missing lines fall back to the side
        /// names so a hand-written request still gets a readable card.
        /// </summary>
        public static CardTexts From(Presentation presentation, string playerSideName, string enemySideName, Func<string, string> fold)
        {
            Func<string, string> f = fold ?? (s => s);
            string player = CardText.Clean(f(playerSideName), CardText.MaxLineLength);
            string enemy = CardText.Clean(f(enemySideName), CardText.MaxLineLength);
            var texts = new CardTexts
            {
                Title = CardText.Clean(f(presentation?.Title), CardText.MaxTitleLength),
                Subtitle = CardText.Clean(f(presentation?.Subtitle), CardText.MaxLineLength),
                Detail = CardText.Clean(f(presentation?.Detail), CardText.MaxLineLength),
                Objective = CardText.Clean(f(presentation?.Objective), CardText.MaxLineLength),
            };
            if (texts.Title.Length == 0) texts.Title = CardText.DefaultTitle;
            if (texts.Subtitle.Length == 0 && player.Length > 0 && enemy.Length > 0) texts.Subtitle = player + " against " + enemy;
            if (texts.Objective.Length == 0) texts.Objective = CardText.DefaultObjective(enemy);
            return texts;
        }
    }

    /// <summary>Pure wording and text-safety rules of the presentation layer.</summary>
    public static class CardText
    {
        public const int MaxTitleLength = 96;
        public const int MaxLineLength = 160;
        public const string DefaultTitle = "The Battle";

        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);

        /// <summary>
        /// Removes what Gauntlet text or TextObject would interpret or cannot draw: "{...}" markup and stray braces,
        /// control characters; collapses whitespace (line breaks included) and caps the length at a word boundary with
        /// an ellipsis. Dots and punctuation are kept (unlike object ids, see Naming).
        /// </summary>
        public static string Clean(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder(value.Length);
            int braceDepth = 0;
            foreach (char c in value)
            {
                if (c == '{')
                {
                    braceDepth++;
                    continue;
                }
                if (c == '}')
                {
                    if (braceDepth > 0) braceDepth--;
                    continue;
                }
                if (braceDepth > 0) continue;
                sb.Append(char.IsControl(c) ? ' ' : c);
            }
            string clean = Whitespace.Replace(sb.ToString(), " ").Trim();
            if (maxLength <= 1 || clean.Length <= maxLength) return clean;
            string cut = clean.Substring(0, maxLength - 1);
            int space = cut.LastIndexOf(' ');
            if (space > maxLength / 2) cut = cut.Substring(0, space);
            return cut.TrimEnd(' ', ',', ';', ':', '-', '·') + "…";
        }

        /// <summary>Objective when the bridge sent none: "Defeat the Host of X" style, or a generic line.</summary>
        public static string DefaultObjective(string enemyName)
        {
            if (string.IsNullOrEmpty(enemyName)) return "Defeat the enemy host";
            return "Defeat " + enemyName;
        }

        /// <summary>
        /// "The Battle of Orangallu is won" / "is lost" / "is over" (no decision, e.g. a mutual rout). A title that is an
        /// exclamation or a question rather than a battle name (the second clash's "Reinforcements arrive!",
        /// PROTOCOL.md §14.6) reads "The battle is won".
        /// </summary>
        /// <param name="texts">presentation.texts (PROTOCOL.md §3.2): the bridge's templates in the player's language; null, or a
        /// template that is empty or lacks {title}, keeps the English.</param>
        public static string Outcome(string title, bool playerWon, bool playerLost, ModuleTexts texts = null)
        {
            bool named = !string.IsNullOrEmpty(title) && !title.EndsWith("!", StringComparison.Ordinal) &&
                         !title.EndsWith("?", StringComparison.Ordinal);
            string template = texts == null ? null : playerWon ? texts.OutcomeWon : playerLost ? texts.OutcomeLost : texts.OutcomeOver;
            if (Usable(template, "{title}"))
            {
                string localSubject = named ? title : !string.IsNullOrEmpty(texts.TheBattle) ? texts.TheBattle : "The battle";
                return template.Replace("{title}", localSubject);
            }
            string subject = named ? title : "The battle";
            string verdict = playerWon ? "won" : playerLost ? "lost" : "over";
            return subject + " is " + verdict;
        }

        /// <summary>A template from presentation.texts that can be used: not empty and with its required placeholder.</summary>
        private static bool Usable(string template, string placeholder) =>
            !string.IsNullOrEmpty(template) && template.IndexOf(placeholder, StringComparison.Ordinal) >= 0;

        /// <summary>Seconds shown in the countdown: rounded up, never negative.</summary>
        public static int SecondsShown(float secondsLeft)
        {
            if (float.IsNaN(secondsLeft) || secondsLeft <= 0f) return 0;
            return (int)Math.Ceiling(secondsLeft);
        }

        /// <summary>Result footer while the return countdown runs.</summary>
        public static string CountdownFooter(string outcome, int seconds, ModuleTexts texts = null)
        {
            string s = seconds.ToString(CultureInfo.InvariantCulture);
            if (Usable(texts?.CountdownFooter, "{outcome}")) return texts.CountdownFooter.Replace("{outcome}", outcome).Replace("{seconds}", s);
            return outcome + "  ·  Returning to your realm in " + s + "  ·  [Enter] now  ·  [Space] stay on the field";
        }

        /// <summary>Result footer when the player stays on the field (or the countdown is disabled).</summary>
        public static string WaitFooter(string outcome, ModuleTexts texts = null) =>
            Usable(texts?.WaitFooter, "{outcome}") ? texts.WaitFooter.Replace("{outcome}", outcome) : outcome + "  ·  [Enter] Return to your realm";
    }

    /// <summary>
    /// Bannerlord draws text from bitmap fonts with a fixed glyph set (GUI/GauntletUI/Fonts/*/*.fnt); a missing glyph
    /// renders as nothing. CK3 names use letters such as ǫ (Old Norse) or ș (Romanian) that Galahad lacks, so text is
    /// folded to the supported set: unsupported letters lose their diacritics (canonical decomposition), a few letters
    /// without a decomposition get a close ASCII form, anything else is dropped.
    /// </summary>
    public static class GlyphFolding
    {
        private static readonly Dictionary<char, string> Substitutes = new Dictionary<char, string>
        {
            { 'ŋ', "n" }, { 'Ŋ', "N" },   // ŋ Ŋ
            { 'ı', "i" },                      // dotless i
            { 'ə', "e" }, { 'Ə', "E" },   // ə Ə
            { 'ɛ', "e" }, { 'Ɛ', "E" },   // ɛ Ɛ
            { 'ɔ', "o" }, { 'Ɔ', "O" },   // ɔ Ɔ
            { 'ʿ', "'" }, { 'ʾ', "'" },   // ʿ ʾ (Arabic transliteration)
            { 'ʻ', "'" }, { 'ʼ', "'" },   // ʻ ʼ
            { '‘', "'" }, { '’', "'" },
            { '“', "\"" }, { '”', "\"" },
            { 'Ð', "D" }, { 'ð', "d" },   // Ð ð
            { 'Þ', "Th" }, { 'þ', "th" }, // Þ þ
            { 'Æ', "AE" }, { 'æ', "ae" },
            { 'Œ', "OE" }, { 'œ', "oe" },
            { 'ß', "ss" },
            { 'Ø', "O" }, { 'ø', "o" },
            { 'Ł', "L" }, { 'ł', "l" },
            { 'Đ', "D" }, { 'đ', "d" },
            { '–', "-" }, { '—', "-" },
            { '·', " - " }, { '•', " - " },
            { '…', "..." },
        };

        private static readonly Regex CharId = new Regex("<char\\s+id=\"(\\d+)\"", RegexOptions.Compiled);

        /// <summary>Code points listed in a BMFont XML file (<c>&lt;char id="N" .../&gt;</c>).</summary>
        public static HashSet<int> ParseBmFontCharIds(string fontXml)
        {
            var ids = new HashSet<int>();
            if (string.IsNullOrEmpty(fontXml)) return ids;
            foreach (Match m in CharId.Matches(fontXml))
                if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) ids.Add(id);
            return ids;
        }

        /// <summary>
        /// Returns <paramref name="text"/> using only code points in <paramref name="supported"/> (plus the space).
        /// A null or empty set means "unknown font": the text is returned unchanged.
        /// </summary>
        public static string Fold(string text, ICollection<int> supported)
        {
            if (string.IsNullOrEmpty(text) || supported == null || supported.Count == 0) return text ?? "";
            StringBuilder sb = null;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == ' ' || (!char.IsSurrogate(c) && supported.Contains(c)))
                {
                    sb?.Append(c);
                    continue;
                }
                if (sb == null) sb = new StringBuilder(text, 0, i, text.Length + 8);
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    int cp = char.ConvertToUtf32(c, text[i + 1]);
                    i++;
                    if (supported.Contains(cp)) sb.Append(char.ConvertFromUtf32(cp));
                    continue;
                }
                sb.Append(Replacement(c, supported));
            }
            return sb?.ToString() ?? text;
        }

        private static string Replacement(char c, ICollection<int> supported)
        {
            if (char.IsSurrogate(c)) return "";
            if (char.IsWhiteSpace(c)) return " ";
            string decomposed = c.ToString().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (char d in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(d) == UnicodeCategory.NonSpacingMark) continue;
                if (supported.Contains(d)) sb.Append(d);
                else if (d != c && Substitutes.TryGetValue(d, out string s1) && AllSupported(s1, supported)) sb.Append(s1);
            }
            if (sb.Length > 0) return sb.ToString();
            return Substitutes.TryGetValue(c, out string s2) && AllSupported(s2, supported) ? s2 : "";
        }

        private static bool AllSupported(string s, ICollection<int> supported)
        {
            foreach (char c in s)
                if (c != ' ' && !supported.Contains(c)) return false;
            return true;
        }
    }

    /// <summary>Minimal PNG header check before image bytes are handed to the native texture loader.</summary>
    public static class PngHeader
    {
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>True for a PNG whose first chunk is IHDR; returns its pixel size.</summary>
        public static bool TryRead(byte[] data, out int width, out int height)
        {
            width = height = 0;
            if (data == null || data.Length < 24) return false;
            for (int i = 0; i < Signature.Length; i++)
                if (data[i] != Signature[i]) return false;
            if (data[12] != 'I' || data[13] != 'H' || data[14] != 'D' || data[15] != 'R') return false;
            width = BigEndian(data, 16);
            height = BigEndian(data, 20);
            return width > 0 && height > 0;
        }

        private static int BigEndian(byte[] d, int at) => (d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3];
    }
}

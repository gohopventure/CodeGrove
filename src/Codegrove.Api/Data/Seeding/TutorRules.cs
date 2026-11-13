using System.Text.RegularExpressions;

namespace Codegrove.Api.Data.Seeding;

/// <summary>
/// The writing rules from the Avatar Tutor Character &amp; Behavior Spec, checked at seed time so a line that would
/// sound wrong read aloud (or break a rule) never reaches a learner. Each method returns an error message, or null.
/// The web app's tutorScripts.test.ts checks the same rules on the content files.
/// </summary>
public static partial class TutorRules
{
    /// <summary>
    /// Text a voice can't read naturally: code symbols, markdown, URLs, and abbreviations a voice spells out.
    /// Speech says "console dot log", "the h1 tag", "u-l", "C sharp" instead.
    /// </summary>
    [GeneratedRegex(@"[`<>{}\[\]*#_=|\\;]|https?:|://|\b(e\.g|i\.e|etc)\.", RegexOptions.IgnoreCase)]
    private static partial Regex NotSpoken();

    /// <summary>A sentence ends with . ! or ? followed by whitespace or the end of the text ("1.5" isn't an end).</summary>
    [GeneratedRegex(@"[.!?]+(?=\s|$)")]
    private static partial Regex SentenceEnd();

    public static int Sentences(string text) => SentenceEnd().Matches(text.Trim()).Count;

    /// <summary>Intro and recap length: Explorer 2–3 sentences (15–20 s spoken), Builder 3–4 (25–30 s).</summary>
    public static (int Min, int Max) ScriptSentences(AgeBand band) => band switch
    {
        AgeBand.Explorer => (2, 3),
        AgeBand.Builder => (3, 4),
        _ => (2, 4),
    };

    /// <summary>Every other line (hints, event lines): Explorer at most 2 sentences, Builder at most 3.</summary>
    public static int MaxLineSentences(AgeBand band) => band == AgeBand.Explorer ? 2 : 3;

    /// <summary>Checks a line meant to be read aloud.</summary>
    public static string? CheckSpeech(string? speech, int minSentences, int maxSentences)
    {
        if (string.IsNullOrWhiteSpace(speech)) return "needs speechText.";
        if (NotSpoken().Match(speech) is { Success: true } m)
            return $"speechText has \"{m.Value}\", which a voice can't read naturally: \"{speech}\"";
        var n = Sentences(speech);
        if (n < minSentences || n > maxSentences)
        {
            var range = minSentences == maxSentences ? $"{minSentences}" : minSentences <= 1 ? $"at most {maxSentences}" : $"{minSentences}–{maxSentences}";
            return $"speechText should be {range} sentences, not {n}: \"{speech}\"";
        }
        return null;
    }

    /// <summary>Checks a line shown in the tutor panel.</summary>
    public static string? CheckDisplay(string? display) =>
        string.IsNullOrWhiteSpace(display) ? "needs displayText." : display.Length > 1000 ? "displayText is over 1000 characters." : null;

    /// <summary>
    /// Hint rules. Level 1 is always a guiding question (ask before telling). No hint may contain a whole solution
    /// file: the full answer is a separate step the learner asks for after all three hints.
    /// </summary>
    public static string? CheckHint(int level, string markdown, string? speech, AgeBand band, IReadOnlyDictionary<string, string> solutionFiles)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return $"hint {level} is empty.";
        if (CheckSpeech(speech, 1, MaxLineSentences(band)) is { } bad) return $"hint {level}: {bad}";
        if (level == 1 && !(markdown.TrimEnd().EndsWith('?') && speech!.TrimEnd().EndsWith('?')))
            return "hint 1 must be a guiding question: end both markdown and speechText with a question mark.";

        var hint = Squash(markdown);
        foreach (var (name, code) in solutionFiles)
        {
            var file = Squash(code);
            if (file.Length >= 40 && hint.Contains(file, StringComparison.Ordinal))
                return $"hint {level} contains the whole solution {name}. Hints stop short of the answer.";
        }
        return null;
    }

    /// <summary>Whitespace-insensitive form, so reformatting a pasted solution doesn't hide it.</summary>
    private static string Squash(string s) => WhiteSpace().Replace(s, "");

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}

using System.Reflection;
using Codegrove.Api.Data;
using Codegrove.Api.Data.Entities;
using Codegrove.Api.Data.Seeding;
using Codegrove.Api.Services;

namespace Codegrove.Api.Tests;

public class ContentFilterTests
{
    private readonly ContentFilter _filter = new();

    [Theory]
    [InlineData("PixelFox42")]
    [InlineData("Byte_Runner")]
    [InlineData("ClassBass")]      // contains "ass" inside words: allowed
    [InlineData("GrapeSkill")]     // contains "rape" and "kill" inside words: allowed
    [InlineData("Essay_Writer")]
    public void Allows_ordinary_usernames(string name) =>
        Assert.True(_filter.CheckUsername(name).Ok, _filter.CheckUsername(name).Reason);

    [Theory]
    [InlineData("ab")]                  // too short
    [InlineData("has space")]
    [InlineData("Sam2014")]             // looks like a birth year
    [InlineData("Call5551234")]         // phone-like digit run
    [InlineData("sh1tlord")]            // leetspeak profanity
    [InlineData("Big_Ass")]             // blocked whole token
    public void Rejects_unsafe_usernames(string name) =>
        Assert.False(_filter.CheckUsername(name).Ok);

    [Theory]
    [InlineData("email me at kid@example.com")]
    [InlineData("call 555 123 4567")]
    [InlineData("see www.example.com")]
    [InlineData("I live at 42 Maple Street")]
    [InlineData("my name is Sam")]
    [InlineData("follow @coolkid")]
    public void Rejects_personal_information_in_text(string text) =>
        Assert.False(_filter.CheckText(text).Ok);

    [Fact]
    public void Every_generated_username_passes_the_filter()
    {
        // Reflection keeps the word lists private to the generator while still testing every combination.
        string[] Get(string field) => (string[])typeof(UsernameGenerator)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        foreach (var adjective in Get("Adjectives"))
        foreach (var animal in Get("Animals"))
        {
            var name = $"{adjective}{animal}42";
            Assert.True(_filter.CheckUsername(name).Ok, $"{name}: {_filter.CheckUsername(name).Reason}");
        }
    }
}

public class AgePolicyTests
{
    private static readonly DateOnly Today = new(2026, 11, 10);

    [Theory]
    [InlineData(2013, 10, 13, false)] // birthday month already passed: 13
    [InlineData(2013, 11, 12, true)]  // birthday month is this month: assume not yet, so 12
    [InlineData(2013, 12, 12, true)]
    [InlineData(2010, 1, 16, false)]
    public void Age_errs_younger_when_birthday_month_is_now(int year, int month, int expectedAge, bool under13)
    {
        Assert.Equal(expectedAge, AgePolicy.AgeInYears(year, month, Today));
        Assert.Equal(under13, AgePolicy.IsUnder13(year, month, Today));
    }

    [Fact]
    public void Default_band_splits_at_14() =>
        Assert.Equal((AgeBand.Explorer, AgeBand.Builder),
            (AgePolicy.DefaultBand(2013, 1, Today), AgePolicy.DefaultBand(2012, 1, Today)));
}

public class MotivationTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(49, 1)]
    [InlineData(50, 2)]
    [InlineData(149, 2)]
    [InlineData(150, 3)]
    [InlineData(300, 4)]
    public void Levels_need_50_more_xp_each(int xp, int level) => Assert.Equal(level, Levels.LevelFor(xp));

    [Fact]
    public void Streak_counts_full_weeks_and_never_breaks_on_the_current_week()
    {
        var today = new DateOnly(2026, 11, 11); // a Wednesday; week starts Mon Nov 9
        var days = new List<(DateOnly, int)>();
        void Active(int y, int m, int d) => days.Add((new DateOnly(y, m, d), 600));

        // Last week (Nov 2–8): 3 active days. The week before (Oct 26–Nov 1): 3 days. Before that: 1.
        Active(2026, 11, 2); Active(2026, 11, 4); Active(2026, 11, 6);
        Active(2026, 10, 26); Active(2026, 10, 28); Active(2026, 10, 30);
        Active(2026, 10, 20);
        // This week: 1 day so far, below the goal: must not reset the streak.
        Active(2026, 11, 9);

        var streak = Streaks.Calculate(days, goalDays: 3, today);

        Assert.Equal(2, streak.StreakWeeks);
        Assert.Equal(1, streak.DaysActiveThisWeek);
    }

    [Fact]
    public void Short_visits_do_not_count_as_active_days()
    {
        var today = new DateOnly(2026, 11, 11);
        var days = new[] { (new DateOnly(2026, 11, 9), 30), (new DateOnly(2026, 11, 10), 30), (new DateOnly(2026, 11, 11), 30) };
        Assert.Equal(0, Streaks.Calculate(days, 3, today).StreakWeeks);
    }
}

public class QuizGraderTests
{
    private static Quiz TwoQuestionQuiz() => new()
    {
        Id = 1, PassPercent = 60,
        Questions =
        [
            new QuizQuestion { Id = 10, SortOrder = 1, Explanation = "a", Options = [new() { Id = 100, IsCorrect = true }, new() { Id = 101 }] },
            new QuizQuestion { Id = 11, SortOrder = 2, Explanation = "b", Options = [new() { Id = 110 }, new() { Id = 111, IsCorrect = true }] },
        ],
    };

    [Fact]
    public void Half_right_fails_a_60_percent_quiz()
    {
        var grade = QuizGrader.Grade(TwoQuestionQuiz(), new Dictionary<int, int> { [10] = 100, [11] = 110 });
        Assert.Equal(50, grade.ScorePercent);
        Assert.False(grade.Passed);
        Assert.Equal([true, false], grade.Results.Select(r => r.Correct));
    }

    [Fact]
    public void An_option_from_another_question_is_not_accepted()
    {
        var grade = QuizGrader.Grade(TwoQuestionQuiz(), new Dictionary<int, int> { [10] = 111, [11] = 111 });
        Assert.Equal(50, grade.ScorePercent);
    }
}

public class TutorRulesTests
{
    private static readonly Dictionary<string, string> Solution = new() { ["index.html"] = "<h1>Welcome to my page</h1>\n<p>Otters hold hands while they sleep.</p>" };

    [Theory]
    [InlineData("Wrap it in u-l tags. Then add an l-i for each game.", null)]
    [InlineData("Use <ul> tags.", "\"<\"")]
    [InlineData("Add a semicolon; then run it.", "\";\"")]
    [InlineData("Try `console.log` next.", "\"`\"")]
    [InlineData("See https://example.com for more.", "\"https:\"")]
    [InlineData("Use a tag, e.g. the h1.", "\"e.g.\"")]
    public void Speech_must_be_plain_words(string speech, string? expected)
    {
        var error = TutorRules.CheckSpeech(speech, 1, 3);
        if (expected is null) Assert.Null(error);
        else Assert.Contains(expected, error);
    }

    [Theory]
    [InlineData("One. Two.", AgeBand.Explorer, true)]
    [InlineData("One. Two. Three. Four.", AgeBand.Explorer, false)]
    [InlineData("One. Two.", AgeBand.Builder, false)]
    [InlineData("One version 1.5 is out. Two! Three?", AgeBand.Builder, true)]
    public void Intro_length_depends_on_band(string speech, AgeBand band, bool ok)
    {
        var (min, max) = TutorRules.ScriptSentences(band);
        Assert.Equal(ok, TutorRules.CheckSpeech(speech, min, max) is null);
    }

    [Fact]
    public void Hint_one_must_be_a_question() =>
        Assert.NotNull(TutorRules.CheckHint(1, "Look at line 4.", "Look at line 4.", AgeBand.Explorer, Solution));

    [Fact]
    public void A_hint_cannot_contain_the_whole_solution() =>
        Assert.Contains("whole solution", TutorRules.CheckHint(3,
            "Here it is:\n```html\n<h1>Welcome to my page</h1>\n  <p>Otters hold hands while they sleep.</p>\n```",
            "Copy this.", AgeBand.Builder, Solution));

    [Fact]
    public void A_good_hint_ladder_passes()
    {
        Assert.Null(TutorRules.CheckHint(1, "What does every opening tag need a partner for?", "What does every opening tag need a partner for?", AgeBand.Explorer, Solution));
        Assert.Null(TutorRules.CheckHint(3, "Add `</p>` after the text on line 4.", "Add a closing p tag right after the text on line 4.", AgeBand.Explorer, Solution));
    }
}

public class PortfolioRulesTests
{
    private readonly ContentFilter _filter = new();

    private static Dictionary<string, string> Page(string head) => new() { ["web/index.html"] = $"<html><head>{head}</head></html>" };

    [Theory]
    [InlineData("<title>Junior League</title>", "Junior League")]
    [InlineData("<title>\n  Pip's   Clubs\n</title>", "Pip's Clubs")]
    [InlineData("<title>Owl &amp; Frog</title>", "Owl & Frog")]
    [InlineData("<title></title>", "Make it yours")]                       // empty: the lesson's name
    [InlineData("", "Make it yours")]                                       // no title
    [InlineData("<title>my name is Sam</title>", "Make it yours")]         // personal information never becomes the name
    [InlineData("<title>Visit www.example.com</title>", "Make it yours")]
    public void A_project_is_named_after_its_page_title_when_that_is_safe(string head, string expected) =>
        Assert.Equal(expected, PortfolioNames.FromPage(Page(head), "Make it yours", _filter));

    [Theory]
    [InlineData("<h1>Pip&apos;s Club Corner</h1>", "Pip's Club Corner")]              // the name they gave it wins
    [InlineData("<h1 className=\"big\">\n  Moon   League\n</h1>", "Moon League")]
    [InlineData("<h1>{name}</h1>", "Grove Club Hub")]                                 // built from code: use the title
    [InlineData("<h1>Club <em>Corner</em></h1>", "Grove Club Hub")]
    [InlineData("<h1>call 555 123 4567</h1>", "Grove Club Hub")]                      // filtered
    [InlineData("", "Grove Club Hub")]
    public void The_apps_own_heading_names_it_before_the_page_title(string heading, string expected)
    {
        var files = new Dictionary<string, string>
        {
            ["web/index.html"] = "<title>Grove Club Hub</title><div id=\"root\"></div>",
            ["web/NewClubForm.jsx"] = "<h2>Start a new club</h2>",
            ["web/App.jsx"] = $"export default function App() {{ return <main>{heading}</main>; }}",
            ["api/Program.cs"] = "// <h1>Not this</h1>",
        };
        Assert.Equal(expected, PortfolioNames.FromPage(files, "Make it yours", _filter));
    }

    [Fact]
    public void Long_titles_are_cut_to_the_limit() =>
        Assert.Equal(PortfolioEntry.MaxTitleLength, PortfolioNames.FromPage(Page($"<title>{new string('a', 90)}</title>"), "x", _filter).Length);

    [Theory]
    [InlineData("", "Your project needs a name.")]
    [InlineData("   ", "Your project needs a name.")]
    [InlineData("call 555 123 4567", null)]
    public void Typed_names_are_checked(string typed, string? reasonStart)
    {
        var (name, problem) = PortfolioNames.Validate(typed, _filter);
        Assert.Null(name);
        if (reasonStart is not null) Assert.Equal(reasonStart, problem);
        else Assert.NotNull(problem);
    }

    [Fact]
    public void Pictures_must_be_real_pngs_of_a_sensible_size()
    {
        static string Url(byte[] b) => "data:image/png;base64," + Convert.ToBase64String(b);
        var header = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        byte[] Png(uint w, uint h)
        {
            var b = new byte[40];
            header.CopyTo(b, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), w);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), h);
            return b;
        }

        Assert.NotNull(PortfolioImages.Read(Url(Png(1200, 750))).Png);
        Assert.Null(PortfolioImages.Read(Url(Png(5000, 750))).Png);                       // too wide
        Assert.Null(PortfolioImages.Read(Url(Png(0, 750))).Png);
        Assert.Null(PortfolioImages.Read("data:image/svg+xml;base64,PHN2Zz4=").Png);      // not a PNG URL
        Assert.Null(PortfolioImages.Read(Url("<svg onload=alert(1)>"u8.ToArray())).Png);   // PNG URL, not PNG bytes
        Assert.Null(PortfolioImages.Read("data:image/png;base64,***").Png);
        Assert.Equal("The picture is too big.", PortfolioImages.Read(Url(new byte[PortfolioEntry.MaxScreenshotBytes + 10])).Problem);
    }
}

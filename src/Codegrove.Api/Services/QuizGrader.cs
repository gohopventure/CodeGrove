using Codegrove.Api.Data.Entities;

namespace Codegrove.Api.Services;

public sealed record QuestionResult(int QuestionId, bool Correct, string Explanation);

public sealed record QuizGrade(int ScorePercent, bool Passed, IReadOnlyList<QuestionResult> Results);

/// <summary>
/// Grades a quiz on the server. The correct option is not returned: learners see whether each answer
/// was right plus the explanation, so a retry still means thinking, not copying.
/// </summary>
public static class QuizGrader
{
    public static QuizGrade Grade(Quiz quiz, IReadOnlyDictionary<int, int> answers)
    {
        if (quiz.Questions.Count == 0)
            return new QuizGrade(100, true, []);

        var results = quiz.Questions
            .OrderBy(q => q.SortOrder)
            .Select(q =>
            {
                var correct = answers.TryGetValue(q.Id, out var chosen)
                    && q.Options.Any(o => o.Id == chosen && o.IsCorrect);
                return new QuestionResult(q.Id, correct, q.Explanation);
            })
            .ToList();

        // Round down so "60% to pass" means at least 60%, never 59.5%.
        var score = (int)Math.Floor(100.0 * results.Count(r => r.Correct) / results.Count);
        return new QuizGrade(score, score >= quiz.PassPercent, results);
    }
}

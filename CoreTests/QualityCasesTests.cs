using System.Text.Json;
using ModelFitStudio.Core;
using Xunit;

namespace CoreTests;

/// <summary>
/// One quality test for two apps. zer0ne code asks the same questions and marks them
/// the same way; quality-cases.json is the list both must match, so a score means
/// the same in either app.
/// </summary>
public class QualityCasesTests
{
    private static string CasesPath => Path.Combine(AppContext.BaseDirectory, "quality-cases.json");
    private static readonly JsonElement Cases = JsonDocument.Parse(File.ReadAllText(CasesPath)).RootElement;

    private static AnswerKind Kind(string k) => k switch
    {
        "number" => AnswerKind.Number,
        "word" => AnswerKind.Word,
        "choice" => AnswerKind.Choice,
        "exact" => AnswerKind.Exact,
        "exactCase" => AnswerKind.ExactCase,
        "list" => AnswerKind.List,
        _ => throw new ArgumentException(k),
    };

    [Fact]
    public void The_questions_are_the_same_in_both_apps()
    {
        Assert.Equal(Cases.GetProperty("ending").GetString(), QualityQuestions.Ending);
        var listed = Cases.GetProperty("questions").EnumerateArray()
            .Select(q => (q.GetProperty("skill").GetString(), q.GetProperty("prompt").GetString(),
                          q.GetProperty("answer").GetString(), Kind(q.GetProperty("kind").GetString()!)))
            .ToList();
        Assert.Equal(QualityQuestions.All.Select(q => ((string?)q.Skill, (string?)q.Prompt, (string?)q.Answer, q.Kind)), listed);
    }

    [Fact]
    public void Replies_are_marked_the_same_in_both_apps()
    {
        foreach (var g in Cases.GetProperty("grading").EnumerateArray())
        {
            var reply = g.GetProperty("reply").GetString();
            var q = new QualityQuestion("Test", "?", g.GetProperty("answer").GetString()!, Kind(g.GetProperty("kind").GetString()!));
            Assert.True(g.GetProperty("right").GetBoolean() == QualityQuestions.Grade(q, reply), $"{q.Kind} {q.Answer}: {reply}");
        }
    }

    [Fact]
    public void Both_copies_of_the_cases_are_the_same_file()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "zer0ne_code"))) dir = dir.Parent;
        if (dir is null) return;
        var other = Path.Combine(dir.FullName, "zer0ne_code", "engine", "test", "fixtures", "quality-cases.json");
        if (!File.Exists(other)) return;
        Assert.Equal(File.ReadAllText(other).ReplaceLineEndings("\n"), File.ReadAllText(CasesPath).ReplaceLineEndings("\n"));
    }
}

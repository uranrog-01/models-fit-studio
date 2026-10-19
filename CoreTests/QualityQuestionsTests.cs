using ModelFitStudio.Core;
using Xunit;

namespace CoreTests;

/// <summary>
/// The quality test is only as honest as its marking: a right answer written in an
/// ordinary way must count, and a wrong one must not slip through on a loose match.
/// </summary>
public class QualityQuestionsTests
{
    private static QualityQuestion Q(AnswerKind kind, string answer) => new("Test", "?", answer, kind);

    [Fact]
    public void The_Test_Has_Forty_Questions_Eight_Per_Skill()
    {
        Assert.Equal(40, QualityQuestions.All.Count);
        Assert.All(QualityQuestions.All.GroupBy(q => q.Skill), g => Assert.Equal(8, g.Count()));
    }

    [Fact]
    public void Every_Question_Accepts_Its_Own_Answer()
    {
        Assert.All(QualityQuestions.All, q => Assert.True(QualityQuestions.Grade(q, q.Answer), q.Prompt));
    }

    [Theory]
    [InlineData("43", true)]
    [InlineData("17 + 26 = 43.", true)]
    [InlineData("The answer is 43", true)]
    [InlineData("1,275", false)]
    [InlineData("44", false)]
    [InlineData("", false)]
    public void A_Number_Is_The_Last_Number_In_The_Reply(string reply, bool right)
    {
        Assert.Equal(right, QualityQuestions.Grade(Q(AnswerKind.Number, "43"), reply));
    }

    [Fact]
    public void A_Number_May_Carry_Thousands_Separators()
    {
        Assert.True(QualityQuestions.Grade(Q(AnswerKind.Number, "1275"), "1,275"));
    }

    [Theory]
    [InlineData("Yes.", true)]
    [InlineData("yes, every bloop is a lazzie", true)]
    [InlineData("No", false)]
    [InlineData("Yes and no", false)]
    public void Yes_Must_Not_Come_With_A_No(string reply, bool right)
    {
        Assert.Equal(right, QualityQuestions.Grade(Q(AnswerKind.Word, "yes"), reply));
    }

    [Theory]
    [InlineData("B", true)]
    [InlineData("B) Mercury", true)]
    [InlineData("Answer: b", true)]
    [InlineData("**B**", true)]
    [InlineData("C", false)]
    [InlineData("Mercury", false)]
    public void A_Choice_Is_A_Letter_Standing_Alone(string reply, bool right)
    {
        Assert.Equal(right, QualityQuestions.Grade(Q(AnswerKind.Choice, "B"), reply));
    }

    [Theory]
    [InlineData("tenalp", true)]
    [InlineData("\"Tenalp\".", true)]
    [InlineData("The word backwards is tenalp.", true)]
    [InlineData("planet", false)]
    [InlineData("tenalp is not planet", false)]
    public void An_Exact_Answer_Is_The_Final_Answer_Or_Its_Last_Word(string reply, bool right)
    {
        Assert.Equal(right, QualityQuestions.Grade(Q(AnswerKind.Exact, "tenalp"), reply));
    }

    [Fact]
    public void Capital_Letters_Are_Checked_When_They_Are_The_Task()
    {
        Assert.True(QualityQuestions.Grade(Q(AnswerKind.ExactCase, "QUIET RIVER"), "QUIET RIVER"));
        Assert.False(QualityQuestions.Grade(Q(AnswerKind.ExactCase, "QUIET RIVER"), "Quiet River"));
    }

    [Fact]
    public void A_List_Is_Its_Numbers_In_Order()
    {
        Assert.True(QualityQuestions.Grade(Q(AnswerKind.List, "3,7,19,42"), "3, 7, 19, 42"));
        Assert.False(QualityQuestions.Grade(Q(AnswerKind.List, "3,7,19,42"), "42, 19, 7, 3"));
    }

    [Theory]
    // what a 0.6B model really wrote, and what it settled on
    [InlineData("23 x 17 = 391\n\nFinal answer: 391.", "391.")]
    [InlineData("The word **lantern** is repeated.\n\nFinal answer: **Final answer: lantern**.", "lantern**.")]
    [InlineData("**Output:**\n```\nBCA\n```\n\n### Final Answer:\n```\nBCA\n```", "BCA")]
    [InlineData("So the output is `10`.", "So the output is `10`.")]
    [InlineData("The answer is 43", "43")]
    public void The_Final_Answer_Is_What_Follows_The_Last_Answer_Marker(string reply, string expected)
    {
        Assert.Equal(expected, QualityQuestions.FinalAnswer(reply));
    }

    [Fact]
    public void Reasoning_Before_The_Final_Line_Does_Not_Count_Against_A_Model()
    {
        var q = Q(AnswerKind.Number, "4");
        Assert.True(QualityQuestions.Grade(q, "2c + 4w = 28 and c + w = 10, so w = 4 and c = 6.\n\nFinal answer: 4 cows."));
        Assert.False(QualityQuestions.Grade(q, "c + w = 10...\n\nFinal answer: 6 cows."));
    }

    [Fact]
    public void Thinking_Written_Before_The_Answer_Is_Set_Aside()
    {
        Assert.True(QualityQuestions.Grade(Q(AnswerKind.Number, "43"), "<think>17 + 26... maybe 44? no</think>\n43"));
        // cut off inside its thinking: there is no answer to mark
        Assert.False(QualityQuestions.Grade(Q(AnswerKind.Number, "43"), "<think>17 + 26 is 43"));
    }
}

using System.Text.Json.Nodes;
using ModelFitStudio.Core;
using Xunit;

namespace CoreTests;

/// <summary>"Best for agents": the tasks, how an answer is graded, and the points.</summary>
public class AgentCasesTests
{
    private static AgentCases.Case Find(string ask) => AgentCases.All.First(c => c.Ask.StartsWith(ask));

    [Fact]
    public void There_are_ten_tasks_and_two_need_no_tool()
    {
        Assert.Equal(10, AgentCases.All.Length);
        Assert.Equal(2, AgentCases.All.Count(c => c.Tool is null));
        var names = AgentCases.Tools().Select(t => t!["function"]!["name"]!.ToString()).ToHashSet();
        Assert.All(AgentCases.All.Where(c => c.Tool is not null), c => Assert.Contains(c.Tool!, names));
    }

    [Fact]
    public void The_right_tool_with_the_right_detail_is_right()
    {
        var readme = Find("Open README.md");
        Assert.True(AgentCases.Grade(readme, "read_file", "{\"path\":\"./README.md\"}", null));
        Assert.False(AgentCases.Grade(readme, "read_file", "{\"path\":\"package.json\"}", null));   // wrong file
        Assert.False(AgentCases.Grade(readme, "run_shell", "{\"command\":\"cat README.md\"}", null)); // wrong tool
        Assert.False(AgentCases.Grade(readme, null, null, "The README says…"));                     // no call
        Assert.False(AgentCases.Grade(readme, "read_file", "not json", null));
        Assert.True(AgentCases.Grade(Find("Run the unit tests"), "run_shell", "{\"command\":\"npm run test\"}", null));
    }

    [Fact]
    public void A_task_that_needs_no_tool_is_answered_in_words()
    {
        var paris = Find("What is the capital");
        Assert.True(AgentCases.Grade(paris, null, null, "The capital of France is Paris."));
        Assert.False(AgentCases.Grade(paris, "search_code", "{\"query\":\"capital\"}", null));
        Assert.False(AgentCases.Grade(paris, null, null, "I don't know."));
        Assert.True(AgentCases.Grade(Find("Thanks"), null, null, "You're welcome!"));
    }

    [Fact]
    public void The_request_is_an_openai_chat_with_the_tools()
    {
        var req = AgentCases.Request(AgentCases.All[0]);
        Assert.Equal(5, ((JsonArray)req["tools"]!).Count);
        Assert.Equal("system", req["messages"]![0]!["role"]!.ToString());
        Assert.Equal(AgentCases.All[0].Ask, req["messages"]![1]!["content"]!.ToString());
    }

    [Theory]
    [InlineData(10, 10, 5, 40, 100)]
    [InlineData(10, 10, 60, 40, 70)]
    [InlineData(5, 10, 5, 0, 60)]
    [InlineData(0, 10, 32.5, 20, 20)]
    public void Points_weigh_tools_then_reading_then_writing(int correct, int total, double read, double write, int points)
    {
        Assert.Equal(points, AgentCases.Points(correct, total, read, write));
    }

    [Fact]
    public void The_long_prompt_is_about_the_asked_length_in_words()
    {
        var words = AgentCases.LongPrompt(12_000).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.InRange(words, 5000, 6000);
    }
}

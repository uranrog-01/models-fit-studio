using System.Text.Json;
using System.Text.Json.Nodes;

namespace ModelFitStudio.Core;

/// <summary>
/// "Best for agents": what is asked of a model behind an AI agent, and how it is scored. Ten small
/// agent tasks with five tools, each with one right tool and the details it must carry (two need no
/// tool at all), plus how quickly it reads a 12,000-token request, which is most of an agent's wait
/// on a PC like this. Pure: the runner is <c>AgentTest</c>.
/// </summary>
public static class AgentCases
{
    public const int ReadTokens = 12_000;

    public const string System =
        "You are a coding agent working in the user's project folder. When a request needs a file, a command or a search, " +
        "call the one tool that does it. When it needs none, answer in one short sentence.";

    /// <param name="Tool">The tool that must be called, or null when the answer needs none.</param>
    /// <param name="Arg">The argument that must carry <paramref name="Contains"/> (any of the words, case ignored).</param>
    public sealed record Case(string Ask, string? Tool, string? Arg, string[] Contains);

    public static readonly Case[] All =
    [
        new("Open README.md and tell me what the project is.", "read_file", "path", ["readme"]),
        new("Run the unit tests with npm test.", "run_shell", "command", ["npm test", "npm run test"]),
        new("Find where the function parseConfig is defined.", "search_code", "query", ["parseconfig"]),
        new("Create a file named notes.txt that says hello.", "write_file", "path", ["notes.txt"]),
        new("Which files are in the src folder?", "list_files", "path", ["src"]),
        new("What is the capital of France?", null, null, ["paris"]),
        new("Show me what is in package.json.", "read_file", "path", ["package.json"]),
        new("Search the code for TODO comments.", "search_code", "query", ["todo"]),
        new("Install the dependencies with npm install.", "run_shell", "command", ["npm install", "npm i"]),
        new("Thanks, that is all for now.", null, null, []),
    ];

    public static JsonArray Tools()
    {
        static JsonObject Fn(string name, string description, params (string arg, string what)[] args)
        {
            var props = new JsonObject();
            foreach (var (arg, what) in args) props[arg] = new JsonObject { ["type"] = "string", ["description"] = what };
            return new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = name,
                    ["description"] = description,
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = props,
                        ["required"] = new JsonArray(args.Select(a => (JsonNode)JsonValue.Create(a.arg)!).ToArray()),
                    },
                },
            };
        }
        return
        [
            Fn("read_file", "Read a text file of the project.", ("path", "The file's path")),
            Fn("write_file", "Create or replace a file of the project.", ("path", "The file's path"), ("content", "The whole new text")),
            Fn("run_shell", "Run a shell command in the project folder.", ("command", "The command line")),
            Fn("search_code", "Search the project's files for text.", ("query", "The text to look for")),
            Fn("list_files", "List the files in a folder of the project.", ("path", "The folder")),
        ];
    }

    /// <summary>The chat request for one case, OpenAI style.</summary>
    public static JsonObject Request(Case c) => new()
    {
        ["model"] = "agent-test",
        ["max_tokens"] = 300,
        ["temperature"] = 0,
        ["tools"] = Tools(),
        ["messages"] = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = System },
            new JsonObject { ["role"] = "user", ["content"] = c.Ask }),
    };

    /// <summary>Right when the expected tool is called with the expected detail, or no tool when none is needed.</summary>
    public static bool Grade(Case c, string? calledTool, string? argumentsJson, string? text)
    {
        if (c.Tool is null)
        {
            if (!string.IsNullOrEmpty(calledTool)) return false;
            return c.Contains.Length == 0 || c.Contains.Any(w => (text ?? "").Contains(w, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.Equals(calledTool, c.Tool, StringComparison.Ordinal)) return false;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (c.Arg is null) return true;
            if (!doc.RootElement.TryGetProperty(c.Arg, out var v)) return false;
            var s = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
            return c.Contains.Any(w => s.Contains(w, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>A prompt of about <paramref name="tokens"/> tokens, made of plain words so no tokenizer is needed.</summary>
    public static string LongPrompt(int tokens)
    {
        string[] words = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa", "lambda", "sigma"];
        // each word with its digit reads as about two tokens on the tokenizers measured
        var n = (int)(tokens * 0.46);
        var sb = new System.Text.StringBuilder(n * 8);
        for (var i = 0; i < n; i++) sb.Append(words[i % words.Length]).Append(i % 7).Append(' ');
        return sb.ToString();
    }

    /// <summary>
    /// Points out of 100: the right tool matters most (60), then how fast an agent request is read (30:
    /// full at 5 s for 12,000 tokens, none at 60 s), then writing speed (10: full at 40 tokens a second).
    /// </summary>
    public static int Points(int correct, int total, double readSeconds, double writeTps)
    {
        var tools = total > 0 ? (double)correct / total : 0;
        var read = readSeconds > 0 ? Math.Clamp((60 - readSeconds) / 55, 0, 1) : 0;
        var write = Math.Clamp(writeTps / 40, 0, 1);
        return (int)Math.Round(tools * 60 + read * 30 + write * 10);
    }
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace ModelFitStudio.Core;

/// <summary>How an answer is checked: every question has one right answer a program can recognise.</summary>
public enum AnswerKind
{
    /// <summary>The last number in the final answer.</summary>
    Number,
    /// <summary>The final answer contains the word (and, for yes/no, not its opposite).</summary>
    Word,
    /// <summary>One letter, A to D.</summary>
    Choice,
    /// <summary>The final answer, or its last word, ignoring case, quotes and punctuation.</summary>
    Exact,
    /// <summary>The final answer, case included.</summary>
    ExactCase,
    /// <summary>The numbers of the final answer, in order.</summary>
    List,
}

public sealed record QualityQuestion(string Skill, string Prompt, string Answer, AnswerKind Kind);

/// <summary>
/// The quality test: 40 short questions in five skills, each with one answer that
/// can be checked without judgement. The model may reason first — forbidding that
/// measures something else, and small models then fail questions they can solve —
/// and what is marked is its final line. The test is small on purpose (a few
/// minutes on a laptop): it ranks models and shows what a heavy quant costs; it is
/// not a replacement for the large public benchmarks.
/// </summary>
public static class QualityQuestions
{
    public const string Reasoning = "Reasoning";
    public const string Maths = "Math";
    public const string Knowledge = "Knowledge";
    public const string Instructions = "Instructions";
    public const string Code = "Code";

    /// <summary>Added to every question. Tried against a 0.6B model: an example line ("Answer: …") gets copied, this wording does not.</summary>
    public const string Ending = "\n\nThink it through, then end with a short last line that begins with \"Final answer:\".";

    public static readonly IReadOnlyList<QualityQuestion> All =
    [
        // ---- reasoning
        new(Reasoning, "Alice is older than Bob. Carol is younger than Bob. Dave is older than Alice. Who is the second oldest?", "Alice", AnswerKind.Word),
        new(Reasoning, "All bloops are razzies. All razzies are lazzies. Is every bloop a lazzie? Yes or no.", "yes", AnswerKind.Word),
        new(Reasoning, "A farm has chickens and cows. Together they have 10 heads and 28 legs. How many cows are there?", "4", AnswerKind.Number),
        new(Reasoning, "What is the next number in the sequence: 2, 6, 12, 20, 30, ?", "42", AnswerKind.Number),
        new(Reasoning, "Anna's mother has three daughters. Two of them are named April and May. What is the name of the third daughter?", "Anna", AnswerKind.Word),
        new(Reasoning, "A clock shows 3:15. What is the angle in degrees between the hour hand and the minute hand?", "7.5", AnswerKind.Number),
        new(Reasoning, "I have 3 boxes. Each box holds 4 bags. Each bag holds 5 marbles. I give away 2 bags. How many marbles do I have left?", "50", AnswerKind.Number),
        new(Reasoning, "If yesterday was Thursday, what day is the day after tomorrow?", "Sunday", AnswerKind.Word),

        // ---- math
        new(Maths, "What is 37 times 48?", "1776", AnswerKind.Number),
        new(Maths, "What is 17 squared minus 13 squared?", "120", AnswerKind.Number),
        new(Maths, "A recipe needs 3 eggs for 8 pancakes. How many eggs are needed for 40 pancakes?", "15", AnswerKind.Number),
        new(Maths, "What is the least common multiple of 12 and 18?", "36", AnswerKind.Number),
        new(Maths, "The average of five numbers is 12. Four of them are 10, 14, 9 and 15. What is the fifth number?", "12", AnswerKind.Number),
        new(Maths, "A price of 2000 rises by 10% and then falls by 10%. What is the final price?", "1980", AnswerKind.Number),
        new(Maths, "How many seconds are there in 2.5 hours?", "9000", AnswerKind.Number),
        new(Maths, "Solve for x: 2(x - 3) + 4 = 3x - 7.", "5", AnswerKind.Number),

        // ---- knowledge
        new(Knowledge, "Which element has the atomic number 6?\nA) Oxygen\nB) Nitrogen\nC) Carbon\nD) Boron", "C", AnswerKind.Choice),
        new(Knowledge, "Which of these is a prime number?\nA) 97\nB) 91\nC) 87\nD) 93", "A", AnswerKind.Choice),
        new(Knowledge, "About how long does light take to travel from the Sun to the Earth?\nA) 8 seconds\nB) 8 minutes\nC) 8 hours\nD) 8 days", "B", AnswerKind.Choice),
        new(Knowledge, "Which sorting algorithm has an average time complexity of O(n log n)?\nA) Bubble sort\nB) Insertion sort\nC) Selection sort\nD) Merge sort", "D", AnswerKind.Choice),
        new(Knowledge, "In which organ of the human body is insulin produced?\nA) Liver\nB) Pancreas\nC) Kidney\nD) Spleen", "B", AnswerKind.Choice),
        new(Knowledge, "Which treaty ended the First World War?\nA) Treaty of Versailles\nB) Treaty of Vienna\nC) Treaty of Paris\nD) Treaty of Tordesillas", "A", AnswerKind.Choice),
        new(Knowledge, "What does the HTTP status code 404 mean?\nA) Server error\nB) Unauthorized\nC) Moved permanently\nD) Not found", "D", AnswerKind.Choice),
        new(Knowledge, "What is the capital of Australia?\nA) Sydney\nB) Melbourne\nC) Canberra\nD) Perth", "C", AnswerKind.Choice),

        // ---- following instructions
        new(Instructions, "Write the letters of the word \"stream\" in alphabetical order, as one word.", "aemrst", AnswerKind.Exact),
        new(Instructions, "Write the word \"planet\" backwards.", "tenalp", AnswerKind.Exact),
        new(Instructions, "How many letters are in the word \"elephant\"?", "8", AnswerKind.Number),
        new(Instructions, "Sort these numbers from smallest to largest, separated by commas: 42, 7, 19, 3", "3,7,19,42", AnswerKind.List),
        new(Instructions, "Write this in capital letters: quiet river", "QUIET RIVER", AnswerKind.ExactCase),
        new(Instructions, "What is the third word of this sentence: The quick brown fox jumps.", "brown", AnswerKind.Exact),
        new(Instructions, "How many vowels (a, e, i, o, u) are in the word \"education\"?", "5", AnswerKind.Number),
        new(Instructions, "Join the first letter of each word into one word: Big Red Apple Tree", "BRAT", AnswerKind.Exact),

        // ---- reading code
        new(Code, "What does this Python code print?\n\ndef f(n):\n    return n * f(n - 1) if n > 1 else 1\n\nprint(f(5))", "120", AnswerKind.Number),
        new(Code, "What does this Python code print?\n\nd = {\"a\": 1, \"b\": 2}\nd[\"c\"] = d[\"a\"] + d[\"b\"]\nprint(len(d) + d[\"c\"])", "6", AnswerKind.Number),
        new(Code, "What does this Python code print?\n\ncount = 0\nfor i in range(10):\n    if i % 3 == 0:\n        count += 1\nprint(count)", "4", AnswerKind.Number),
        new(Code, "What does this Python code print?\n\nx = [3, 1, 2]\ny = x\ny.append(4)\nx.sort()\nprint(y[0] + len(x))", "5", AnswerKind.Number),
        new(Code, "What does this Python code print?\n\ndef g(a, b=[]):\n    b.append(a)\n    return len(b)\n\nprint(g(1) + g(2))", "3", AnswerKind.Number),
        new(Code, "What does this Python code print?\n\ns = \"hello world\"\nprint(s.split()[1][::-1][:3])", "dlr", AnswerKind.Exact),
        new(Code, "What does this JavaScript code print?\n\nconsole.log([10, 9, 1].sort()[1]);", "10", AnswerKind.Number),
        new(Code, "What does this Python code print?\n\nprint(sum(i * i for i in range(1, 5)))", "30", AnswerKind.Number),
    ];

    /// <summary>
    /// What the model settled on: the text after the last "answer" marker, else its
    /// last line. Thinking written in tags, code fences and markdown decoration are
    /// set aside first.
    /// </summary>
    public static string FinalAnswer(string? reply)
    {
        var text = Regex.Replace(reply ?? "", @"<think>.*?</think>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        // a reply cut off inside its thinking has no answer at all
        var open = text.LastIndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (open >= 0) text = text[..open];

        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !Regex.IsMatch(l, @"^[`$#*\-\s]*(python|js|javascript|text)?$", RegexOptions.IgnoreCase))
            .ToList();
        if (lines.Count == 0) return "";

        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var m = Regex.Match(lines[i], @"^.*\banswer\b\s*(?:is)?\s*[:：]?\s*(.*)$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var rest = m.Groups[1].Value.Trim(' ', '*', '`', '$');
            if (rest.Length > 0) return rest;
            if (i + 1 < lines.Count) return lines[i + 1];   // "Final answer:" with the answer on the next line
        }
        return lines[^1];
    }

    /// <summary>True when the model's final answer is the right one.</summary>
    public static bool Grade(QualityQuestion q, string? reply)
    {
        var text = FinalAnswer(reply);
        if (text.Length == 0) return false;

        switch (q.Kind)
        {
            case AnswerKind.Number:
            {
                var nums = Regex.Matches(text.Replace(",", ""), @"-?\d+(?:\.\d+)?");
                return nums.Count > 0 &&
                       double.TryParse(nums[^1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var got) &&
                       double.TryParse(q.Answer, NumberStyles.Float, CultureInfo.InvariantCulture, out var want) &&
                       Math.Abs(got - want) < 1e-6;
            }
            case AnswerKind.Word:
            {
                bool Has(string w) => Regex.IsMatch(text, $@"\b{Regex.Escape(w)}\b", RegexOptions.IgnoreCase);
                if (q.Answer.Equals("yes", StringComparison.OrdinalIgnoreCase)) return Has("yes") && !Has("no");
                if (q.Answer.Equals("no", StringComparison.OrdinalIgnoreCase)) return Has("no") && !Has("yes");
                return Has(q.Answer);
            }
            case AnswerKind.Choice:
            {
                // "B", "B)", "B) Mercury", "**B**" — the first letter that stands alone
                var m = Regex.Match(text, @"(?<![A-Za-z])([A-Da-d])(?![A-Za-z])");
                return m.Success && m.Groups[1].Value.Equals(q.Answer, StringComparison.OrdinalIgnoreCase);
            }
            case AnswerKind.Exact:
            {
                if (Squash(text).Equals(q.Answer, StringComparison.OrdinalIgnoreCase)) return true;
                // "The word backwards is tenalp." — the last word is the answer given
                var words = Regex.Matches(text, @"[A-Za-z0-9]+");
                return words.Count > 0 && words[^1].Value.Equals(q.Answer, StringComparison.OrdinalIgnoreCase);
            }
            case AnswerKind.ExactCase:
                return text.Trim(' ', '"', '\'', '.', '!', '*', '`').Equals(q.Answer, StringComparison.Ordinal);
            case AnswerKind.List:
                return string.Join(",", Regex.Matches(text, @"-?\d+").Select(x => x.Value)) == q.Answer;
            default:
                return false;
        }
    }

    private static string Squash(string s) => Regex.Replace(s, @"[^A-Za-z0-9]", "");
}

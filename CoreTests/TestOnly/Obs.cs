using System.IO;
using System.Text;

namespace CoreTests.TestOnly;

/// <summary>
/// Appends the actual observed status/body of every test to observed.log so the
/// verification report can quote real output even for passing tests
/// (`dotnet test -v q` swallows ITestOutputHelper on success).
/// Test-only helper.
/// </summary>
public static class Obs
{
    private static readonly object Gate = new();

    public static string LogPath { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "observed.log");

    public static void Log(string test, string detail)
    {
        lock (Gate)
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {test}\n    {detail}\n",
                Encoding.UTF8);
        }
    }

    public static void Reset() { lock (Gate) { try { File.Delete(LogPath); } catch { } } }
}

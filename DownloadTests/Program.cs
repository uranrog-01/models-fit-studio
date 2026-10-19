using System.Security.Cryptography;
using DownloadTests;
using ModelFitStudio.Core;

var failures = 0;
var rnd = new Random(42);

void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail.Length == 0 ? "" : $"  — {detail}")}");
    if (!ok) failures++;
}

static string HashOf(string path) =>
    path.Length == 0 ? "" : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

// fresh store root per run so old files cannot leak between scenarios
var root = Path.Combine(Path.GetTempPath(), "mfs-dl-tests", Guid.NewGuid().ToString("N"));
ModelStore.SetTestRoot(root);
const string repo = "testorg/testrepo";
const string file = "model.gguf";

using var server = new TestServer();
await server.StartAsync();
ModelStore.DownloadUrlOverride = (_, _) => server.Url;

// ---- 1. plain download completes and is byte-identical ------------------
server.SetPayload(new byte[5 * 1024 * 1023 + 777]);
rnd.NextBytes(server.Payload);
var p = await ModelStore.DownloadAsync(repo, file, new DownloadProgress { Repo = repo, File = file });
var dest = ModelStore.ResolvePath(repo, file);
Check("1. fresh download completes", p.State == DownloadState.Done && File.Exists(dest));
Check("1. bytes are identical (SHA-256)", HashOf(dest) == Sha(server.Payload));
Check("1. no .part or meta left behind", !File.Exists(dest + ".part") && !File.Exists(dest + ".part.meta"));

// ---- 2. pause mid-stream, then resume ------------------------------------
server.SetPayload(new byte[4 * 1024 * 1024 + 333]);
rnd.NextBytes(server.Payload);
server.SlowMs = 30; // slow pipe so the pause lands mid-stream, not post-completion
ModelStore.Delete(repo, file);
var p2 = new DownloadProgress { Repo = repo, File = file };
using (var cts = new CancellationTokenSource())
{
    var task = ModelStore.DownloadAsync(repo, file, p2, cts.Token);
    while (p2.Received < 1_000_000) await Task.Delay(20); // some bytes in
    p2.PauseRequested = true;
    cts.Cancel();
    await task;
}
Check("2. pause stops the transfer", p2.State == DownloadState.Paused);
Check("2. the .part keeps every byte received", File.Exists(dest + ".part") && new FileInfo(dest + ".part").Length == p2.Received);
var savedBytes = p2.Received;
var p2r = new DownloadProgress { Repo = repo, File = file };
p2r = await ModelStore.DownloadAsync(repo, file, p2r);
Check("2. resume completes from the saved part", p2r.State == DownloadState.Done, p2r.Error ?? "");
Check("2. resumed file is byte-identical", HashOf(dest) == Sha(server.Payload));
Check("2. the second attempt actually skipped ahead",
    p2r.Received >= savedBytes && savedBytes > 0);

// ---- 3. connection drop mid-stream auto-retries and resumes --------------
server.SetPayload(new byte[3 * 1024 * 1024 + 11]);
rnd.NextBytes(server.Payload);
ModelStore.Delete(repo, file);
server.DropNext = 2; // drop the first two connections abruptly
var p3 = await ModelStore.DownloadAsync(repo, file, new DownloadProgress { Repo = repo, File = file });
Check("3. dropped connections are survived", p3.State == DownloadState.Done, p3.Error ?? "");
Check("3. final file is byte-identical", HashOf(dest) == Sha(server.Payload));

// ---- 4. remote file changed mid-download → clean restart, no corruption --
server.SetPayload(new byte[2 * 1024 * 1024 + 55]);
rnd.NextBytes(server.Payload);
server.SlowMs = 30;
ModelStore.Delete(repo, file);
var p4 = new DownloadProgress { Repo = repo, File = file };
using (var cts = new CancellationTokenSource())
{
    var task = ModelStore.DownloadAsync(repo, file, p4, cts.Token);
    while (p4.Received < 800_000) await Task.Delay(20);
    p4.PauseRequested = true;
    cts.Cancel();
    await task;
}
var oldEtagFile = File.Exists(dest + ".part.meta");
var changed = new byte[2 * 1024 * 1024 + 55];
rnd.NextBytes(changed);
server.SetPayload(changed); // same URL, different content (new ETag)
var p4r = await ModelStore.DownloadAsync(repo, file, new DownloadProgress { Repo = repo, File = file });
Check("4. changed remote restarts cleanly", p4r.State == DownloadState.Done, p4r.Error ?? "");
Check("4. result matches the NEW payload", HashOf(dest) == Sha(changed));
Check("4. the ETag was actually stored for If-Range", oldEtagFile, "no .meta sidecar");

// ---- 5. server ignores Range → must not append (corruption guard) --------
server.SlowMs = 0;
server.SetPayload(new byte[1024 * 1024 + 99]);
rnd.NextBytes(server.Payload);
ModelStore.Delete(repo, file);
File.WriteAllBytes(dest + ".part", server.Payload[..100_000]);
File.WriteAllText(dest + ".part.meta", server.Etag);
server.IgnoreRange = true;
var p5 = await ModelStore.DownloadAsync(repo, file, new DownloadProgress { Repo = repo, File = file });
server.IgnoreRange = false;
Check("5. range-less server still completes", p5.State == DownloadState.Done, p5.Error ?? "");
Check("5. file is NOT corrupted by appending", HashOf(dest) == Sha(server.Payload));

ModelStore.Delete(repo, file);
ModelStore.ChunkBytes = 256 * 1024;
var p7 = await ModelStore.DownloadAsync(repo, file, new DownloadProgress { Repo = repo, File = file });
Check("7. multiple ranges finish with the exact advertised total", p7.State == DownloadState.Done && p7.Total == server.Payload.Length, p7.Error ?? "");
Check("7. ranged file is byte-identical", HashOf(dest) == Sha(server.Payload));

ModelStore.IdleTimeout = TimeSpan.FromMilliseconds(250);
ModelStore.HeaderTimeout = TimeSpan.FromMilliseconds(500);
foreach (var headerStall in new[] { false, true })
{
    ModelStore.Delete(repo, file);
    if (headerStall) server.StallHeadersNext = 1;
    else server.StallNext = 1;
    var recovered = await ModelStore.DownloadAsync(repo, file, new DownloadProgress { Repo = repo, File = file });
    Check($"8. stalled {(headerStall ? "headers" : "body")} reconnect automatically", recovered.State == DownloadState.Done, recovered.Error ?? "");
    Check("8. recovered file is byte-identical", HashOf(dest) == Sha(server.Payload));
}
ModelStore.HeaderTimeout = TimeSpan.FromSeconds(30);
ModelStore.IdleTimeout = TimeSpan.FromSeconds(45);
ModelStore.Delete(repo, file);
server.WrongRangeNext = 1;
var p9 = await ModelStore.DownloadAsync(repo, file, new DownloadProgress { Repo = repo, File = file });
Check("9. invalid Content-Range retries without corrupting the file", p9.State == DownloadState.Done && HashOf(dest) == Sha(server.Payload), p9.Error ?? "");
ModelStore.ChunkBytes = 32L * 1024 * 1024;

// ---- 6. the production URL against the real Hugging Face CDN (opt-in) ----
if (args.Contains("--hf"))
{
    ModelStore.DownloadUrlOverride = null; // the real https://huggingface.co/... URL
    var hfRoot = Path.Combine(Path.GetTempPath(), "mfs-dl-tests", "hf-" + Guid.NewGuid().ToString("N")[..8]);
    ModelStore.SetTestRoot(hfRoot);
    const string hfRepo = "unsloth/Qwen3-0.6B-GGUF";
    const string hfFile = "README.md"; // small, but the real thing
    var p6 = await ModelStore.DownloadAsync(hfRepo, hfFile, new DownloadProgress { Repo = hfRepo, File = hfFile });
    var hfPath = ModelStore.ResolvePath(hfRepo, hfFile);
    Check("6. real Hugging Face download completes", p6.State == DownloadState.Done && File.Exists(hfPath),
        p6.Error ?? "");
    Check("6. the file has content", File.Exists(hfPath) && new FileInfo(hfPath).Length > 500);
    try { Directory.Delete(hfRoot, recursive: true); } catch { }
}

try { Directory.Delete(root, recursive: true); } catch { }
Console.WriteLine(failures == 0 ? "\nALL TESTS PASSED" : $"\n{failures} FAILURE(S)");
return failures;

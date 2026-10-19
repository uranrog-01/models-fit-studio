using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace ModelFitStudio.Core;

/// <summary>One app per Windows user: a second launch brings the first one to the front.</summary>
internal sealed class AppInstance : IDisposable
{
    // For working on the app while a released copy is open: MODELFIT_SECOND_INSTANCE=1
    // gives this process its own lock, so it starts beside the other one.
    private static readonly string Side =
        Environment.GetEnvironmentVariable("MODELFIT_SECOND_INSTANCE") == "1" ? "-dev" : "";

    private FileStream? _lease;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _lockPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModelFitStudio", $"instance{Side}.lock");
    private readonly string _pipe = "ModelFitStudio-" + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))))[..16] + Side;

    private bool TryAcquire()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);
        try { _lease = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); return true; }
        catch (IOException) { return false; }
    }

    /// <summary>True when this process is the app. False when another one is, and it has been asked to show itself.</summary>
    public async Task<bool> AcquireAsync(Action activate)
    {
        if (!TryAcquire())
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // The first app may still be starting its pipe.
            while (!timeout.IsCancellationRequested)
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await client.ConnectAsync(500, timeout.Token);
                    using var reader = new StreamReader(client, leaveOpen: true);
                    using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
                    await writer.WriteLineAsync("activate");
                    if (await reader.ReadLineAsync(timeout.Token) is not null) break;
                }
                catch (IOException) { }
                catch (TimeoutException) { }
                catch (OperationCanceledException) { break; }
                await Task.Delay(100);
            }
            return false;
        }
        _ = ListenAsync(activate);
        return true;
    }

    private async Task ListenAsync(Action activate)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                if (await reader.ReadLineAsync(deadline.Token) != "activate") continue;
                await writer.WriteLineAsync("existing");
                activate();
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _lease?.Dispose();
        _lease = null;
    }
}

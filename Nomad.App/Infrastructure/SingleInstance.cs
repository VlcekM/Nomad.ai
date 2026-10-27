using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Nomad.App.Infrastructure;

/// <summary>
/// One Nomad.ai per Windows user. The first instance holds a named mutex and answers on a named pipe; a second one
/// asks it to show its window (lets it take the foreground) and exits. While the first is exiting (an update
/// restart, a quick relaunch) it no longer answers, and the new one waits for the mutex instead.
/// Scripted runs (<c>--dev-*</c>) are exempt, so screenshots work beside the user's own instance.
/// </summary>
internal sealed partial class SingleInstance : IDisposable
{
    private static readonly string Name = $"Nomad.ai-{Environment.UserDomainName}-{Environment.UserName}";

    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(15);

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();
    private bool _listening;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>Raised on a thread-pool thread when another launch asks this instance to show itself.</summary>
    public event Action? ShowRequested;

    /// <summary>This process is the instance (null: another one was asked to show itself; exit).</summary>
    public static SingleInstance? Claim()
    {
        var mutex = new Mutex(false, @"Local\" + Name);
        if (Acquire(mutex, TimeSpan.Zero))
        {
            return new SingleInstance(mutex);
        }

        if (AskToShow())
        {
            mutex.Dispose();
            return null;
        }

        // Held, but nobody answers: that instance is exiting. Take over once it has.
        if (Acquire(mutex, ExitWait))
        {
            return new SingleInstance(mutex);
        }

        mutex.Dispose();
        return null;
    }

    private static bool Acquire(Mutex mutex, TimeSpan wait)
    {
        try
        {
            return mutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            return true; // The last owner died without releasing it: ours now.
        }
    }

    private static bool AskToShow()
    {
        try
        {
            // The running instance may bring its window to the front: this launch is the user's own action.
            AllowSetForegroundWindow(-1);
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut);
            pipe.Connect(1500);
            pipe.WriteByte(1);
            pipe.Flush();
            return pipe.ReadByte() == 1;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Starts answering other launches.</summary>
    public void Listen()
    {
        if (_listening)
        {
            return;
        }

        _listening = true;
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    var buffer = new byte[1];
                    if (await pipe.ReadAsync(buffer, _stop.Token) == 1 && buffer[0] == 1)
                    {
                        ShowRequested?.Invoke();
                        pipe.WriteByte(1);
                        await pipe.FlushAsync(_stop.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    // A client that went away: wait for the next.
                }
            }
        });
    }

    /// <summary>Stops answering (exiting): a launch from now on waits for the mutex.</summary>
    public void StopListening() => _stop.Cancel();

    public void Dispose()
    {
        StopListening();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread (released already).
        }

        _mutex.Dispose();
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}

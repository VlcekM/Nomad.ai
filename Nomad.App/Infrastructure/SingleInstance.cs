using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Nomad.App.Infrastructure;

/// <summary>
/// One Nomad.ai per user. On Windows the first instance holds a named mutex and answers on a named pipe; on Linux/macOS
/// it holds an exclusive file lock (<c>flock</c>) and answers on a Unix domain socket in <c>XDG_RUNTIME_DIR</c> (because
/// .NET's <c>Local\</c> mutex is scoped to the POSIX session ID, which differs for every launcher/terminal start).
/// A second instance asks the running one to show its window (on a session if the launch named one, e.g. a clicked
/// notification) and exits. While the first is exiting (an update restart, a quick relaunch) it no longer answers, and
/// the new one waits for the lock instead.
/// Scripted runs (<c>--dev-*</c>) are exempt, so screenshots work beside the user's own instance.
/// </summary>
internal sealed partial class SingleInstance : IDisposable
{
    private static readonly string Name = $"Nomad.ai-{Environment.UserDomainName}-{Environment.UserName}";

    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly Mutex? _mutex;
    private readonly FileStream? _lockFile;
    private readonly string? _socketPath;
    private readonly CancellationTokenSource _stop = new();
    private Socket? _unixListener;
    private bool _listening;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    private SingleInstance(FileStream lockFile, string socketPath)
    {
        _lockFile = lockFile;
        _socketPath = socketPath;
    }

    /// <summary>Raised on a thread-pool thread when another launch asks this instance to show itself (and a session).</summary>
    public event Action<string?>? ShowRequested;

    /// <summary>This process is the instance (null: another one was asked to show itself; exit).</summary>
    public static SingleInstance? Claim(string? sessionId) =>
        OperatingSystem.IsWindows() ? ClaimWindows(sessionId) : ClaimUnix(sessionId);

    private static SingleInstance? ClaimWindows(string? sessionId)
    {
        var mutex = new Mutex(false, @"Local\" + Name);
        if (Acquire(mutex, TimeSpan.Zero))
        {
            return new SingleInstance(mutex);
        }

        if (AskToShowWindows(sessionId))
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

    private static SingleInstance? ClaimUnix(string? sessionId)
    {
        var (lockPath, socketPath) = UnixPaths();
        if (TryAcquireLock(lockPath) is { } lockFile)
        {
            return new SingleInstance(lockFile, socketPath);
        }

        if (AskToShowUnix(socketPath, sessionId))
        {
            return null;
        }

        // Held, but nobody answers: that instance is exiting. Take over once it has.
        if (AcquireLock(lockPath, ExitWait) is { } taken)
        {
            return new SingleInstance(taken, socketPath);
        }

        return null;
    }

    private static (string LockPath, string SocketPath) UnixPaths()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var dir = !string.IsNullOrEmpty(runtime)
            ? Path.Combine(runtime, "nomad")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nomad");

        Directory.CreateDirectory(dir);

        var lockPath = Path.Combine(dir, "instance.lock");
        var socketPath = Path.Combine(dir, "instance.sock");

        // Unix domain socket paths are capped at 108 bytes on Linux (sun_path).
        if (System.Text.Encoding.UTF8.GetByteCount(socketPath) >= 104)
        {
            socketPath = Path.Combine(Path.GetTempPath(), $"nomad-{Environment.UserName}.sock");
        }

        return (lockPath, socketPath);
    }

    private static FileStream? TryAcquireLock(string lockPath)
    {
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static FileStream? AcquireLock(string lockPath, TimeSpan wait)
    {
        var deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        while (true)
        {
            if (TryAcquireLock(lockPath) is { } lockFile)
            {
                return lockFile;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return null;
            }

            Thread.Sleep(50);
        }
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

    private static bool AskToShowWindows(string? sessionId)
    {
        try
        {
            // The running instance may bring its window to the front: this launch is the user's own action.
            if (OperatingSystem.IsWindows())
            {
                AllowSetForegroundWindow(-1);
            }

            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut);
            pipe.Connect((int)ConnectTimeout.TotalMilliseconds);
            return SendRequest(pipe, sessionId);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool AskToShowUnix(string socketPath, string? sessionId)
    {
        var deadline = Environment.TickCount64 + (long)ConnectTimeout.TotalMilliseconds;
        var endpoint = new UnixDomainSocketEndPoint(socketPath);

        while (true)
        {
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                socket.ReceiveTimeout = (int)ConnectTimeout.TotalMilliseconds;
                socket.SendTimeout = (int)ConnectTimeout.TotalMilliseconds;
                socket.Connect(endpoint);

                using var stream = new NetworkStream(socket, ownsSocket: false);
                return SendRequest(stream, sessionId);
            }
            catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
            {
                if (Environment.TickCount64 >= deadline)
                {
                    return false;
                }

                Thread.Sleep(50);
            }
        }
    }

    private static bool SendRequest(Stream stream, string? sessionId)
    {
        // 1: show; 2: show this session (its id follows, length first).
        var id = System.Text.Encoding.UTF8.GetBytes(sessionId ?? "");
        if (id.Length is 0 or > 255)
        {
            stream.WriteByte(1);
        }
        else
        {
            stream.Write([2, (byte)id.Length, .. id]);
        }

        stream.Flush();
        return stream.ReadByte() == 1;
    }

    /// <summary>Starts answering other launches.</summary>
    public void Listen()
    {
        if (_listening)
        {
            return;
        }

        _listening = true;
        if (OperatingSystem.IsWindows())
        {
            _ = Task.Run(ListenWindowsAsync);
        }
        else if (_socketPath is not null)
        {
            _ = Task.Run(() => ListenUnixAsync(_socketPath));
        }
    }

    private async Task ListenWindowsAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                await HandleRequestAsync(pipe, _stop.Token);
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
    }

    private async Task ListenUnixAsync(string socketPath)
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        // We hold the exclusive lock, so any leftover socket file is from a crashed run.
        TryDeleteFile(socketPath);

        Socket listener;
        try
        {
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            listener.Listen(8);
            _unixListener = listener;
            if (_stop.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _unixListener, null)?.Dispose();
                TryDeleteFile(socketPath);
                return;
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
        {
            return;
        }

        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var client = await listener.AcceptAsync(_stop.Token);
                    await using var stream = new NetworkStream(client, ownsSocket: false);
                    await HandleRequestAsync(stream, _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    // A client that went away, or listener closed during shutdown.
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _unixListener, null)?.Dispose();
        }
    }

    private async Task HandleRequestAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        if (await stream.ReadAsync(buffer, ct) == 1 && buffer[0] is 1 or 2)
        {
            string? sessionId = null;
            if (buffer[0] == 2)
            {
                await stream.ReadExactlyAsync(buffer, ct);
                var id = new byte[buffer[0]];
                await stream.ReadExactlyAsync(id, ct);
                sessionId = System.Text.Encoding.UTF8.GetString(id);
            }

            ShowRequested?.Invoke(sessionId);
            stream.WriteByte(1);
            await stream.FlushAsync(ct);
        }
    }

    /// <summary>Stops answering (exiting): a launch from now on waits for the lock.</summary>
    public void StopListening()
    {
        _stop.Cancel();
        Interlocked.Exchange(ref _unixListener, null)?.Dispose();
        if (_socketPath is not null)
        {
            TryDeleteFile(_socketPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        StopListening();
        if (_mutex is not null)
        {
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

        _lockFile?.Dispose();
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using Sshelfy.Core.Ssh;
using Sshelfy.Core.Terminal;

namespace Sshelfy.Infrastructure.Windows;

/// <summary>
/// Local shells behind a Windows pseudo console (ConPTY, Windows 10 1809+). ConPTY speaks VT
/// sequences, so the same terminal emulator renders PowerShell, cmd and WSL like an SSH session.
/// </summary>
public sealed class ConPtyShellLauncher : ILocalShellLauncher
{
    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    public async Task<IReadOnlyList<LocalShellProfile>> DetectAsync(CancellationToken cancellationToken = default)
    {
        var shells = new List<LocalShellProfile>();
        if (!IsSupported)
            return shells;

        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (FindPwsh() is { } pwsh)
            shells.Add(new LocalShellProfile("pwsh", "PowerShell", pwsh, "-NoLogo"));
        var windowsPowerShell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(windowsPowerShell))
            shells.Add(new LocalShellProfile("powershell", "Windows PowerShell", windowsPowerShell, "-NoLogo"));
        shells.Add(new LocalShellProfile("cmd", "Command Prompt", Path.Combine(system, "cmd.exe")));

        var gitBash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        if (File.Exists(gitBash))
            shells.Add(new LocalShellProfile("gitbash", "Git Bash", gitBash, "--login -i"));

        var wsl = Path.Combine(system, "wsl.exe");
        if (File.Exists(wsl))
        {
            foreach (var distribution in await ListWslDistributionsAsync(wsl, cancellationToken))
                shells.Add(new LocalShellProfile("wsl:" + distribution, distribution + " (WSL)", wsl, $"-d \"{distribution}\" --cd ~"));
        }

        return shells;
    }

    public Task<IShellChannel> StartAsync(LocalShellProfile profile, int columns, int rows, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            throw new LocalShellException("Local terminals need Windows 10 version 1809 or newer.");
        return Task.Run<IShellChannel>(() => PseudoConsoleSession.Start(profile, columns, rows, workingDirectory), cancellationToken);
    }

    public string? FindOpenSsh()
    {
        var builtIn = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
        return File.Exists(builtIn) ? builtIn : FindOnPath("ssh.exe");
    }

    private static string? FindOnPath(string file)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), file);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string? FindPwsh()
    {
        if (FindOnPath("pwsh.exe") is { } onPath)
            return onPath;

        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        return File.Exists(installed) ? installed : null;
    }

    /// <summary>`wsl.exe -l -q` prints distribution names in UTF-16.</summary>
    private static async Task<IReadOnlyList<string>> ListWslDistributionsAsync(string wsl, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(wsl, "-l -q")
            {
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.Unicode,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
                return [];

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode != 0
                ? []
                : output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(n => n.Trim('\0', '\r'))
                    .Where(n => n.Length > 0 && !n.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
                    .ToList();
        }
        catch (Exception ex) when (ex is Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>One process attached to a pseudo console, exposed as a shell channel.</summary>
    private sealed class PseudoConsoleSession : IShellChannel
    {
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const int UseStdHandles = 0x00000100;
        private static readonly IntPtr PseudoConsoleAttribute = 0x00020016;

        private readonly IntPtr _pseudoConsole;
        private readonly SafeProcessHandle _process;
        private readonly FileStream _input;
        private readonly FileStream _output;
        private readonly Channel<byte[]> _writes = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        private int _closed;
        private int _disposed;

        private PseudoConsoleSession(IntPtr pseudoConsole, SafeProcessHandle process, FileStream input, FileStream output)
        {
            _pseudoConsole = pseudoConsole;
            _process = process;
            _input = input;
            _output = output;
        }

        public event Action<byte[]>? DataReceived;

        public event EventHandler? Closed;

        public static PseudoConsoleSession Start(LocalShellProfile profile, int columns, int rows, string? workingDirectory)
        {
            if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0) ||
                !CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
                throw new LocalShellException("Could not create the console pipes.", new Win32Exception());

            var size = new Coord((short)Math.Clamp(columns, 2, short.MaxValue), (short)Math.Clamp(rows, 1, short.MaxValue));
            var result = CreatePseudoConsole(size, inputRead, outputWrite, 0, out var pseudoConsole);
            if (result != 0)
                throw new LocalShellException("Could not create a pseudo console.", new Win32Exception(result));

            // The pseudo console holds its own references to these ends.
            inputRead.Dispose();
            outputWrite.Dispose();

            var attributeListSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
            var attributeList = Marshal.AllocHGlobal(attributeListSize);
            try
            {
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize) ||
                    !UpdateProcThreadAttribute(attributeList, 0, PseudoConsoleAttribute, pseudoConsole, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new LocalShellException("Could not attach the pseudo console.", new Win32Exception());

                var startup = new StartupInfoEx { AttributeList = attributeList };
                startup.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();

                // Empty standard handles: otherwise a child of a process with redirected output (a test
                // runner, a console host) writes there instead of into the pseudo console.
                startup.StartupInfo.dwFlags = UseStdHandles;
                var commandLine = new StringBuilder($"\"{profile.Executable}\"{(profile.Arguments is { Length: > 0 } a ? " " + a : string.Empty)}");
                var directory = workingDirectory is not null && Directory.Exists(workingDirectory) ? workingDirectory : null;
                var environment = profile.Environment is { Count: > 0 } extra ? Marshal.StringToHGlobalUni(EnvironmentBlock(extra)) : IntPtr.Zero;

                ProcessInformation info;
                try
                {
                    if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                            environment, directory, ref startup, out info))
                    {
                        var error = new Win32Exception();
                        ClosePseudoConsole(pseudoConsole);
                        throw new LocalShellException($"Could not start {profile.Name}: {error.Message}", error);
                    }
                }
                finally
                {
                    if (environment != IntPtr.Zero)
                        Marshal.FreeHGlobal(environment);
                }

                CloseHandle(info.hThread);
                var session = new PseudoConsoleSession(
                    pseudoConsole,
                    new SafeProcessHandle(info.hProcess, ownsHandle: true),
                    new FileStream(inputWrite, FileAccess.Write, 1),
                    new FileStream(outputRead, FileAccess.Read, 1));
                session.Run();
                return session;
            }
            finally
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
        }

        /// <summary>The app's environment with <paramref name="extra"/> applied, as a sorted "name=value\0…\0" block.</summary>
        private static string EnvironmentBlock(IReadOnlyDictionary<string, string> extra)
        {
            var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                variables[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
            foreach (var (name, value) in extra)
                variables[name] = value;

            var block = new StringBuilder();
            foreach (var (name, value) in variables)
                block.Append(name).Append('=').Append(value).Append('\0');
            return block.Append('\0').ToString();
        }

        public void Write(byte[] data)
        {
            if (data.Length > 0)
                _writes.Writer.TryWrite(data);
        }

        public void Resize(int columns, int rows)
        {
            if (_disposed == 0)
                ResizePseudoConsole(_pseudoConsole, new Coord((short)Math.Clamp(columns, 2, short.MaxValue), (short)Math.Clamp(rows, 1, short.MaxValue)));
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return ValueTask.CompletedTask;

            _writes.Writer.TryComplete();
            // Closing the pseudo console ends the attached processes and breaks the output pipe.
            ClosePseudoConsole(_pseudoConsole);
            _input.Dispose();
            _output.Dispose();
            _process.Dispose();
            return ValueTask.CompletedTask;
        }

        private void Run()
        {
            _ = Task.Run(ReadLoop);
            _ = Task.Run(WriteLoopAsync);
            _ = Task.Run(WaitForExit);
        }

        private void ReadLoop()
        {
            var buffer = new byte[16 * 1024];
            try
            {
                int read;
                while ((read = _output.Read(buffer, 0, buffer.Length)) > 0)
                    DataReceived?.Invoke(buffer.AsSpan(0, read).ToArray());
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
            finally
            {
                RaiseClosed();
            }
        }

        private async Task WriteLoopAsync()
        {
            try
            {
                await foreach (var data in _writes.Reader.ReadAllAsync())
                {
                    _input.Write(data, 0, data.Length);
                    _input.Flush();
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }

        /// <summary>The output pipe stays open after the shell exits until the pseudo console is closed.</summary>
        private void WaitForExit()
        {
            WaitForSingleObject(_process.DangerousGetHandle(), uint.MaxValue);
            RaiseClosed();
            _ = DisposeAsync();
        }

        private void RaiseClosed()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
                Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, IntPtr attributes, int size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr pseudoConsole);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr pseudoConsole, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation info);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}

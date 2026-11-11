using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Sshelfy.Infrastructure.Ssh.Agent;

/// <summary>Sends one agent request and returns the reply (without the length prefix).</summary>
internal interface IAgentTransport
{
    string Name { get; }

    byte[] Request(byte[] message);
}

/// <summary>Stream based agents: Windows named pipes (OpenSSH for Windows) and Unix sockets.</summary>
internal abstract class StreamAgentTransport : IAgentTransport
{
    protected static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public abstract string Name { get; }

    protected abstract Stream Open();

    public byte[] Request(byte[] message)
    {
        using var stream = Open();
        stream.Write(AgentProtocol.Frame(message));
        stream.Flush();

        Span<byte> header = stackalloc byte[4];
        stream.ReadExactly(header);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 or > AgentProtocol.MaxMessageLength)
            throw new InvalidDataException("Invalid agent reply length.");

        var reply = new byte[length];
        stream.ReadExactly(reply);
        return reply;
    }
}

internal sealed class NamedPipeAgentTransport(string pipePath, string name) : StreamAgentTransport
{
    public override string Name => name;

    public string PipePath => pipePath;

    protected override Stream Open()
    {
        // "\\.\pipe\openssh-ssh-agent" → server ".", pipe "openssh-ssh-agent".
        var pipeName = pipePath.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase) ? pipePath[9..] : pipePath;
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        try
        {
            pipe.Connect(2000);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }
}

internal sealed class UnixSocketAgentTransport(string socketPath) : StreamAgentTransport
{
    public override string Name => "SSH agent";

    protected override Stream Open()
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            return new NetworkStream(socket, ownsSocket: true) { ReadTimeout = (int)Timeout.TotalMilliseconds };
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>
/// PuTTY's Pageant through its classic WM_COPYDATA interface: the request is written to a
/// named shared memory block, whose name is sent to the Pageant window.
/// </summary>
internal sealed class PageantAgentTransport : IAgentTransport
{
    private const uint AgentCopyDataId = 0x804e50ba;
    private const int WmCopyData = 0x004A;
    private const int MapSize = 8192;

    public string Name => "Pageant";

    public static bool IsRunning => OperatingSystem.IsWindows() && FindWindow("Pageant", "Pageant") != IntPtr.Zero;

    public byte[] Request(byte[] message)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();

        var window = FindWindow("Pageant", "Pageant");
        if (window == IntPtr.Zero)
            throw new AgentException("Pageant is not running.");

        var framed = AgentProtocol.Frame(message);
        if (framed.Length > MapSize)
            throw new AgentException("The request is too large for Pageant.");

        var mapName = $"PageantRequest{Environment.CurrentManagedThreadId:x8}{Guid.NewGuid():N}"[..40];
        using var map = MemoryMappedFile.CreateNew(mapName, MapSize);
        using var view = map.CreateViewAccessor(0, MapSize);
        view.WriteArray(0, framed, 0, framed.Length);

        var nameBytes = Encoding.ASCII.GetBytes(mapName + "\0");
        var namePtr = Marshal.AllocHGlobal(nameBytes.Length);
        try
        {
            Marshal.Copy(nameBytes, 0, namePtr, nameBytes.Length);
            var data = new CopyDataStruct { dwData = unchecked((nint)(nuint)AgentCopyDataId), cbData = nameBytes.Length, lpData = namePtr };
            if (SendMessage(window, WmCopyData, IntPtr.Zero, ref data) == IntPtr.Zero)
                throw new AgentException("Pageant did not answer the request.");
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
        }

        var header = new byte[4];
        view.ReadArray(0, header, 0, 4);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 || length + 4 > MapSize)
            throw new InvalidDataException("Invalid Pageant reply.");

        var reply = new byte[length];
        view.ReadArray(4, reply, 0, (int)length);
        return reply;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string windowName);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref CopyDataStruct lParam);
}

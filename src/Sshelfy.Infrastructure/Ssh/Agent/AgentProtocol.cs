using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Sshelfy.Core.Ssh;

namespace Sshelfy.Infrastructure.Ssh.Agent;

/// <summary>Message codes and framing of the SSH agent protocol (draft-miller-ssh-agent).</summary>
internal static class AgentProtocol
{
    public const byte Failure = 5;
    public const byte RequestIdentities = 11;
    public const byte IdentitiesAnswer = 12;
    public const byte SignRequest = 13;
    public const byte SignResponse = 14;

    public const uint RsaSha2_256 = 2;
    public const uint RsaSha2_512 = 4;

    /// <summary>Upper bound for a reply; real replies are a few KB.</summary>
    public const int MaxMessageLength = 256 * 1024;

    public static byte[] BuildRequestIdentities() => [RequestIdentities];

    public static byte[] BuildSignRequest(byte[] keyBlob, byte[] data, uint flags)
    {
        var w = new WireWriter();
        w.WriteByte(SignRequest);
        w.WriteString(keyBlob);
        w.WriteString(data);
        w.WriteUInt32(flags);
        return w.ToArray();
    }

    public static IReadOnlyList<AgentKey> ParseIdentities(byte[] reply)
    {
        var r = new WireReader(reply);
        var code = r.ReadByte();
        if (code != IdentitiesAnswer)
            throw new InvalidDataException($"Unexpected agent reply {code} to an identities request.");

        var count = r.ReadUInt32();
        var keys = new List<AgentKey>((int)Math.Min(count, 64));
        for (var i = 0; i < count; i++)
        {
            var blob = r.ReadString();
            var comment = Encoding.UTF8.GetString(r.ReadString());
            var algorithm = Encoding.ASCII.GetString(new WireReader(blob).ReadString());
            keys.Add(new AgentKey(algorithm, Fingerprint(blob), comment, blob));
        }

        return keys;
    }

    /// <returns>The signature blob (string algorithm, string signature), as SSH expects it.</returns>
    public static byte[] ParseSignature(byte[] reply)
    {
        var r = new WireReader(reply);
        var code = r.ReadByte();
        if (code == Failure)
            throw new AgentException("The SSH agent refused to sign (the key may need to be confirmed or unlocked).");
        if (code != SignResponse)
            throw new InvalidDataException($"Unexpected agent reply {code} to a sign request.");
        return r.ReadString();
    }

    /// <summary>Writes a length-prefixed message.</summary>
    public static byte[] Frame(byte[] message)
    {
        var framed = new byte[4 + message.Length];
        BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)message.Length);
        message.CopyTo(framed, 4);
        return framed;
    }

    public static string Fingerprint(byte[] blob) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
}

internal sealed class AgentException(string message, Exception? inner = null) : Exception(message, inner);

internal sealed class WireWriter
{
    private readonly MemoryStream _stream = new();

    public void WriteByte(byte value) => _stream.WriteByte(value);

    public void WriteUInt32(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        _stream.Write(buffer);
    }

    public void WriteString(byte[] value)
    {
        WriteUInt32((uint)value.Length);
        _stream.Write(value);
    }

    public byte[] ToArray() => _stream.ToArray();
}

internal ref struct WireReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _offset;

    public WireReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _offset = 0;
    }

    public byte ReadByte()
    {
        Ensure(1);
        return _data[_offset++];
    }

    public uint ReadUInt32()
    {
        Ensure(4);
        var value = BinaryPrimitives.ReadUInt32BigEndian(_data[_offset..]);
        _offset += 4;
        return value;
    }

    public byte[] ReadString()
    {
        var length = ReadUInt32();
        if (length > AgentProtocol.MaxMessageLength)
            throw new InvalidDataException("Agent message field is too long.");
        Ensure((int)length);
        var value = _data.Slice(_offset, (int)length).ToArray();
        _offset += (int)length;
        return value;
    }

    private readonly void Ensure(int count)
    {
        if (_offset + count > _data.Length)
            throw new InvalidDataException("Agent message is truncated.");
    }
}

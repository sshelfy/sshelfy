namespace Sshelfy.Core.Sftp;

/// <summary>Owner, group and permission bits of a remote file (numeric ids: SFTP v3 does not carry names).</summary>
public sealed record RemoteFileDetails(string Path, int Mode, int UserId, int GroupId);

/// <summary>POSIX permission bits (<c>0o7777</c>): conversion between the number, octal text and <c>rwxr-xr-x</c>.</summary>
public static class UnixPermissions
{
    public const int SetUid = 0b100_000_000_000;
    public const int SetGid = 0b010_000_000_000;
    public const int Sticky = 0b001_000_000_000;

    /// <summary>Bit for one class ("owner", "group", "others" = 0, 1, 2) and right ("read", "write", "execute" = 0, 1, 2).</summary>
    public static int Bit(int who, int right) => 1 << (8 - who * 3 - right);

    public static bool Has(int mode, int who, int right) => (mode & Bit(who, right)) != 0;

    public static int With(int mode, int who, int right, bool on) => on ? mode | Bit(who, right) : mode & ~Bit(who, right);

    /// <summary>"755", or four digits when special bits are set ("4755").</summary>
    public static string ToOctal(int mode)
    {
        mode &= 0b111_111_111_111;
        var text = Convert.ToString(mode, 8);
        return mode > 0b111_111_111 ? text.PadLeft(4, '0') : text.PadLeft(3, '0');
    }

    /// <summary>Parses 3 or 4 octal digits; null for anything else.</summary>
    public static int? ParseOctal(string text)
    {
        text = text.Trim();
        if (text.Length is < 3 or > 4 || text.Any(c => c is < '0' or > '7'))
            return null;
        return Convert.ToInt32(text, 8);
    }

    public static string ToSymbolic(int mode)
    {
        Span<char> chars = stackalloc char[9];
        const string letters = "rwx";
        for (var who = 0; who < 3; who++)
        {
            for (var right = 0; right < 3; right++)
                chars[who * 3 + right] = Has(mode, who, right) ? letters[right] : '-';
        }

        // Special bits replace the execute letter, like ls does.
        if ((mode & SetUid) != 0)
            chars[2] = chars[2] == 'x' ? 's' : 'S';
        if ((mode & SetGid) != 0)
            chars[5] = chars[5] == 'x' ? 's' : 'S';
        if ((mode & Sticky) != 0)
            chars[8] = chars[8] == 'x' ? 't' : 'T';
        return new string(chars);
    }
}

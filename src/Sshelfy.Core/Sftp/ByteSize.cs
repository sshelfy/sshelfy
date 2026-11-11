using System.Globalization;

namespace Sshelfy.Core.Sftp;

public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>Human readable size using 1024-based units, e.g. "1.4 MB".</summary>
    public static string Format(long bytes)
    {
        if (bytes < 1024)
            return bytes.ToString(CultureInfo.CurrentCulture) + " B";

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(value < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + Units[unit];
    }
}

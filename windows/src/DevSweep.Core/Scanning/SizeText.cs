using System.Globalization;

namespace DevSweep.Core.Scanning;

/// <summary>Sizes as Explorer shows them (1 KB = 1,024 bytes).</summary>
public static class SizeText
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes)
    {
        if (bytes < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} {(bytes == 1 ? "byte" : "bytes")}");
        }

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{(value >= 100 ? value.ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.#", CultureInfo.InvariantCulture))} {Units[unit]}");
    }
}

using System.IO.Compression;

namespace DevSweep.App.Background;

/// <summary>
/// The fallback icon, drawn in code (a blue disc with a white broom), used only when the real logo files in
/// Assets are missing: premultiplied BGRA pixels for the tray icon and a PNG for the notification's icon.
/// </summary>
internal static class AppIconImage
{
    /// <summary>Premultiplied BGRA, top-down, <paramref name="size"/> by <paramref name="size"/> pixels.</summary>
    public static byte[] Bgra(int size)
    {
        byte[] pixels = new byte[size * size * 4];
        double pixel = 1.0 / size;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double u = (x + 0.5) * pixel;
                double v = (y + 0.5) * pixel;
                double disc = Coverage(Math.Sqrt(((u - 0.5) * (u - 0.5)) + ((v - 0.5) * (v - 0.5))) - 0.47, pixel);
                double handle = Coverage(DistanceToSegment(u, v, 0.30, 0.72, 0.72, 0.28) - 0.045, pixel);
                double head = Coverage(DistanceToSegment(u, v, 0.20, 0.56, 0.44, 0.80) - 0.10, pixel);
                double white = Math.Max(handle, head * 0.92) * disc;
                double alpha = disc;
                // Colour = blue under white, premultiplied by the disc's own coverage.
                double r = (((0x1F * (1 - white)) + (255 * white)) * alpha);
                double g = (((0x6F * (1 - white)) + (255 * white)) * alpha);
                double b = (((0xEB * (1 - white)) + (255 * white)) * alpha);
                int i = ((y * size) + x) * 4;
                pixels[i] = (byte)Math.Round(b);
                pixels[i + 1] = (byte)Math.Round(g);
                pixels[i + 2] = (byte)Math.Round(r);
                pixels[i + 3] = (byte)Math.Round(alpha * 255);
            }
        }

        return pixels;
    }

    /// <summary>A PNG of the icon (straight alpha), for the toast's logo.</summary>
    public static byte[] Png(int size)
    {
        byte[] bgra = Bgra(size);
        byte[] raw = new byte[size * ((size * 4) + 1)];
        for (int y = 0; y < size; y++)
        {
            int row = y * ((size * 4) + 1);
            raw[row] = 0;
            for (int x = 0; x < size; x++)
            {
                int s = ((y * size) + x) * 4;
                int d = row + 1 + (x * 4);
                byte a = bgra[s + 3];
                raw[d] = Unpremultiply(bgra[s + 2], a);
                raw[d + 1] = Unpremultiply(bgra[s + 1], a);
                raw[d + 2] = Unpremultiply(bgra[s], a);
                raw[d + 3] = a;
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] header = new byte[13];
        WriteUInt32(header, 0, (uint)size);
        WriteUInt32(header, 4, (uint)size);
        header[8] = 8;   // bit depth
        header[9] = 6;   // RGBA
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static byte Unpremultiply(byte value, byte alpha) => alpha == 0 ? (byte)0 : (byte)Math.Min(255, value * 255 / alpha);

    private static double Coverage(double signedDistance, double pixel) => Math.Clamp(0.5 - (signedDistance / pixel), 0, 1);

    private static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double t = Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / ((dx * dx) + (dy * dy)), 0, 1);
        double cx = ax + (t * dx);
        double cy = ay + (t * dy);
        return Math.Sqrt(((px - cx) * (px - cx)) + ((py - cy) * (py - cy)));
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        byte[] length = new byte[4];
        WriteUInt32(length, 0, (uint)data.Length);
        stream.Write(length);
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        uint crc = Crc32(typeBytes, 0xFFFFFFFF);
        crc = Crc32(data, crc) ^ 0xFFFFFFFF;
        byte[] crcBytes = new byte[4];
        WriteUInt32(crcBytes, 0, crc);
        stream.Write(crcBytes);
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] data, uint crc)
    {
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return crc;
    }
}

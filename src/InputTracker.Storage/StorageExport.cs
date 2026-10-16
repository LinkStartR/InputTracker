using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace InputTracker.Storage;

public sealed record StorageExportArtifact(string Path, string Sha256, long Length);

internal static class StorageExport
{
    private const int HeatmapColumns = 16;
    private const int FontWidth = 5;
    private const int FontHeight = 7;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static StorageExportArtifact WriteKeyboardCsv(
        StorageLayout layout,
        StorageKeyboardPeriodResult result,
        IReadOnlyDictionary<Guid, string?> aliases,
        string relativePath)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(aliases);
        ValidateResult(result);
        string path = ResolveNewOutputPath(layout, relativePath, ".csv");
        var text = new StringBuilder();
        text.Append("device_id,device_alias,make_code,is_e0,is_e1,press_count,hold_duration_us\r\n");
        foreach (StorageKeyboardPeriodAggregate aggregate in result.Aggregates
            .OrderBy(row => row.DeviceId)
            .ThenBy(row => row.MakeCode)
            .ThenBy(row => row.IsE0)
            .ThenBy(row => row.IsE1))
        {
            aliases.TryGetValue(aggregate.DeviceId, out string? alias);
            text.Append(aggregate.DeviceId.ToString("N"));
            text.Append(',');
            AppendCsv(text, alias ?? string.Empty);
            text.Append(',').Append(aggregate.MakeCode.ToString(CultureInfo.InvariantCulture));
            text.Append(',').Append(aggregate.IsE0 ? '1' : '0');
            text.Append(',').Append(aggregate.IsE1 ? '1' : '0');
            text.Append(',').Append(aggregate.PressCount.ToString(CultureInfo.InvariantCulture));
            text.Append(',').Append(aggregate.HoldDurationMicroseconds.ToString(CultureInfo.InvariantCulture));
            text.Append("\r\n");
        }

        return WriteNewAtomic(path, Encoding.UTF8.GetBytes(text.ToString()));
    }

    public static StorageExportArtifact WriteKeyboardHeatmapPng(
        StorageLayout layout,
        StorageKeyboardPeriodResult result,
        string relativePath,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateResult(result);
        if (width is < 256 or > 4096 || height is < 256 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "PNG dimensions must be between 256 and 4096 pixels.");
        }

        string path = ResolveNewOutputPath(layout, relativePath, ".png");
        byte[] pixels = RenderHeatmap(result, width, height);
        return WriteNewAtomic(path, EncodePng(pixels, width, height));
    }

    private static byte[] RenderHeatmap(StorageKeyboardPeriodResult result, int width, int height)
    {
        StorageKeyboardPeriodAggregate[] rows = result.Aggregates
            .OrderBy(row => row.IsE1 ? 2 : row.IsE0 ? 1 : 0)
            .ThenBy(row => row.MakeCode)
            .ThenBy(row => row.DeviceId)
            .ToArray();
        long maximum = rows.Length == 0 ? 0 : rows.Max(row => row.PressCount);
        var pixels = new byte[checked(width * height * 4)];
        Fill(pixels, width, height, 20, 24, 32, 255);
        int tileGap = Math.Max(2, width / 256);
        int tileWidth = Math.Max(1, (width - tileGap * (HeatmapColumns + 1)) / HeatmapColumns);
        int rowCount = Math.Max(1, (rows.Length + HeatmapColumns - 1) / HeatmapColumns);
        int tileHeight = Math.Max(1, (height - tileGap * (rowCount + 1)) / rowCount);
        int fontScale = Math.Max(1, Math.Min(tileWidth / 28, tileHeight / 12));

        for (int index = 0; index < rows.Length; index++)
        {
            StorageKeyboardPeriodAggregate row = rows[index];
            int column = index % HeatmapColumns;
            int gridRow = index / HeatmapColumns;
            int x = tileGap + column * (tileWidth + tileGap);
            int y = tileGap + gridRow * (tileHeight + tileGap);
            double strength = maximum == 0 ? 0 : Math.Sqrt((double)row.PressCount / maximum);
            byte red = checked((byte)Math.Round(35 + strength * 205));
            byte green = checked((byte)Math.Round(75 + strength * 55));
            byte blue = checked((byte)Math.Round(150 - strength * 100));
            FillRect(pixels, width, height, x, y, tileWidth, tileHeight, red, green, blue, 255);

            string label = row.IsE1 ? "E1" : row.IsE0 ? "E0" : "--";
            label += row.MakeCode.ToString("X4", CultureInfo.InvariantCulture);
            int labelWidth = label.Length * (FontWidth + 1) * fontScale - fontScale;
            int labelX = x + Math.Max(1, (tileWidth - labelWidth) / 2);
            int labelY = y + Math.Max(1, (tileHeight - FontHeight * fontScale) / 2);
            bool darkText = red * 299 + green * 587 + blue * 114 > 150_000;
            byte text = darkText ? (byte)16 : (byte)248;
            DrawText(pixels, width, height, labelX, labelY, label, fontScale, text, text, text);
        }

        return pixels;
    }

    private static byte[] EncodePng(byte[] rgba, int width, int height)
    {
        using var output = new MemoryStream();
        output.Write(PngSignature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = 6;
        WritePngChunk(output, "IHDR", header);

        using var raw = new MemoryStream(checked(height * (width * 4 + 1)));
        for (int row = 0; row < height; row++)
        {
            raw.WriteByte(0);
            raw.Write(rgba, checked(row * width * 4), checked(width * 4));
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(zlib);
        }

        WritePngChunk(output, "IDAT", compressed.ToArray());
        WritePngChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static void WritePngChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        byte[] crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput.AsSpan(typeBytes.Length));
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, CalculatePngCrc(crcInput));
        output.Write(crc);
    }

    private static uint CalculatePngCrc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
            }
        }

        return ~crc;
    }

    private static StorageExportArtifact WriteNewAtomic(string path, byte[] bytes)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: false);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }

        return new StorageExportArtifact(
            path,
            Convert.ToHexString(SHA256.HashData(bytes)),
            bytes.LongLength);
    }

    private static string ResolveNewOutputPath(StorageLayout layout, string relativePath, string extension)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathFullyQualified(relativePath)
            || !string.Equals(Path.GetExtension(relativePath), extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Export path must be a relative file with the requested extension.", nameof(relativePath));
        }

        string root = Path.GetFullPath(layout.ProgramDirectory);
        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Export path escapes the program directory.");
        }

        string parent = Path.GetDirectoryName(path)!;
        CreateContainedDirectoryChain(root, parent);
        if (File.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Export destination cannot be a reparse point.");
            }
            throw new IOException("Export destination already exists.");
        }

        if (Directory.Exists(path))
        {
            throw new InvalidDataException("Export destination cannot be a directory.");
        }

        return path;
    }

    private static void CreateContainedDirectoryChain(string root, string directory)
    {
        string relative = Path.GetRelativePath(root, directory);
        string current = root;
        if (!Directory.Exists(current) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Export directory chain is missing or contains a reparse point.");
        }

        foreach (string component in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (File.Exists(current))
            {
                throw new InvalidDataException("Export directory chain contains a file.");
            }

            if (!Directory.Exists(current))
            {
                Directory.CreateDirectory(current);
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Export directory chain is missing or contains a reparse point.");
            }
        }
    }

    private static void ValidateResult(StorageKeyboardPeriodResult result)
    {
        result.Period.UtcRange.Validate();
        foreach (StorageKeyboardPeriodAggregate row in result.Aggregates)
        {
            if (row.DeviceId == Guid.Empty || (row.IsE0 && row.IsE1)
                || row.PressCount < 0 || row.HoldDurationMicroseconds < 0)
            {
                throw new InvalidDataException("Keyboard export contains an invalid aggregate.");
            }
        }
    }

    private static void AppendCsv(StringBuilder destination, string value)
    {
        bool quote = value.IndexOfAny([',', '"', '\r', '\n']) >= 0;
        if (quote) destination.Append('"');
        foreach (char character in value)
        {
            if (character == '"') destination.Append('"');
            destination.Append(character);
        }
        if (quote) destination.Append('"');
    }

    private static void Fill(byte[] pixels, int width, int height, byte r, byte g, byte b, byte a) =>
        FillRect(pixels, width, height, 0, 0, width, height, r, g, b, a);

    private static void FillRect(
        byte[] pixels, int width, int height, int x, int y, int w, int h,
        byte r, byte g, byte b, byte a)
    {
        for (int py = Math.Max(0, y); py < Math.Min(height, y + h); py++)
        {
            for (int px = Math.Max(0, x); px < Math.Min(width, x + w); px++)
            {
                int offset = checked((py * width + px) * 4);
                pixels[offset] = r;
                pixels[offset + 1] = g;
                pixels[offset + 2] = b;
                pixels[offset + 3] = a;
            }
        }
    }

    private static void DrawText(
        byte[] pixels, int width, int height, int x, int y, string text, int scale,
        byte r, byte g, byte b)
    {
        foreach (char character in text)
        {
            ReadOnlySpan<byte> glyph = GetGlyph(character);
            for (int row = 0; row < FontHeight; row++)
            {
                for (int column = 0; column < FontWidth; column++)
                {
                    if ((glyph[row] & (1 << (FontWidth - column - 1))) != 0)
                    {
                        FillRect(pixels, width, height, x + column * scale, y + row * scale,
                            scale, scale, r, g, b, 255);
                    }
                }
            }
            x += (FontWidth + 1) * scale;
        }
    }

    private static ReadOnlySpan<byte> GetGlyph(char value) => value switch
    {
        '0' => [14, 17, 19, 21, 25, 17, 14], '1' => [4, 12, 4, 4, 4, 4, 14],
        '2' => [14, 17, 1, 2, 4, 8, 31], '3' => [30, 1, 1, 14, 1, 1, 30],
        '4' => [2, 6, 10, 18, 31, 2, 2], '5' => [31, 16, 16, 30, 1, 1, 30],
        '6' => [14, 16, 16, 30, 17, 17, 14], '7' => [31, 1, 2, 4, 8, 8, 8],
        '8' => [14, 17, 17, 14, 17, 17, 14], '9' => [14, 17, 17, 15, 1, 1, 14],
        'A' => [14, 17, 17, 31, 17, 17, 17], 'B' => [30, 17, 17, 30, 17, 17, 30],
        'C' => [14, 17, 16, 16, 16, 17, 14], 'D' => [30, 17, 17, 17, 17, 17, 30],
        'E' => [31, 16, 16, 30, 16, 16, 31], 'F' => [31, 16, 16, 30, 16, 16, 16],
        '-' => [0, 0, 0, 31, 0, 0, 0], _ => [0, 0, 0, 0, 0, 0, 0]
    };
}

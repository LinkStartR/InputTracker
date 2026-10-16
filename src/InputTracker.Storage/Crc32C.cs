namespace InputTracker.Storage;

internal static class Crc32C
{
    private const uint ReflectedPolynomial = 0x82F63B78;

    public static uint Calculate(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (ReflectedPolynomial & (uint)-(int)(crc & 1));
            }
        }

        return ~crc;
    }
}

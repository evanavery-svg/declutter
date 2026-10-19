using System.Text;

namespace Declutter.Core.Util;

public static class TextDecoding
{
    /// <summary>
    /// Decodes console-tool output whose encoding isn't known up front: honours a BOM, recognises
    /// BOM-less UTF-16 by its zero bytes, then tries strict UTF-8, then falls back to Latin-1.
    /// </summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        int sample = Math.Min(bytes.Length, 4096);
        int oddZeros = 0;
        for (int i = 1; i < sample; i += 2)
        {
            if (bytes[i] == 0) oddZeros++;
        }
        if (sample >= 4 && oddZeros > sample / 4) return Encoding.Unicode.GetString(bytes);

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}

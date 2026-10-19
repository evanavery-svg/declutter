using System.Globalization;
using Declutter.Core.Util;

namespace Declutter.Core.Tests;

public class UtilTests
{
    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1, "1 byte")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(10L * 1024 * 1024, "10.0 MB")]
    [InlineData(150L * 1024 * 1024 * 1024, "150 GB")]
    [InlineData(-5, "0 bytes")]
    public void ByteSize_Formats(long bytes, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expected, ByteSize.Format(bytes));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void PathUtil_ContainmentIsSegmentAware()
    {
        string root = Path.Combine(Path.GetTempPath(), "x", "Temp");
        Assert.True(PathUtil.IsSameOrUnder(Path.Combine(root, "a"), root));
        Assert.True(PathUtil.IsSameOrUnder(root, root + Path.DirectorySeparatorChar));
        Assert.False(PathUtil.IsSameOrUnder(root + "2", root));
        Assert.False(PathUtil.IsStrictlyUnder(root, root));
    }

    [Fact]
    public void FileActivityLog_AppendsLines()
    {
        string dir = Path.Combine(Path.GetTempPath(), "declutter-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new FileActivityLog(dir);
            log.Write("Test", "first");
            log.Write("Test", "second");

            var lines = File.ReadAllLines(log.CurrentFile);
            Assert.Equal(2, lines.Length);
            Assert.Contains("[Test]  second", lines[1]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class TextDecodingTests
{
    [Fact]
    public void Utf16WithBom() => Assert.Equal("<Tasks>", TextDecoding.Decode([0xFF, 0xFE, .. System.Text.Encoding.Unicode.GetBytes("<Tasks>")]));

    [Fact]
    public void Utf16WithoutBom() => Assert.Equal("<Tasks>", TextDecoding.Decode(System.Text.Encoding.Unicode.GetBytes("<Tasks>")));

    [Fact]
    public void Utf8() => Assert.Equal("café", TextDecoding.Decode(System.Text.Encoding.UTF8.GetBytes("café")));

    [Fact]
    public void FallsBackToLatin1() => Assert.Equal("café", TextDecoding.Decode([0x63, 0x61, 0x66, 0xE9]));
}

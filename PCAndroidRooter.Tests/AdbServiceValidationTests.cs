using PCAndroidRooter.Services;
using Xunit;

namespace PCAndroidRooter.Tests;

public class AdbServiceValidationTests
{
    [Theory]
    [InlineData("emulator-5554", true)]
    [InlineData("R58M12ABCDE", true)]
    [InlineData("1A2B3C:4.0", true)]
    [InlineData("abc_def-1.2", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("serial; rm -rf /", false)]
    [InlineData("serial$(whoami)", false)]
    [InlineData("serial`id`", false)]
    [InlineData("a b", false)]
    public void IsValidSerial_AcceptsOnlySafeChars(string serial, bool expected)
    {
        Assert.Equal(expected, AdbService.IsValidSerial(serial));
    }

    [Theory]
    [InlineData("/dev/block/by-name/boot", true)]
    [InlineData("/dev/block/bootdevice/by-name/boot_a", true)]
    [InlineData("/data/local/tmp/boot.img", true)]
    [InlineData("/dev/block/../etc/passwd", false)]
    [InlineData("", false)]
    [InlineData("/data/local/tmp/../data/data", false)]
    public void IsValidBlockPath_AllowsExpectedPaths(string path, bool expected)
    {
        Assert.Equal(expected, AdbService.IsValidBlockPath(path));
    }

    [Theory]
    [InlineData("com.topjohnwu.magisk", true)]
    [InlineData("com.android.chrome", true)]
    [InlineData("pkg;evil", false)]
    [InlineData("pkg name", false)]
    [InlineData("", false)]
    public void IsValidPackageName_ChecksFormat(string pkg, bool expected)
    {
        Assert.Equal(expected, AdbService.IsValidPackageName(pkg));
    }

    [Theory]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData("../evil.jpg", "evil.jpg")]
    [InlineData("a/b/c.png", "c.png")]
    [InlineData("name with space.txt", "name_with_space.txt")]
    [InlineData("", "unknown")]
    public void SanitizeFileName_RemovesTraversalAndUnsafeChars(string input, string expected)
    {
        Assert.Equal(expected, AdbService.SanitizeFileName(input));
    }
}

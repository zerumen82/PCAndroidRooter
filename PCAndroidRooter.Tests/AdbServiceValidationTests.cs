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
    // Backup/restore paths that were previously (and wrongly) rejected
    [InlineData("/data/app/com.example.app/base.apk", true)]
    [InlineData("/data/app/~~AbC1==/com.example.app-XYZ==/base.apk", true)]
    [InlineData("/sdcard/DCIM/Camera/photo_001.jpg", true)]
    [InlineData("/sdcard/My Documents/report final.pdf", true)]
    [InlineData("/sdcard/WhatsApp/Databases/msgstore.db", true)]
    [InlineData("/data/data/com.android.providers.telephony/databases/mmssms.db", true)]
    [InlineData("/storage/emulated/0/Download/file.zip", true)]
    [InlineData("/dev/block/by-name/boot", true)]
    [InlineData("/data/local/tmp/new-boot.img", true)]
    // Rejected: traversal, shell metacharacters, relative, empty
    [InlineData("/sdcard/../system/etc/passwd", false)]
    [InlineData("/sdcard/file; rm -rf /", false)]
    [InlineData("/sdcard/$(whoami).jpg", false)]
    [InlineData("/sdcard/`id`.jpg", false)]
    [InlineData("/sdcard/a|b.jpg", false)]
    [InlineData("/sdcard/a&b.jpg", false)]
    [InlineData("sdcard/file.txt", false)]
    [InlineData("", false)]
    [InlineData("/sdcard/file\nname.jpg", false)]
    public void IsValidRemoteFilePath_AllowsBackupPaths_BlocksTraversal(string path, bool expected)
    {
        Assert.Equal(expected, AdbService.IsValidRemoteFilePath(path));
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

    [Theory]
    // Unlocked
    [InlineData("unlocked: yes", true)]
    [InlineData("(BOOTLOADER) unlocked!", true)]
    [InlineData("Device unlocked: true", true)]
    [InlineData("unlocked: yes (adb ro.boot.vbmeta.device_state=unlocked)", true)]
    [InlineData("already unlocked", true)]
    // Locked
    [InlineData("unlocked: no", false)]
    [InlineData("unlocked: false", false)]
    [InlineData("Device unlocked: false", false)]
    [InlineData("unlocked: no (adb ro.boot.verifiedbootstate=green)", false)]
    [InlineData("(BOOTLOADER) unlocked: no", false)]
    // Unknown
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("garbage output without state", null)]
    [InlineData("unlocked: unknown (sin props de bootloader)", null)]
    public void ParseBootloaderUnlocked_ParsesStates(string? output, bool? expected)
    {
        Assert.Equal(expected, AdbService.ParseBootloaderUnlocked(output));
    }
}

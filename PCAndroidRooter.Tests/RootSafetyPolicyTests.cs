using PCAndroidRooter.Models;
using PCAndroidRooter.Services;
using Xunit;

namespace PCAndroidRooter.Tests;

public class RootSafetyPolicyTests
{
    [Theory]
    [InlineData(RootMethodType.KernelSU)]
    [InlineData(RootMethodType.CustomRecovery)]
    [InlineData(RootMethodType.TemporaryRoot)]
    [InlineData(RootMethodType.AdbExploit)]
    public void FakeMethods_AreUnsupported(RootMethodType type)
    {
        Assert.True(RootSafetyPolicy.IsFakeOrUnsupported(type));
        Assert.False(RootSafetyPolicy.IsDestructiveUnlock(type));
    }

    [Theory]
    [InlineData(RootMethodType.OneClickRoot)]
    [InlineData(RootMethodType.MagiskPatch)]
    [InlineData(RootMethodType.FastbootBoot)]
    public void RealRootMethods_DoNotUnlock(RootMethodType type)
    {
        Assert.False(RootSafetyPolicy.IsFakeOrUnsupported(type));
        Assert.False(RootSafetyPolicy.IsDestructiveUnlock(type));
    }

    [Theory]
    [InlineData(RootMethodType.BootloaderUnlock)]
    [InlineData(RootMethodType.MtkClientUnlock)]
    public void UnlockMethods_AreExplicitlyDestructive(RootMethodType type)
    {
        Assert.True(RootSafetyPolicy.IsDestructiveUnlock(type));
        Assert.False(RootSafetyPolicy.IsFakeOrUnsupported(type));
    }

    [Theory]
    [InlineData(0, 0, 0, false)]
    [InlineData(1, 0, 0, true)]
    [InlineData(0, 2, 0, true)]
    [InlineData(0, 0, 3, true)]
    public void BackupHasUserFiles_IgnoresEmptyAttempt(int apps, int media, int docs, bool expected)
    {
        Assert.Equal(expected, RootSafetyPolicy.BackupHasUserFiles(apps, media, docs));
    }

    [Theory]
    [InlineData("unlocked", "1", "green", null, true)]
    [InlineData("locked", "0", "orange", null, false)]
    [InlineData("", "0", "", null, true)]
    [InlineData("", "1", "", null, false)]
    [InlineData("", "", "orange", null, true)]
    [InlineData("", "", "green", null, false)]
    [InlineData("", "", "yellow", null, false)]
    [InlineData("", "", "", "0", true)]
    [InlineData("", "", "", "1", false)]
    [InlineData("", "", "", null, null)]
    [InlineData("  ", "  ", "  ", "  ", null)]
    public void InterpretBootloaderState_IgnoresOemToggle(
        string? vbmeta, string? flash, string? verified, string? other, bool? expected)
    {
        // El toggle OEM (sys.oem_unlock_allowed) no es un argumento: no puede
        // convertir un bootloader cerrado en "desbloqueado".
        Assert.Equal(expected, RootSafetyPolicy.InterpretBootloaderState(vbmeta, flash, verified, other));
    }
}

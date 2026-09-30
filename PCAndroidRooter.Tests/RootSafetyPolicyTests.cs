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

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, false)]
    public void MayBeginRoot_OnlyWhenConfirmedUnlocked(bool? unlocked, bool expected)
    {
        Assert.Equal(expected, RootSafetyPolicy.MayBeginRoot(unlocked));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(null, false)]
    public void MayUnlockDuringRoot_OnlyWhenConfirmedLocked(bool? unlocked, bool expected)
    {
        Assert.Equal(expected, RootSafetyPolicy.MayUnlockDuringRoot(unlocked));
    }

    [Fact]
    public void MayFlashPermanent_RequiresEvidenceConfirmAndEitherTempRootOrInitBoot()
    {
        Assert.False(RootSafetyPolicy.MayFlashPermanent(null, true, true, true, true, false));
        Assert.False(RootSafetyPolicy.MayFlashPermanent(false, true, true, true, true, false));
        Assert.False(RootSafetyPolicy.MayFlashPermanent(true, false, true, true, true, false));
        Assert.False(RootSafetyPolicy.MayFlashPermanent(true, true, false, true, true, false));
        Assert.False(RootSafetyPolicy.MayFlashPermanent(true, true, true, false, true, false));
        Assert.False(RootSafetyPolicy.MayFlashPermanent(true, true, true, true, false, false));
        Assert.True(RootSafetyPolicy.MayFlashPermanent(true, true, true, true, true, false));
        Assert.True(RootSafetyPolicy.MayFlashPermanent(true, true, true, true, false, true));
        Assert.True(RootSafetyPolicy.MayFlashPermanent(true, true, true, true, false, false, cannotTempBoot: true));
        Assert.False(RootSafetyPolicy.MayFlashPermanent(false, true, true, true, false, false, cannotTempBoot: true));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SamsungOemUnlockSwitch_IsNotTheBootloader(string? prop, bool expected)
    {
        Assert.Equal(expected, RootSafetyPolicy.SamsungOemUnlockSwitchOn(prop));
    }

    [Theory]
    [InlineData("boot_a", "BOOT")]
    [InlineData("init_boot_b", "INIT_BOOT")]
    [InlineData("/dev/block/by-name/boot", "BOOT")]
    [InlineData("userdata", null)]
    public void HeimdallPitName_OnlyBootOrInitBoot(string partition, string? expected)
    {
        Assert.Equal(expected, RootSafetyPolicy.HeimdallPitName(partition));
    }

    [Theory]
    [InlineData(true, true, null, true)]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(false, true, null, false)]
    [InlineData(null, true, null, false)]
    [InlineData(true, false, null, false)]
    public void MayRestoreOriginal_BlocksKnownFingerprintMismatch(bool? unlocked, bool originalValid, bool? fingerprint, bool expected)
    {
        Assert.Equal(expected, RootSafetyPolicy.MayRestoreOriginal(unlocked, originalValid, fingerprint));
    }

    [Fact]
    public void ChooseBootPartition_PrefersInitBootOnActiveSlot()
    {
        var names = new HashSet<string> { "boot_a", "boot_b", "init_boot_a", "init_boot_b" };
        Assert.Equal("init_boot_a", RootSafetyPolicy.ChooseBootPartition(names, "_a"));
        Assert.Equal("init_boot_b", RootSafetyPolicy.ChooseBootPartition(names, "_b"));
        Assert.Equal("init_boot", RootSafetyPolicy.ChooseBootPartition(
            new HashSet<string> { "boot", "init_boot" }, null));
        Assert.Equal("boot_b", RootSafetyPolicy.ChooseBootPartition(
            new HashSet<string> { "init_boot_a", "boot_b" }, "_b"));
        Assert.Null(RootSafetyPolicy.ChooseBootPartition(new HashSet<string> { "userdata", "vendor_boot_a" }, "_a"));
    }

    [Theory]
    [InlineData("init_boot_a", true)]
    [InlineData("/dev/block/by-name/boot_b", true)]
    [InlineData("userdata", false)]
    [InlineData("vbmeta_a", false)]
    [InlineData("", false)]
    public void IsFlashableBootPartition_RejectsAnythingElse(string name, bool expected)
    {
        Assert.Equal(expected, RootSafetyPolicy.IsFlashableBootPartition(name));
    }

    [Theory]
    [InlineData("uid=0(root) gid=0(root)", true)]
    [InlineData("package:com.topjohnwu.magisk", false)]
    [InlineData("", false)]
    public void OutputShowsRootUid_IgnoresMagiskPackageName(string output, bool expected)
    {
        Assert.Equal(expected, RootSafetyPolicy.OutputShowsRootUid(output));
    }
}

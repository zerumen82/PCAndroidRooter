using PCAndroidRooter.Models;

namespace PCAndroidRooter.Services;

/// <summary>
/// Qué se puede ejecutar sin borrar el teléfono.
/// Desbloquear el bootloader hace un factory reset: no forma parte del root.
/// </summary>
public static class RootSafetyPolicy
{
    /// <summary>
    /// Métodos que la UI ofrecía como root y que no lo son
    /// (o que reiniciaban el teléfono sin rootear).
    /// </summary>
    public static bool IsFakeOrUnsupported(RootMethodType type) => type is
        RootMethodType.KernelSU or
        RootMethodType.CustomRecovery or
        RootMethodType.TemporaryRoot or
        RootMethodType.AdbExploit;

    public static bool IsDestructiveUnlock(RootMethodType type) => type is
        RootMethodType.BootloaderUnlock or
        RootMethodType.MtkClientUnlock;

    /// <summary>
    /// Un backup solo cuenta si ESTA ejecución sacó apps, fotos o documentos.
    /// Un contacts.txt suelto, o archivos de un intento anterior en la misma
    /// carpeta, no autorizan el wipe. Sigue siendo una copia parcial.
    /// </summary>
    public static bool BackupHasUserFiles(int apps, int media, int documents) =>
        apps > 0 || media > 0 || documents > 0;

    /// <summary>
    /// Estado real del bootloader a partir de props de arranque.
    /// <c>sys.oem_unlock_allowed</c> y <c>ro.oem_unlock_supported</c> NO entran:
    /// solo dicen que el toggle de desarrollador existe o está encendido.
    /// Tratarlos como "desbloqueado" hacía saltar el aviso y luego borrar el teléfono.
    /// null = desconocido; el caller debe tratarlo como bloqueado.
    /// </summary>
    public static bool? InterpretBootloaderState(
        string? vbmetaDeviceState,
        string? flashLocked,
        string? verifiedBootState,
        string? otherLocked = null)
    {
        var state = (vbmetaDeviceState ?? "").Trim();
        if (state.Equals("unlocked", StringComparison.OrdinalIgnoreCase)) return true;
        if (state.Equals("locked", StringComparison.OrdinalIgnoreCase)) return false;

        var fl = (flashLocked ?? "").Trim();
        if (fl == "0") return true;
        if (fl == "1") return false;

        var vbs = (verifiedBootState ?? "").Trim();
        if (vbs.Equals("orange", StringComparison.OrdinalIgnoreCase)) return true;
        if (vbs.Equals("green", StringComparison.OrdinalIgnoreCase) ||
            vbs.Equals("yellow", StringComparison.OrdinalIgnoreCase) ||
            vbs.Equals("red", StringComparison.OrdinalIgnoreCase))
            return false;

        // Samsung: ro.boot.other.locked. Solo después de las props estándar.
        var other = (otherLocked ?? "").Trim();
        if (other == "0") return true;
        if (other == "1") return false;

        return null;
    }

    /// <summary>El root solo empieza con el bootloader confirmado. null = parado.</summary>
    public static bool MayBeginRoot(bool? bootloaderUnlocked) => bootloaderUnlocked == true;

    /// <summary>
    /// El root automático desbloquea solo si el bootloader está CERRADO confirmado.
    /// Ya abierto: no se toca (no hace falta, y repetirlo puede volver a formatear).
    /// Desconocido: no se desbloquea.
    /// </summary>
    public static bool MayUnlockDuringRoot(bool? bootloaderUnlocked) => bootloaderUnlocked == false;

    /// <summary>
    /// Grabar solo si el bootloader está confirmado, hay original válido,
    /// el parche tiene evidencia Magisk, el usuario confirmó y
    /// (la prueba temporal vio uid=0, o es init_boot, que no se puede probar con fastboot boot).
    /// </summary>
    public static bool MayFlashPermanent(
        bool? bootloaderUnlocked,
        bool originalValid,
        bool patchEvidence,
        bool userConfirmed,
        bool tempBootVerified,
        bool isInitBoot,
        bool cannotTempBoot = false) =>
        bootloaderUnlocked == true
        && originalValid
        && patchEvidence
        && userConfirmed
        && (isInitBoot || tempBootVerified || cannotTempBoot);

    /// <summary>
    /// Samsung no entra en fastboot. Solo se manda a Download Mode si el interruptor
    /// Desbloqueo OEM está encendido. Ese interruptor no significa que ya esté desbloqueado.
    /// </summary>
    public static bool SamsungOemUnlockSwitchOn(string? oemUnlockAllowed) =>
        string.Equals(oemUnlockAllowed?.Trim(), "1", StringComparison.Ordinal);

    /// <summary>
    /// Nombre de partición PIT para heimdall. null si no es boot ni init_boot.
    /// </summary>
    public static string? HeimdallPitName(string? fastbootPartition)
    {
        var name = FastbootNameFromBlockPath(fastbootPartition);
        if (name is "boot" or "boot_a" or "boot_b") return "BOOT";
        if (name is "init_boot" or "init_boot_a" or "init_boot_b") return "INIT_BOOT";
        return null;
    }

    /// <summary>
    /// Restaurar el original. Si el fingerprint se pudo leer y no coincide, no.
    /// null = no se pudo leer (teléfono en fastboot): no bloquear la marcha atrás.
    /// </summary>
    public static bool MayRestoreOriginal(bool? bootloaderUnlocked, bool originalValid, bool? fingerprintMatches) =>
        bootloaderUnlocked == true && originalValid && fingerprintMatches != false;

    public static bool OutputShowsRootUid(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        return output.Contains("uid=0", StringComparison.Ordinal);
    }

    public static bool IsInitBootPartition(string? fastbootName) =>
        FastbootNameFromBlockPath(fastbootName).StartsWith("init_boot", StringComparison.Ordinal);

    public static bool IsFlashableBootPartition(string? fastbootName)
    {
        var name = FastbootNameFromBlockPath(fastbootName);
        return name is "boot" or "boot_a" or "boot_b" or "init_boot" or "init_boot_a" or "init_boot_b";
    }

    public static string FastbootNameFromBlockPath(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName)) return string.Empty;
        var name = pathOrName.Replace('\\', '/').Trim();
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        return name;
    }

    /// <summary>
    /// init_boot del slot activo gana a boot. Nunca elige el slot contrario.
    /// </summary>
    public static string? ChooseBootPartition(IReadOnlySet<string> names, string? activeSlot)
    {
        return Pick(names, "init_boot", activeSlot) ?? Pick(names, "boot", activeSlot);

        static string? Pick(IReadOnlySet<string> names, string baseName, string? slot)
        {
            if (slot is "_a" or "_b" && names.Contains(baseName + slot))
                return baseName + slot;
            if (names.Contains(baseName))
                return baseName;
            return null;
        }
    }
}

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
}

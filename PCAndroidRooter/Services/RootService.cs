using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PCAndroidRooter.Models;

namespace PCAndroidRooter.Services;

/// <summary>
/// Fachada del flujo de boot. Mantiene la API pública que MainViewModel ya
/// conocía (ExecuteMethodAsync, CommitPatchedBootAsync, DiscardPendingBootAsync,
/// RestoreOriginalBootAsync, RestoreBackupAsync) y delega en los servicios
/// separados: MagiskRootService (root), UnlockDangerService (zona de peligro:
/// desbloqueo/MTK) y RestoreService (restauración de backups).
///
/// Separación dura: el servicio de root (MagiskRootService) NO contiene
/// 'flashing unlock', 'oem unlock' ni MTKClient. Solo puede atravesar el
/// bootloader cerrado a través de IUnlockDanger (gate auditable).
/// </summary>
public class RootService : RootServiceBase
{
    private readonly AdbService _adbService;
    private readonly MagiskService _magiskService;
    private readonly MagiskRootService _magiskRoot;
    private readonly UnlockDangerService _unlockDanger;
    private readonly RestoreService _restore;

    public RootService(AdbService adbService, MagiskService magiskService)
    {
        _adbService = adbService;
        _magiskService = magiskService;
        _unlockDanger = new UnlockDangerService(adbService, magiskService);
        _magiskRoot = new MagiskRootService(adbService, magiskService, _unlockDanger);
        _restore = new RestoreService(adbService);

        // Redirigir log/eventos de los sub-servicios a esta fachada,
        // igual que cuando todo vivía en esta clase.
        foreach (var svc in new RootServiceBase[] { _magiskRoot, _unlockDanger, _restore })
        {
            svc.LogUpdated += msg => Log(msg);
            svc.MethodStatusChanged += (type, status) => RaiseMethodStatus(type, status);
        }
    }

    public async Task<RootMethodStatus> ExecuteMethodAsync(RootMethod method, string serial, CancellationToken ct)
    {
        if (RootSafetyPolicy.IsFakeOrUnsupported(method.Type))
        {
            Log($"'{method.Name}' no está implementado.");
            Log("  No se ha reiniciado ni modificado el teléfono.");
            Log("  Para rootear sin borrar datos: bootloader ya desbloqueado + Magisk Patch.");
            RaiseMethodStatus(method.Type, RootMethodStatus.NotSupported);
            return RootMethodStatus.NotSupported;
        }

        RaiseMethodStatus(method.Type, RootMethodStatus.Running);

        // Verificar que ADB responde antes de empezar
        if (!_adbService.CheckAdbHealthy())
        {
            var diagnosis = _adbService.DiagnoseAdbIssue();
            Log($"ERROR: {diagnosis ?? "ADB no responde."}");
            Log("  Prueba a reiniciar ADB o el PC.");
            RaiseMethodStatus(method.Type, RootMethodStatus.Failed);
            return RootMethodStatus.Failed;
        }

        try
        {
            RootMethodStatus status;
            switch (method.Type)
            {
                case RootMethodType.MagiskPatch:
                    status = await _magiskRoot.MagiskRootAsync(serial, ct);
                    break;
                case RootMethodType.BootloaderUnlock:
                    status = await _unlockDanger.UnlockBootloaderAsync(serial, ct);
                    break;
                case RootMethodType.OneClickRoot:
                    status = await _magiskRoot.OneClickRootAsync(serial, ct);
                    break;
                case RootMethodType.FastbootBoot:
                    status = await _magiskRoot.FastbootBootAsync(serial, ct);
                    break;
                case RootMethodType.MtkClientUnlock:
                    status = await _unlockDanger.MtkClientUnlockAsync(serial, ct);
                    break;
                default:
                    status = RootMethodStatus.NotSupported;
                    break;
            }

            RaiseMethodStatus(method.Type, status);
            return status;
        }
        catch (OperationCanceledException)
        {
            Log("Operación cancelada por el usuario.");
            CleanupDeviceTempFiles(serial);
            RaiseMethodStatus(method.Type, RootMethodStatus.Failed);
            return RootMethodStatus.Failed;
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
            CleanupDeviceTempFiles(serial);
            RaiseMethodStatus(method.Type, RootMethodStatus.Failed);
            return RootMethodStatus.Failed;
        }
    }

    private void CleanupDeviceTempFiles(string serial)
    {
        try
        {
            _adbService.Shell(serial, "rm -rf /data/local/tmp/magisk /data/local/tmp/boot_to_patch.img /data/local/tmp/ramdisk.cpio /data/local/tmp/new-boot.img /data/local/tmp/boot_to_patch.img.bak /data/local/tmp/boot_to_patch.img_tmp");
        }
        catch { }
    }

    public bool PendingIsInitBoot => _magiskRoot.PendingIsInitBoot;

    public async Task<RootMethodStatus> CommitPatchedBootAsync(string serial, CancellationToken ct)
        => await _magiskRoot.CommitPatchedBootAsync(serial, ct);

    public Task<RootMethodStatus> DiscardPendingBootAsync(string serial, CancellationToken ct)
        => _magiskRoot.DiscardPendingBootAsync(serial, ct);

    public async Task<RootMethodStatus> RestoreOriginalBootAsync(string serial, CancellationToken ct)
        => await _magiskRoot.RestoreOriginalBootAsync(serial, ct);

    public IAsyncEnumerable<string> RestoreBackupAsync(string serial, string backupDir, CancellationToken ct)
        => _restore.RestoreBackupAsync(serial, backupDir, ct);
}

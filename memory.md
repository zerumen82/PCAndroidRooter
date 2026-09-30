# memory.md — PCAndroidRooter

> Memoria del proyecto. Resumen de AGENTS.md + estado real del código.
> Si este archivo y AGENTS.md discrepan, manda AGENTS.md.

## Qué es

App WPF (.NET 9, `net9.0-windows`) que rootea un Android propio desde el PC.
Stack: MaterialDesign, CommunityToolkit.Mvvm. Tests: xUnit (`dotnet test PCAndroidRooter.sln`).

## Restricción del usuario (gana a todo)

**Root sin perder datos.** En un Android moderno con bootloader cerrado no existe root sin formatear:
el desbloqueo OEM / `fastboot flashing unlock` / MTK `seccfg unlock` hace factory reset y ningún backup de esta app lo evita.

## Regla de planificación (cualquier CLI)

Funcionalidad nueva → primero plan, aprobación del usuario, después código. **AGENTS.md manda siempre** sobre cualquier idea del plan. Estructura: contexto, cumplimiento de AGENTS.md, pasos con archivos, riesgos/límites, verificación con tests. Detalle en `.agents/skills/plan/SKILL.md` y `.codebuff/commands/plan.md`.

## Regla de corrección de problemas (cualquier CLI)

Corregir bug → 100% receptivo a las ideas del usuario (probar su sospecha primero), respetar AGENTS.md, revisar este archivo para **no repetir** intentos fallidos, máximo esfuerzo (causa raíz, tests) y buscar en internet soluciones nuevas si las conocidas no bastan. Detalle en `.agents/skills/fix/SKILL.md`.

## Regla de refactorización (cualquier CLI)

Refactor → plan aprobado antes de tocar código, **AGENTS.md manda**, revisar este archivo para no deshacer lo consolidado ni revivir código muerto, cero cambios de comportamiento, pasos pequeños con tests en verde tras cada uno. Detalle en `.agents/skills/refactor/SKILL.md`.

## Decisiones consolidadas (no deshacer)

### Seguridad
- El root automático desbloquea **solo** si el bootloader está cerrado confirmado (`MayUnlockDuringRoot`). Formatea; la UI avisa una vez antes de empezar.
- Bootloader abierto → solo Magisk, sin formatear. Estado desconocido → no desbloquear ni flashear.
- `sys.oem_unlock_allowed` / `ro.oem_unlock_supported` son el toggle de desarrollador, **no** el estado del bootloader.
- Prohibido: exploits, root temporal, `su` copiado a `/system`, reabrir `FindExploitablePid` / `ExploitPid`.
- La UI no promete "no perderás datos", "backup automático" ni "reversible" si el flujo formatea.

### Arquitectura de dos puertas
1. **Root**: `OneClickRoot` y `MagiskPatch` (recorrido completo). `FastbootBoot` no desbloquea.
2. **Zona de peligro (formatea)**: `BootloaderUnlock` y `MtkClientUnlock`. No son root, no se recomiendan, siempre con confirmación explícita. Nunca un paso del root.

### Camino Magisk
- `ChooseBootPartition`: `init_boot` del slot activo gana a `boot`; nunca el slot contrario.
- `FlashBootViaFastboot` valida el nombre de partición (`init_boot_a`, `boot_b`, …) y rechaza el resto.
- En `boot`: prueba con `fastboot boot` + `su -c id` (`uid=0`) antes de `CommitPatchedBootAsync`. Si falla, no se flashea.
- En `init_boot`: sin prueba en RAM; se graba solo esa partición en el mismo paso, con original válido y bootloader confirmado.
- Tener la app Magisk instalada no cuenta como root: solo `uid=0`.
- Botón **Restaurar boot** → `RestoreOriginalBootAsync`; no restaura si el fingerprint no coincide.
- Samsung: `adb reboot download` + Volumen Arriba, flash con heimdall (`tools\heimdall.exe`). Sin interruptor OEM no se reinicia.
- Sesión por dispositivo en `boot_sessions/{serial}.json`.

### Protecciones ya aplicadas (commit `594afa0` + camino Magisk)
- One-Click ya no llama a `UnlockBootloaderAsync` ni `SamsungUnlockFlowAsync`.
- `ExecuteMethodAsync` corta `KernelSU`, `CustomRecovery`, `TemporaryRoot` y `AdbExploit` con `RootSafetyPolicy.IsFakeOrUnsupported`.
- Esos 4 métodos están `NotSupported` y su botón Ejecutar sale deshabilitado.
- Recomendación: bootloader cerrado → nada; abierto → Magisk.
- Backup: carpeta nueva por intento (`CreateFreshBackupDirectory`); el gate de unlock/MTK exige `BackupHasUserFiles` de **este** intento (un `contacts.txt` solo no autoriza). Sin `skipBackup`.
- Tests de política: `PCAndroidRooter.Tests/RootSafetyPolicyTests.cs`.

## Estado de la implementación

Pasos 1–5 hechos; **el siguiente es el 6**:

1. ✅ `MayBeginRoot`, `MayFlashPermanent`, `MayUnlockDuringRoot`, `MayRestoreOriginal`.
2. ✅ `ChooseBootPartition` + `IsFlashableBootPartition`.
3. ✅ Prueba `fastboot boot` + `uid=0`; en `init_boot` graba en el mismo paso. (No usar `VerifyRoot` para decidir el flash: da por bueno el paquete Magisk sin `su`.)
4. ✅ Botón Restaurar boot.
5. ✅ Métodos falsos fuera de la ventana.
6. ⬜ **Pendiente**: partir `RootService` en parche / restaurar / desbloqueo. El de root no llama a `flashing unlock`, `oem unlock` ni MTKClient.
7. ⬜ Si `dd` falla, pedir el `init_boot.img` / `boot.img` oficial de la misma build (`ro.build.fingerprint`). No hecho.

## Pendientes / conocidos

- Partir `RootService` (paso 6 de arriba).
- Imagen oficial de boot si `dd` falla (paso 7 de arriba).
- Backup de fotos y APK parcial (`Take(50)`, `head`): no es copia del teléfono ni autoriza un wipe.
- Código muerto que **no** hay que recablear al root: `SamsungUnlockFlowAsync`, `AdbExploitRootAsync`, `TemporaryRootAsync`, `KernelSURootAsync`, `CustomRecoveryRootAsync`.

## Reglas al tocar código

- No meter unlock, wipe, Download Mode ni MTK dentro de One-Click, Magisk o Fastboot Boot.
- No flashear si `ParseBootloaderUnlocked` no es `true`, ni imágenes sin magic Android válido o sin parche Magisk (`BootImageValidator`).
- No marcar `Success` sin root verificado; `WaitingDevice` no es root completado.
- Batería fail-closed: lectura fiable ≥ 30 % antes de flash (`EnsureMinBatteryAsync`).
- Cancelar mata procesos hijos (MTK) y no deja el teléfono en fastboot sin flashear.
- Serial/rutas: `AdbService.IsValidSerial`, `SanitizeSerialForPath`, `IsValidBlockPath`, `IsValidRemoteFilePath`. Rechazar `..`.
- UI: ADB/fastboot en `Task.Run`; sin `Thread.Sleep` / `.Wait()` en comandos de vista.
- Tests nuevos de política en `PCAndroidRooter.Tests`, sin dispositivo; `dotnet test PCAndroidRooter.sln` en verde.

## Git

- Remoto: `git@github.com:zerumen82/PCAndroidRooter.git`, rama `main`. Sin force-push.
- No commitear `bin/`, `obj/`, `dist/`, `out_exe/`, `mtkclient/`, `backup/` (bin/ y obj/ siguen trackeados de antes: no añadir más binarios).

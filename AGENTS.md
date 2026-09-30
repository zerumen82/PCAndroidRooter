# PCAndroidRooter — instrucciones para agentes

App WPF (.NET 9, `net9.0-windows`) que rootea un Android propio desde el PC.
Stack: MaterialDesign, CommunityToolkit.Mvvm. Tests: xUnit (`dotnet test PCAndroidRooter.sln`).

## Regla de planificación (obligatoria en cualquier CLI)

Cuando el usuario pida **añadir funcionalidad nueva**, antes de escribir código hay que elaborar un plan y mostrarlo. No implementar nada hasta que el usuario lo apruebe. **AGENTS.md manda siempre**: cualquier idea del plan que contradiga este archivo se descarta y se explica por qué.

Estructura obligatoria del plan:

1. **Contexto** — qué pide el usuario y qué parte de la app afecta.
2. **Cumplimiento de AGENTS.md** — reglas que aplican y cómo las respeta el plan.
3. **Pasos de implementación** — lista ordenada con archivos concretos.
4. **Riesgos y límites** — qué NO se hará y por qué.
5. **Impacto** — confirmar que no entorpezca el desarrollo (no rompa flujos, no bloquee el roadmap, no añada deuda) y que beneficie al proyecto.
6. **Verificación** — `dotnet test PCAndroidRooter.sln` en verde; tests de política nuevos en `PCAndroidRooter.Tests` si aplica.

Detalle completo en `.agents/skills/plan/SKILL.md` (formato abierto, válido en cualquier CLI con soporte de skills).

El usuario quiere **root sin perder datos**. Esa restricción gana a cualquier atajo.

## Regla dura

En un Android moderno con el bootloader cerrado **no existe root sin formatear**.
El desbloqueo OEM / `fastboot flashing unlock` / MTK `seccfg unlock` hace factory reset.
Ningún backup de esta app lo evita.

- El root automático **sí desbloquea** si el estado es cerrado confirmado (`MayUnlockDuringRoot` == true solo entonces). Eso formatea. La UI avisa una vez antes de empezar. El backup no recupera el teléfono.
- Si ya está abierto, no se desbloquea: solo Magisk, sin formatear.
- Estado desconocido: no se desbloquea y no se flashea.
- Tras el formateo la depuración USB se apaga. Si el teléfono no vuelve por ADB, no se declara root: el usuario termina el asistente, reactiva USB y pulsa otra vez. Esa segunda vez ya no formatea.
- `sys.oem_unlock_allowed=1` y `ro.oem_unlock_supported=1` son el toggle de desarrollador, **no** el bootloader. No usarlos como "ya desbloqueado".
- No implementar exploits, root temporal, ni "su" copiado a `/system`. No reabrir `FindExploitablePid` / `ExploitPid`.
- No prometer en la UI "no perderás datos", "backup automático" o "reversible" si el flujo formatea o no puede deshacer el flash.

## Dos puertas, y no se mezclan

### 1. Root

`OneClickRoot` y `MagiskPatch` hacen el recorrido completo. `FastbootBoot` no desbloquea.

Si el bootloader está cerrado confirmado, primero `UnlockBootloaderAsync` (formatea; hace falta backup con archivos de este intento). Si vuelve por ADB y el bootloader figura abierto, siguen con Magisk. Si ya estaba abierto, solo Magisk: extraer `init_boot` o `boot`, parchear, probar si se puede, grabar y comprobar `uid=0`. Guardan el original y saben restaurarlo.

### 2. Zona de peligro (formatea)

`BootloaderUnlock` y `MtkClientUnlock`. No son root. No salen como recomendados. El texto debe decir que **borran el teléfono** y que el backup no salva cuentas, chats ni datos de apps.

Un backup no es permiso para formatear. Si esta puerta sigue existiendo, es una decisión explícita del usuario, con confirmación, nunca un paso del root.

## Hecho ya (no deshacerlo)

Commit `594afa0` (`fix: root no longer wipes the phone`):

- One-Click ya no llama a `UnlockBootloaderAsync` ni a `SamsungUnlockFlowAsync`. Si el bootloader no está confirmado, para y no reinicia.
- Magisk y Fastboot Boot no reinician ni flashean si el bootloader no está confirmado.
- `ExecuteMethodAsync` corta `KernelSU`, `CustomRecovery`, `TemporaryRoot` y `AdbExploit` con `RootSafetyPolicy.IsFakeOrUnsupported` antes de tocar ADB.
- La UI ya no dice "No perderás datos" en el botón que antes formateaba. Esos cuatro métodos están `NotSupported` y el botón Ejecutar sale deshabilitado (`IsAvailable`).
- Recomendación: bootloader cerrado → no recomendar nada. Abierto → Magisk.
- Cada intento de backup usa carpeta nueva (`CreateFreshBackupDirectory`). Archivos de un intento viejo no cuentan.
- El gate de unlock/MTK exige `BackupHasUserFiles` (apps, fotos o documentos de **este** intento). Un `contacts.txt` solo no autoriza. Se eliminó `skipBackup`.
- Tests de la política: `PCAndroidRooter.Tests/RootSafetyPolicyTests.cs`.

## Hecho en el camino Magisk (no deshacerlo)

- `RootSafetyPolicy.ChooseBootPartition`: `init_boot` del slot activo gana a `boot`. Nunca el slot contrario.
- `FlashBootViaFastboot` recibe el nombre (`init_boot_a`, `boot_b`, …) y rechaza cualquier otra partición.
- El botón de root es el consentimiento. No hay un segundo diálogo para grabar.
- En `boot`: `fastboot boot`, y solo si `su -c id` muestra `uid=0` se llama a `CommitPatchedBootAsync`. Si la prueba falla, se reinicia al sistema instalado y no se flashea.
- En `init_boot`: no hay prueba en RAM (arrancaría mal). Se graba solo esa partición, en el mismo paso, si el original es válido y el bootloader está confirmado.
- Tener la app Magisk instalada no cuenta como root. One-Click solo se salta el proceso si `uid=0`.
- Sin copia original válida no se reinicia ni se flashea. `MayUnlockDuringRoot` sigue siendo falso: el root no desbloquea.
- Botón **Restaurar boot** → `RestoreOriginalBootAsync`. Si el fingerprint se lee y no coincide, no restaura.
- Samsung sí entra en el root. No tiene fastboot: `adb reboot download`, confirmar con Volumen Arriba (único paso físico) y grabar `BOOT` o `INIT_BOOT` con heimdall (`tools\heimdall.exe`). Sin el interruptor Desbloqueo OEM no se reinicia. Sin heimdall no se entra en Download Mode para flashear.
- KernelSU, ADB exploit, recovery y root temporal ya no están en la ventana.
- Sesión en `boot_sessions/{serial}.json` junto al exe.

## Qué falta

- Partir `RootService` en parche / restaurar / desbloqueo. El desbloqueo no lo puede llamar el root.
- Si `dd` de la partición falla, pedir el `init_boot.img` / `boot.img` oficial de esa misma build (`ro.build.fingerprint`). No está hecho.
- El backup de fotos y APK sigue siendo parcial (`Take(50)`, `head`). No es una copia del teléfono y no autoriza un wipe.
- Código muerto que no hay que volver a cablear al root: `SamsungUnlockFlowAsync`, `AdbExploitRootAsync`, `TemporaryRootAsync`, `KernelSURootAsync`, `CustomRecoveryRootAsync`.

## Orden de implementación

Los pasos 1 a 5 están hechos. El siguiente es el 6, y la imagen oficial si `dd` falla.

1. Hecho. `MayBeginRoot`, `MayFlashPermanent`, `MayUnlockDuringRoot`, `MayRestoreOriginal`.
2. Hecho. `ChooseBootPartition` + `IsFlashableBootPartition`.
3. Hecho. Prueba con `fastboot boot` y `uid=0`. Si pasa, graba solo. En `init_boot` graba esa partición en el mismo paso. No usar `VerifyRoot` para decidir el flash: ese método da por bueno el paquete Magisk sin `su`.
4. Hecho. Botón Restaurar boot.
5. Hecho. Esos métodos ya no salen en la ventana.
6. Pendiente. Partir `RootService`. Un tipo para el parche, otro para restaurar, otro para el desbloqueo. El de root no llama a `flashing unlock`, `oem unlock` ni a MTKClient.

## Regla de corrección de problemas (obligatoria)

Cuando el usuario pida **corregir un problema/bug**:

0. **Plan básico primero**: mostrar un plan breve (petición del usuario con sus ideas, contexto + `memory.md`, causa probable, pasos con archivos, verificación, impacto, límites) y esperar su aprobación antes de editar código. El plan debe confirmar que lo que se va a hacer **no entorpezca el desarrollo** (no rompa flujos, no bloquee el roadmap, no añada deuda) y que **beneficie** al proyecto.
1. **Escuchar al usuario primero**: ser 100% receptivo a sus ideas; probar su sospecha/solución propuesta antes que ninguna otra. Desviarse solo con su acuerdo.
2. **Leer AGENTS.md** y respetar todas sus reglas (nada de unlock en el root, nada de exploits, etc.).
3. **Revisar `memory.md`** para no repetir soluciones ya intentadas ni deshacer decisiones consolidadas.
4. **Máximo esfuerzo**: causa raíz antes que parche, diagnóstico con tests, no parar en el primer intento.
5. **Buscar en internet** soluciones nuevas cuando las conocidas no bastan (mensaje de error exacto + versión de la librería), contrastando con documentación oficial.
6. Cerrar con `dotnet test PCAndroidRooter.sln` en verde y un resumen de qué se probó sin éxito (candidato a anotar en `memory.md`).

Detalle completo en `.agents/skills/fix/SKILL.md`.

## Regla de refactorización (obligatoria)

Cuando el usuario pida **refactorizar / partir / reorganizar código**:

1. **Plan primero**: aplicar la regla de planificación (plan mostrado y aprobado antes de tocar código).
2. **AGENTS.md manda**: ninguna idea del plan puede contradecirlo (p. ej. el paso 6 de partir `RootService` no puede dar acceso al unlock al root).
3. **Revisar `memory.md`** para no deshacer lo consolidado ni revivir código muerto (`SamsungUnlockFlowAsync`, `AdbExploitRootAsync`, `TemporaryRootAsync`, `KernelSURootAsync`, `CustomRecoveryRootAsync`).
4. **Cero cambios de comportamiento**: mismo resultado externo; bug detectado se corrige aparte.
5. **Pasos pequeños**: cada paso compila y deja `dotnet test PCAndroidRooter.sln` en verde antes del siguiente.
6. Si se toca una decisión anotada en `memory.md`, actualizarla al cerrar.

Detalle completo en `.agents/skills/refactor/SKILL.md`.

## Al tocar el código

- No volver a meter unlock, wipe, Download Mode ni MTK dentro de One-Click, Magisk o Fastboot Boot.
- No flashear si `ParseBootloaderUnlocked` no es `true`.
- No flashear una imagen sin magic Android válido ni sin evidencia de parche Magisk (`BootImageValidator`).
- No tratar un backup vacío, viejo o de solo contactos como éxito.
- No marcar `Success` si el root no se verificó. `WaitingDevice` no es root completado.
- Batería: fail-closed, lectura fiable ≥ 30 % antes de un flash (`EnsureMinBatteryAsync`).
- Cancelar tiene que matar procesos hijos (MTK) y no dejar el teléfono en fastboot si todavía no se flasheó.
- Serial y rutas: seguir `AdbService.IsValidSerial`, `SanitizeSerialForPath`, `IsValidBlockPath`, `IsValidRemoteFilePath`. Rechazar `..`.
- UI: el trabajo de ADB/fastboot va en `Task.Run`. No bloquear el hilo de WPF. No hacer `Thread.Sleep` / `.Wait()` en comandos de la vista.
- Tests nuevos de política van en `PCAndroidRooter.Tests` y no necesitan dispositivo. `dotnet test PCAndroidRooter.sln` tiene que seguir en verde.

## Qué no hacer

- No implementar ni mejorar exploits ADB, Dirty COW, root temporal ni un KernelSU falso.
- No auto-desbloquear "para poder rootear".
- No decir que el backup actual es una copia del teléfono.
- No commitear `bin/`, `obj/`, `dist/` (sigue trackeado de antes: no añadir más binarios), `out_exe/`, `mtkclient/` ni `backup/`.
- No force-push. El remoto es `git@github.com:zerumen82/PCAndroidRooter.git`, rama `main`.

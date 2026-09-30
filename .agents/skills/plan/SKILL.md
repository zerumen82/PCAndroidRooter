---
name: plan
description: Elabora un plan aprobado por el usuario antes de añadir funcionalidad nueva. AGENTS.md manda siempre sobre cualquier idea del plan. Usar cuando el usuario pida una funcionalidad nueva o invoque /plan.
---

# Plan para funcionalidad nueva

Antes de escribir **cualquier** código de una funcionalidad nueva, elabora un plan y muéstralo al usuario. No implementes nada hasta que el usuario apruebe el plan.

## Fuente de verdad

**AGENTS.md manda siempre.** Antes de planificar, lee `AGENTS.md` (y `memory.md` como resumen de apoyo). Si alguna idea del plan contradice AGENTS.md, se descarta y se explica por qué. Nunca propongas:

- Meter unlock, wipe, Download Mode ni MTK dentro de One-Click, Magisk o Fastboot Boot.
- Flashear sin bootloader confirmado (`ParseBootloaderUnlocked == true`) o sin validar la imagen (`BootImageValidator`).
- Exploits, root temporal, o revivir el código muerto (`SamsungUnlockFlowAsync`, `AdbExploitRootAsync`, `TemporaryRootAsync`, `KernelSURootAsync`, `CustomRecoveryRootAsync`).
- Promesas en la UI de "no perderás datos" si el flujo formatea.

## Estructura obligatoria del plan

1. **Contexto** — qué pide el usuario y qué parte de la app afecta.
2. **Cumplimiento de AGENTS.md** — cita las reglas que aplican y cómo las respeta el plan. Si hay conflicto, decláralo aquí.
3. **Pasos de implementación** — lista ordenada, cada paso con el/los archivos a tocar.
4. **Riesgos y límites** — qué NO se hará y por qué (según AGENTS.md).
5. **Verificación** — cómo se comprueba: `dotnet test PCAndroidRooter.sln` en verde, tests de política nuevos en `PCAndroidRooter.Tests` si aplica, typecheck.

## Reglas del plan

- Usa la herramienta de todos del CLI (todo list) para registrar los pasos y mantenerlos actualizados durante la implementación.
- Pregunta al usuario si hay decisiones ambiguas antes de cerrar el plan.
- Tras la aprobación, implementa siguiendo el plan paso a paso y verifica con los tests.

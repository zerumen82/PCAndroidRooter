---
name: refactor
description: Refactoriza con plan aprobado por el usuario, sin cambiar el comportamiento, respetando las reglas del proyecto (AGENTS.md), revisando memory.md para no deshacer decisiones consolidadas ni revivir código muerto, en pasos pequeños con tests en verde tras cada uno. Usar cuando el usuario pida refactorizar, reorganizar o "partir" código.
---

# Refactorización

Cuando el usuario pida refactorizar, reorganizar o partir código:

## 1. Plan antes de tocar código

- Refactor es cambio estructural: aplica la skill `plan` — elabora el plan, muéstralo y espera aprobación antes de implementar.
- **Las instrucciones del proyecto mandan siempre.** Si una idea del plan contradice `AGENTS.md` (o el equivalente del proyecto), se descarta y se explica por qué.

## 2. Contexto antes de mover nada

- Lee las instrucciones del proyecto (`AGENTS.md` en la raíz; `CLAUDE.md` si existe).
- Revisa `memory.md` (o equivalente) para **no deshacer** decisiones consolidadas ni "hecho ya (no deshacerlo)", y **no revivir** código muerto marcado como tal.
- Detecta primero el comportamiento actual (tests existentes, llamadas, flujos) para poder comprobar que no cambia.

## 3. Reglas de la refactorización

- **Cero cambios de comportamiento**: mismo resultado externo, distinta estructura interna. Si se detecta un bug durante el refactor, se separa: primero refactor, bug aparte.
- **Pasos pequeños y verificables**: cada paso compila y deja los tests en verde antes de pasar al siguiente.
- **Sin cambios de lado**: no mezclar el refactor con features nuevas, renombres de API públicas ni cambios de formato masivos.
- Mantener las convenciones del proyecto (nombres, estructura de carpetas, estilo existente).

## 4. Verificación

- Build/typecheck tras cada paso.
- Tests del proyecto en verde al final de cada paso y al cerrar.
- Si el proyecto no tiene tests para la zona tocada, proponer añadirlos antes de mover código (caracterización).

## 5. Cierre

- Resumen: qué se movió/partió, qué quedó igual y qué verificación se hizo.
- Si el refactor toca decisiones anotadas en `memory.md`, actualizar `memory.md`.

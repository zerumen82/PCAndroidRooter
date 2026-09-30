---
description: Corrige un problema/bug empezando con un plan básico aprobado por el usuario
---

# Fix con plan básico

Cuando el usuario pida corregir un problema, **primero elabora el plan básico y muéstralo**. No toques código hasta que el usuario lo apruebe (salvo que el diagnóstico trivial lo haga obvio y el usuario diga "directo").

## Fuente de verdad

**AGENTS.md manda siempre.** Lee `AGENTS.md` y revisa `memory.md` antes de proponer nada. Ninguna solución puede contradecir las reglas del proyecto (nada de unlock en el root, nada de exploits, no revivir código muerto, etc.). Si una idea del usuario choca con una regla dura, explícalo y ofrece lo más cercano a lo que pide.

## Plan básico obligatorio

1. **Petición** — el problema tal como lo describe el usuario, con sus palabras, más cualquier idea/sospecha suya. Sus ideas van primero: se evalúan antes que ninguna otra.
2. **Contexto** — archivos y flujos afectados; qué dice `memory.md` sobre esa zona (intentos previos, decisiones "no deshacer").
3. **Causa probable** — hipótesis de causa raíz (puede haber más de una; se listan por orden).
4. **Pasos de corrección** — lista corta y ordenada con archivos concretos. Cambios mínimos, sin reescribir de más.
5. **Verificación** — reproducción del fallo (test que falle → pase), `dotnet test PCAndroidRooter.sln` en verde.
6. **Impacto** — confirmar que la corrección **no entorpezca el desarrollo** (no rompa flujos existentes, no bloquee el roadmap como el paso 6, no añada deuda) y que **beneficie** al proyecto. Si el beneficio es dudoso, plantearlo antes de actuar.
7. **Límites** — qué NO se tocará según AGENTS.md.

Después del plan: implementar paso a paso, con máximo esfuerzo (no parar en el primer intento) y **buscar en internet** el mensaje de error exacto + versión de librería si las soluciones conocidas no bastan. Al cerrar: qué era, qué se cambió y qué se probó sin éxito (candidato a anotar en `memory.md`).

---
name: fix
description: Corrige problemas/bugs siendo 100% receptivo a las ideas del usuario, respetando las reglas del proyecto (AGENTS.md), revisando memory.md para no repetir intentos fallidos, con máximo esfuerzo, y buscando en internet soluciones nuevas si las conocidas no bastan. Usar cuando el usuario pida arreglar, corregir o depurar algo.
---

# Corrección de problemas

Cuando el usuario pida corregir un problema o bug:

## 0. Plan básico antes de tocar código

Elabora un **plan básico** y muéstralo antes de editar nada (salvo diagnóstico trivial y acuerdo del usuario):

1. **Petición** — el problema en palabras del usuario + sus ideas/sospechas (van primero).
2. **Contexto** — archivos/flujos afectados y qué dice `memory.md` de esa zona.
3. **Causa probable** — hipótesis de causa raíz, ordenadas.
4. **Pasos de corrección** — cortos, con archivos concretos, cambios mínimos.
5. **Verificación** — test que reproduce el fallo (falle → pase) y suite del proyecto en verde.
6. **Impacto** — confirmar que la corrección **no entorpezca el desarrollo** (no rompa flujos existentes, no bloquee el roadmap, no añada deuda) y que **beneficie** al proyecto; si el beneficio es dudoso, plantearlo antes de actuar.
7. **Límites** — qué NO se tocará según las instrucciones del proyecto.

Usa la lista de tareas (todo list) del CLI para registrar los pasos y mantenerlos actualizados.

## 1. Escuchar al usuario (prioridad máxima)

- **Sé 100% receptivo a sus ideas.** Si el usuario propone una solución o sospecha, pruébala primero y dale prioridad. No la descartes sin haberla evaluado de verdad.
- Si crees que hay una vía mejor, dilo, pero pregunta antes de desviarte de lo que pidió.
- Si una idea del usuario choca con una regla dura del proyecto, explícalo con claridad y ofrece la alternativa más cercana a lo que quiere.

## 2. Contexto antes de tocar código

- Lee las instrucciones del proyecto (`AGENTS.md` en la raíz; `CLAUDE.md` si existe) y respétalas siempre.
- Revisa `memory.md` (o el equivalente del proyecto) para **no repetir** soluciones ya intentadas, decisiones ya tomadas o código marcado como "no deshacer".
- Identifica la causa raíz antes de aplicar parches superficiales.

## 3. Máximo esfuerzo

- No pares en el primer intento: prueba hipótesis alternativas, añade diagnóstico (logs, tests que reproduzcan el fallo) y verifica la corrección con los tests/build del proyecto.
- Cambios mínimos y enfocados: corrige el problema sin reescribir de más.

## 4. Buscar soluciones nuevas

- Si las soluciones conocidas no funcionan o el error es desconocido, **busca en internet** (web_search / read_url) el mensaje de error exacto, la versión de la librería y soluciones recientes.
- Contrasta con la documentación oficial antes de aplicar hacks.
- Si una solución de internet choca con las reglas del proyecto, adáptala o descártala y explica por qué.

## 5. Cierre

- Verifica: build/typecheck y tests del proyecto en verde.
- Resume qué era el problema, qué se cambió y qué se probó sin éxito (candidato a anotar en `memory.md` para no repetirlo).

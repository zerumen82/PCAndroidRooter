# PCAndroidRooter

Aplicación de escritorio para Windows, desarrollada en **C# y WPF sobre .NET 9**, orientada a gestionar procesos de rooting de dispositivos Android propios.

## Tecnologías

- .NET 9 / WPF (`net9.0-windows`).
- MaterialDesign y CommunityToolkit.Mvvm.
- Integración con herramientas Android como ADB y fastboot.
- Pruebas con xUnit en `PCAndroidRooter.Tests`.

## Organización

- `PCAndroidRooter/`: aplicación y servicios.
- `PCAndroidRooter.Tests/`: pruebas de las políticas del proyecto.
- `PCAndroidRooter.sln`: solución .NET.
- [AGENTS.md](AGENTS.md): arquitectura, restricciones y tareas pendientes.
- [memory.md](memory.md): historial técnico y decisiones.

## Desarrollo

Requiere Windows y el SDK de .NET 9. Abre `PCAndroidRooter.sln` en un entorno compatible con WPF o utiliza:

```powershell
dotnet restore PCAndroidRooter.sln
dotnet build PCAndroidRooter.sln
dotnet test PCAndroidRooter.sln
```

Estos comandos describen el flujo de desarrollo; este README no certifica una ejecución reciente de las pruebas.

## Estado y compatibilidad

**Proyecto en desarrollo.** El soporte depende del fabricante, del estado del bootloader y de la imagen exacta del dispositivo. Consulta los límites y pendientes en `AGENTS.md` antes de utilizarlo.

El rooting y el flasheo pueden provocar pérdida de datos o impedir el arranque. Desbloquear el bootloader puede restablecer el teléfono. Las copias de seguridad actuales son parciales y no equivalen a una copia completa del dispositivo. No se garantiza compatibilidad universal ni conservación de los datos.

## Autor

Diego Tomico — [zerumen82](https://github.com/zerumen82).

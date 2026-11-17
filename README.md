# WinPulse Pro

Optimizador contextual y reversible para **Windows 10 / 11 x64** (.NET 10 / WPF). Ajusta energía, CPU, GPU, procesos, RAM y latencia de red según el perfil de uso que necesites, verificando en Windows qué cambios se aplican y permitiendo deshacerlos en cualquier momento.

## Descarga

Descarga la última versión del instalador (`WinPulseSetup.exe`) desde la sección oficial de **[Releases](https://github.com/Deathvks/winpulse-pro/releases/latest)**.

## Qué incluye

- **Perfiles de uso y detalle inmediato**: *Jugar (Máximo)*, *Estudiar / Trabajar*, *Estar Chill* y *De fábrica*, además de **perfiles personalizados por juego** (exportables e importables en JSON).
- **Automatización inteligente**: cambio automático de perfil al detectar juegos o al desconectar el cargador, y opción de iniciar minimizado junto al reloj.
- **Energía y planes de Windows**: selector de planes de energía instalados en el equipo (respetando directivas de Windows), límites de CPU, modo turbo y modo de energía de Windows.
- **FPS, GPU y CPU**: Modo Juego de Windows, HAGS, optimizaciones para juegos en ventana, temporizador de alta precisión (0,5 ms), núcleos activos y prioridad MMCSS.
- **Procesos y RAM**: pausa reversible de servicios pesados (`SysMain`, `WSearch`, `DiagTrack`), bloqueo de Game DVR y apps en segundo plano, prioridad para la ventana activa, ajuste adaptativo de memoria en segundo plano, monitor de recursos de 6 s y gestor de inicio de Windows.
- **Red y ratón**: ajuste TCP (Nagle), ahorro de energía USB/PCIe, opciones Ethernet (moderación de interrupciones y EEE), pruebas de ping y *ping bajo carga*.
- **Seguridad y transparencia**: captura inicial del equipo, historial navegable, *Deshacer último cambio* / *Restaurar configuración original*, ventana de *Estado de los ajustes* y punto de restauración.
- **Pruebas A/B**: comparativa guiada por juego con **PresentMon 2.x** y medición de consumo en batería.
- **Actualizaciones y privacidad**: aviso y botón de comprobación en la cabecera; estadísticas de uso locales opcionales y envío solo bajo consentimiento explícito desde *Privacidad*.

---

## Compilación y publicación (desarrollo)

1. Sube `<Version>` en `WinPulseOptimizer.csproj` y haz `push` a `main`.
2. El flujo **Publicar WinPulse** (`.github/workflows/release.yml`) compila el instalador ligero `WinPulseSetup.exe` y publica la release `vX.Y.Z` en este repositorio (`Deathvks/winpulse-pro`) usando `github.token`. Un `push` sin cambio de versión conserva la release existente.
3. Para generar un instalador sin conexión localmente, pasa el instalador oficial de .NET Desktop Runtime x64 a `Installer/Build-Installer.ps1` mediante `-RuntimeInstaller` (verifica el SHA-256 y genera `WinPulseSetupOffline.exe`).

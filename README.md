# WinPulse Pro

Optimizador contextual y reversible para **Windows 10 / 11 x64** (.NET 10 / WPF). Ajusta energía, CPU, GPU, procesos, RAM, respuesta de entrada y red según el perfil de uso que necesites, verificando en Windows qué cambios se aplican y permitiendo deshacerlos en cualquier momento.

## Descarga

Descarga la última versión del instalador (`WinPulseSetup.exe`) desde la sección oficial de **[Releases](https://github.com/Deathvks/winpulse-pro/releases/latest)**.

## Qué incluye

- **Perfiles de uso y recomendaciones contextuales**: *Jugar (Máximo)*, *Estudiar / Trabajar*, *Estar Chill* y *De fábrica*, con tarjeta superior **Recomendado al entrar** (limpieza rápida + RAM), bloque **Recomendado para tu plan elegido** con extras en 1 clic sin UAC y **perfiles personalizados por juego** (exportables e importables en JSON).
- **Automatización inteligente**: detección automática de juegos, cambio al desconectar el cargador e inicio minimizado junto al reloj (con menú contextual propio).
- **Inicio y energía**: resumen en vivo (CPU, RAM, GPU, procesos), comparación antes/después, selector de planes de energía instalados (respetando directivas de Windows), límites de CPU, turbo agresivo, modo de energía y punto de restauración.
- **FPS, GPU y CPU**:
  - Modo Juego, HAGS, optimizaciones para juegos en ventana, temporizador de alta precisión (0,5 ms), núcleos activos y prioridad MMCSS.
  - **Gráfica dedicada por juego (dGPU)**: asigna *Alto rendimiento* en Gráficos de Windows (`UserGpuPreferences`, sin UAC) de forma manual, automática al detectar juegos o desde perfiles por juego.
  - **Reparto inteligente de CPU mientras juegas**: prioridad `AboveNormal` para el juego activo y `BelowNormal` + Modo eficiencia (`EcoQoS`) para apps pesadas minimizadas (con restaurado automático y exclusiones editables para proteger programas concretos).
- **Procesos y RAM**:
  - Pausa persistente y reversible de servicios pesados (`SysMain`, `WSearch`, `DiagTrack`), apagado de Game DVR, bloqueo de apps UWP en segundo plano y prioridad de ventana activa.
  - **Respuesta visual (sin UAC)**: *Apertura instantánea de ventanas y menús* (`MenuShowDelay` y `MinAnimate`) y *Desactivar transparencias* (`EnableTransparency = 0`).
  - Limpieza de temporales y cachés gráficas (`%TEMP%`, `Windows\Temp` elevado, `D3DSCache`, NVIDIA y AMD + `ReTrim`), ajuste adaptativo de memoria en segundo plano, monitor de recursos de 6 s y gestor de inicio.
- **Red y ratón**: ajuste TCP/Nagle en adaptadores físicos reales (sin afectar interfaces VPN/túnel), ahorro de energía USB/PCIe, desactivación de aceleración del ratón (respuesta 1:1 sin UAC), opciones Ethernet (*Interrupt Moderation* / *EEE*) y pruebas de ping + *ping bajo carga*.
- **Seguridad y transparencia**: captura inicial del equipo, historial navegable, *Deshacer último cambio* / *Restaurar configuración original* y ventana de *Estado de los ajustes*.
- **Pruebas A/B**: comparativa guiada por juego con **PresentMon 2.x** y medición de consumo en batería.
- **Actualizaciones y privacidad**: comprobación en vivo en GitHub sin caché desde la cabecera; estadísticas locales opcionales y envío solo bajo consentimiento explícito desde *Privacidad*.

---

## Compilación y publicación (desarrollo)

1. Sube `<Version>` en `WinPulseOptimizer.csproj` y haz `push` a `main`.
2. El flujo **Publicar WinPulse** (`.github/workflows/release.yml`) compila el instalador ligero `WinPulseSetup.exe` y publica la release `vX.Y.Z` en este repositorio (`Deathvks/winpulse-pro`) usando `github.token`. Un `push` sin cambio de versión conserva la release existente.
3. Para generar un instalador sin conexión localmente, pasa el instalador oficial de .NET Desktop Runtime x64 a `Installer/Build-Installer.ps1` mediante `-RuntimeInstaller` (verifica el SHA-256 y genera `WinPulseSetupOffline.exe`).
4. Para empaquetar la edición **Microsoft Store (MSIX)**, ejecuta `Store/Build-Msix.ps1` (indica `-PackageName`, `-Publisher` y `-PublisherDisplayName` con los datos de identidad de Partner Center, o `-DevCert` para pruebas locales).

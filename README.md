# WinPulse Pro

Optimizador de Windows x64. El código fuente se mantiene en este repositorio privado. El instalador y las versiones estables se distribuyen en [WinPulse Releases](https://github.com/Deathvks/winpulse-releases/releases).

## Publicar una actualización

1. Sube `Version` en `WinPulseOptimizer.csproj` y haz push a `main`.
2. El flujo **Publicar WinPulse** crea `WinPulseSetup.exe`, con el runtime oficial de .NET incluido, y lo publica como release estable `vX.Y.Z`. Un push con una versión ya publicada conserva esa release.
3. WinPulse comprueba GitHub al abrir la ventana, como máximo una vez al día. Si encuentra una versión superior con instalador disponible, ofrece abrir la página de descarga.

El flujo usa el secreto de Actions `RELEASES_TOKEN` del repositorio privado. Para configurarlo, crea un token detallado de GitHub con acceso solo a `winpulse-releases` y permiso **Contents: Read and write**. Añádelo en **Settings → Secrets and variables → Actions**. El instalador y el código cliente solo necesitan acceso público de lectura a las releases.

# Estadísticas de WinPulse en Telegram

WinPulse envía recuentos solo después de que la persona acepte el envío. El Worker admite lotes por HTTPS y guarda el día UTC, la versión, los recuentos y un identificador aleatorio por lote para evitar duplicados. El resumen diario del bot contiene totales. La aplicación sigue usando el modo local mientras la compilación carezca de un destino y de los datos del aviso.

## Preparar el bot y el servidor

1. Crea tu propio bot con [BotFather](https://t.me/BotFather), abre su chat privado y envíale `/start`. Guarda el token de forma privada. En esta carpeta, ejecuta `.\Get-TelegramChatId.ps1 -BotUsername WinPulseEstadisticasBot` en PowerShell y sigue las instrucciones para obtener el ID del chat. Sustituye el nombre de usuario si usas otro bot. [Telegram Bot API](https://core.telegram.org/bots/api#getupdates)
2. En esta carpeta, inicia sesión con `npx wrangler login` y crea la base con `npx wrangler d1 create winpulse-usage`. Copia `wrangler.example.jsonc` a `wrangler.jsonc` y sustituye el identificador de la base. Ejecuta `npx wrangler d1 migrations apply winpulse-usage --remote` y `npx wrangler deploy`.
3. Configura `TELEGRAM_BOT_TOKEN` y `TELEGRAM_CHAT_ID` con `npx wrangler secret put <nombre>`. El token solo se usa en el Worker. No hace falta publicar un webhook de Telegram.
4. Configura `USAGE_PRIVACY_TEXT` con el aviso completo aprobado para este despliegue, mediante `npx wrangler secret put USAGE_PRIVACY_TEXT < privacy.txt` desde un shell que admita esa redirección, o en la configuración de Cloudflare. El Worker sirve este texto público en `/privacidad`.

La política debe identificar al responsable y su contacto, describir el consentimiento y su retirada, el uso de Cloudflare y Telegram, el tratamiento técnico de la IP durante la solicitud, los plazos de conservación, las transferencias que correspondan y cómo ejercer derechos. El Worker usa la IP en el limitador de solicitudes de Cloudflare y guarda en D1 únicamente los campos del lote. La tarea diaria elimina lotes de días fuera de la ventana de 30 días UTC. Los mensajes de totales se gestionan desde el chat privado de Telegram.

## Conectar WinPulse

Configura las siguientes variables en el entorno de compilación o en **GitHub → Settings → Secrets and variables → Actions → Variables** del repositorio privado.

| Variable | Valor |
| --- | --- |
| `WINPULSE_USAGE_ENDPOINT` | La URL HTTPS pública del Worker, con la ruta `/v1/usage`. |
| `WINPULSE_USAGE_CONTROLLER` | Nombre público de la persona o entidad responsable. |
| `WINPULSE_USAGE_CONTACT` | Correo público de contacto. |
| `WINPULSE_USAGE_POLICY` | URL HTTPS de la política, por ejemplo la ruta `/privacidad` del Worker. |

El envío se activa tras una nueva elección. WinPulse intenta enviar los lotes desde la ventana activa, como máximo una vez al día y fuera de los juegos detectados. Al retirar el consentimiento, la app cancela el envío en curso y borra el resumen y los lotes pendientes locales. El Worker prepara los resúmenes a las 09:00 UTC y omite los días sin actividad.

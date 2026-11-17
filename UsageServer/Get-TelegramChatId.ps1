[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BotUsername
)

$ErrorActionPreference = 'Stop'
$expectedUsername = $BotUsername.TrimStart('@')
$secret = Read-Host 'Token del bot de Telegram' -AsSecureString
$token = [pscredential]::new('TelegramBot', $secret).GetNetworkCredential().Password
try {
    if ($token -cnotmatch '^\d+:[A-Za-z0-9_-]+$') {
        throw 'El formato del token no coincide con el que entrega BotFather.'
    }

    $baseUrl = "https://api.telegram.org/bot$token"
    try {
        $bot = Invoke-RestMethod -Uri "$baseUrl/getMe" -TimeoutSec 15
    } catch {
        throw 'Telegram no pudo verificar el token. Comprueba el token y la conexión.'
    }

    if (!$bot.ok -or $bot.result.username -ne $expectedUsername) {
        throw 'El token no corresponde al nombre de usuario indicado.'
    }

    $code = [guid]::NewGuid().ToString('N').Substring(0, 12)
    Write-Host "Envía este código al chat privado de @$expectedUsername`: $code"
    $null = Read-Host 'Pulsa Intro después de enviarlo'
    try {
        $updates = Invoke-RestMethod -Uri "$baseUrl/getUpdates" -TimeoutSec 15
    } catch {
        throw 'Telegram no pudo leer los mensajes recientes del bot.'
    }

    $chatIds = @($updates.result | ForEach-Object {
        if ($_.message.chat.type -eq 'private' -and $_.message.text -ceq $code) { $_.message.chat.id }
    } | Sort-Object -Unique)

    if (!$updates.ok -or $chatIds.Count -ne 1 -or $chatIds[0] -notmatch '^[1-9]\d{0,19}$') {
        throw 'No encontré el código en un chat privado. Vuelve a ejecutar el comando y envía el nuevo código al bot indicado.'
    }

    Write-Output $chatIds[0]
} finally {
    $secret.Dispose()
    $token = $null
    $baseUrl = $null
}

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $DotNetExe,
    [Parameter(Mandatory)] [string] $IsccExe,
    [string] $RuntimeInstaller,
    [switch] $NoRestore
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repo 'WinPulseOptimizer.csproj'
[xml] $project = Get-Content -LiteralPath $projectPath -Raw
$appVersion = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
$runtimeVersion = $project.SelectSingleNode('/Project/PropertyGroup/RuntimeFrameworkVersion').InnerText
$dotnet = (Resolve-Path -LiteralPath $DotNetExe).Path
$iscc = (Resolve-Path -LiteralPath $IsccExe).Path
if ($env:WINPULSE_USAGE_ENDPOINT -and (!$env:WINPULSE_USAGE_CONTROLLER -or !$env:WINPULSE_USAGE_CONTACT -or !$env:WINPULSE_USAGE_POLICY)) {
    throw 'Configura el responsable, el contacto y la URL de la política de privacidad antes de activar el envío de estadísticas.'
}

# SHA-256 of the x64 Desktop Runtime installer verified against Microsoft's release metadata.
$runtimeHashes = @{
    '10.0.15' = 'E61B030C1673EDFA3C0844502EFF83198C46AF9E7DF70DFC6E2EFC5B11EA513A'
}
$runtimeHash = $runtimeHashes[$runtimeVersion]
if (!$runtimeHash) { throw "Falta registrar la huella del instalador oficial de .NET Desktop Runtime $runtimeVersion x64." }
$runtimeUrl = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/$runtimeVersion/windowsdesktop-runtime-$runtimeVersion-win-x64.exe"
$setupName = 'WinPulseSetup'
$runtimeArguments = @("/DRuntimeUrl=$runtimeUrl", "/DRuntimeSha256=$runtimeHash")
if ($RuntimeInstaller) {
    $runtime = (Resolve-Path -LiteralPath $RuntimeInstaller).Path
    if ((Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash -ne $runtimeHash) {
        throw "El archivo no coincide con el instalador oficial de .NET Desktop Runtime $runtimeVersion x64."
    }
    $setupName = 'WinPulseSetupOffline'
    $runtimeArguments += "/DOfflineRuntimeInstaller=$runtime"
}

$publishDir = Join-Path $repo "bin\InstallerPayload-$appVersion"
$setupDir = Join-Path $repo "bin\WinPulsePro-$appVersion"
$publishArguments = @('publish', $projectPath, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false',
    '-p:PublishSingleFile=true', '-o', $publishDir)
if ($NoRestore) { $publishArguments += '--no-restore' }
& $dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { throw "No se pudo publicar WinPulse. Código $LASTEXITCODE." }

$payload = Join-Path $publishDir 'WinPulseOptimizer.exe'
& $iscc "/O$setupDir" "/F$setupName" "/DAppVersion=$appVersion" "/DRuntimeVersion=$runtimeVersion" `
    "/DAppExecutable=$payload" @runtimeArguments (Join-Path $PSScriptRoot 'WinPulse.iss')
if ($LASTEXITCODE -ne 0) { throw "No se pudo compilar el instalador. Código $LASTEXITCODE." }

Get-Item -LiteralPath (Join-Path $setupDir "$setupName.exe")

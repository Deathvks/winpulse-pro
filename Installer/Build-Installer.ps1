[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $DotNetExe,
    [Parameter(Mandatory)] [string] $IsccExe,
    [Parameter(Mandatory)] [string] $RuntimeInstaller,
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
$runtime = (Resolve-Path -LiteralPath $RuntimeInstaller).Path

# SHA-512 from Microsoft's release metadata for the x64 Desktop Runtime installer.
$runtimeHashes = @{
    '10.0.15' = 'F838960962172F1A769C49FA600E6F60F56F8D08A28E3CB878890FFC2E3A7A2E1B65697588E23EFA767B8B8E8523EC01F55E875120507DDE16D9A0869102DAB3'
}
$runtimeHash = (Get-FileHash -LiteralPath $runtime -Algorithm SHA512).Hash
if ($runtimeHash -ne $runtimeHashes[$runtimeVersion]) {
    throw "El archivo no coincide con el instalador oficial de .NET Desktop Runtime $runtimeVersion x64."
}

$publishDir = Join-Path $repo "bin\InstallerPayload-$appVersion"
$setupDir = Join-Path $repo "bin\WinPulsePro-$appVersion"
$publishArguments = @('publish', $projectPath, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false',
    '-p:PublishSingleFile=true', '-o', $publishDir)
if ($NoRestore) { $publishArguments += '--no-restore' }
& $dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { throw "No se pudo publicar WinPulse. Código $LASTEXITCODE." }

$payload = Join-Path $publishDir 'WinPulseOptimizer.exe'
& $iscc "/O$setupDir" "/DAppVersion=$appVersion" "/DRuntimeVersion=$runtimeVersion" `
    "/DRuntimeInstaller=$runtime" "/DAppExecutable=$payload" (Join-Path $PSScriptRoot 'WinPulse.iss')
if ($LASTEXITCODE -ne 0) { throw "No se pudo compilar el instalador. Código $LASTEXITCODE." }

Get-Item -LiteralPath (Join-Path $setupDir 'WinPulseSetup.exe')

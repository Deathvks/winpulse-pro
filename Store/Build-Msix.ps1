[CmdletBinding()]
param(
    [string] $DotNetExe,
    [string] $SdkBinDir,
    [string] $PackageName = $(if ($env:WINPULSE_STORE_PACKAGE_NAME) { $env:WINPULSE_STORE_PACKAGE_NAME } else { 'Deathvks.WinPulsePro' }),
    [string] $Publisher = $(if ($env:WINPULSE_STORE_PUBLISHER) { $env:WINPULSE_STORE_PUBLISHER } else { 'CN=Deathvks' }),
    [string] $PublisherDisplayName = $(if ($env:WINPULSE_STORE_PUBLISHER_DISPLAY) { $env:WINPULSE_STORE_PUBLISHER_DISPLAY } else { 'Deathvks' }),
    [string] $CertificatePfx,
    [string] $CertificatePassword,
    [switch] $DevCert,
    [switch] $NoRestore
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repo 'WinPulseOptimizer.csproj'
[xml] $project = Get-Content -LiteralPath $projectPath -Raw
$appVersion = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($appVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "La versión del proyecto ('$appVersion') debe tener el formato X.Y.Z."
}
$msixVersion = "$appVersion.0"

if (!$DotNetExe) {
    $localDotnet = Join-Path $repo 'bin\InstallerTools\Dotnet10\dotnet.exe'
    $DotNetExe = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command 'dotnet.exe').Source }
}
$dotnet = (Resolve-Path -LiteralPath $DotNetExe).Path

function Resolve-SdkTool([string]$toolName) {
    if ($SdkBinDir) {
        $candidate = Join-Path $SdkBinDir $toolName
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    $localSdkRoot = Join-Path $repo 'bin\InstallerTools\WinSdkBuildTools'
    if (Test-Path -LiteralPath $localSdkRoot) {
        $found = Get-ChildItem -LiteralPath $localSdkRoot -Recurse -Filter $toolName -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -eq 'x64' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    foreach ($kitsRoot in @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin")) {
        if (Test-Path -LiteralPath $kitsRoot) {
            $found = Get-ChildItem -LiteralPath $kitsRoot -Recurse -Filter $toolName -ErrorAction SilentlyContinue |
                Where-Object { $_.Directory.Name -eq 'x64' } |
                Sort-Object FullName -Descending |
                Select-Object -First 1
            if ($found) { return $found.FullName }
        }
    }
    throw "No se encontró $toolName (instala Windows 10/11 SDK o Microsoft.Windows.SDK.BuildTools en bin\InstallerTools\WinSdkBuildTools)."
}

$makeappx = Resolve-SdkTool 'makeappx.exe'
$makepri = Resolve-SdkTool 'makepri.exe'
$signtool = Resolve-SdkTool 'signtool.exe'

$stagingDir = Join-Path $repo "bin\StorePayload-$appVersion"
$outputDir = Join-Path $repo "bin\WinPulseStore-$appVersion"
if (Test-Path -LiteralPath $stagingDir) { Remove-Item -LiteralPath $stagingDir -Recurse -Force }
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

$publishArgs = @(
    'publish', $projectPath,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=false',
    '-p:WinPulseDistribution=Store',
    '-o', $stagingDir
)
if ($NoRestore) { $publishArgs += '--no-restore' }
& $dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "No se pudo publicar la edición Microsoft Store. Código $LASTEXITCODE." }

Get-ChildItem -LiteralPath $stagingDir -Filter '*.pdb' -File -ErrorAction SilentlyContinue | Remove-Item -Force

$storeAssetsSource = Join-Path $PSScriptRoot 'Assets'
$storeAssetsTarget = Join-Path $stagingDir 'Assets'
New-Item -ItemType Directory -Path $storeAssetsTarget -Force | Out-Null
Copy-Item -Path (Join-Path $storeAssetsSource '*') -Destination $storeAssetsTarget -Recurse -Force

$manifestTemplate = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw -Encoding UTF8
$manifestContent = $manifestTemplate.
    Replace('__PACKAGE_NAME__', $PackageName).
    Replace('__PACKAGE_PUBLISHER__', $Publisher).
    Replace('__PUBLISHER_DISPLAY_NAME__', $PublisherDisplayName).
    Replace('__PACKAGE_VERSION__', $msixVersion)
$manifestPath = Join-Path $stagingDir 'AppxManifest.xml'
[IO.File]::WriteAllText($manifestPath, $manifestContent, [Text.UTF8Encoding]::new($false))

# Build a single merged resources.pri for Store Assets so scale-200 / unplated variants resolve inside one MSIX
$priConfig = Join-Path $outputDir 'priconfig.xml'
$priOut = Join-Path $stagingDir 'resources.pri'
& $makepri createconfig /cf $priConfig /dq es /pv 10.0.0 /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "No se pudo crear priconfig.xml. Código $LASTEXITCODE." }
$priXml = Get-Content -LiteralPath $priConfig -Raw -Encoding UTF8
$priXml = [regex]::Replace($priXml, '<packaging>[\s\S]*?</packaging>', '<packaging />')
$priXml = $priXml.Replace('<index root="\" startIndexAt="\">', '<index root="\" startIndexAt="Assets">')
[IO.File]::WriteAllText($priConfig, $priXml, [Text.UTF8Encoding]::new($false))
& $makepri new /pr $stagingDir /cf $priConfig /mn $manifestPath /of $priOut /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "No se pudo generar resources.pri. Código $LASTEXITCODE." }
Remove-Item -LiteralPath $priConfig -Force -ErrorAction SilentlyContinue

$msixPath = Join-Path $outputDir "WinPulsePro-$appVersion-x64.msix"
& $makeappx pack /d $stagingDir /p $msixPath /o
if ($LASTEXITCODE -ne 0) { throw "No se pudo empaquetar el MSIX. Código $LASTEXITCODE." }

if ($DevCert) {
    $devPfx = Join-Path $outputDir 'WinPulsePro_DevTest.pfx'
    $devCer = Join-Path $outputDir 'WinPulsePro_DevTest.cer'
    $devSecret = [Guid]::NewGuid().ToString('N')
    $secure = ConvertTo-SecureString -String $devSecret -Force -AsPlainText
    $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature `
        -FriendlyName 'WinPulse Pro Dev Test MSIX' -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    try {
        Export-PfxCertificate -Cert $cert -FilePath $devPfx -Password $secure | Out-Null
        Export-Certificate -Cert $cert -FilePath $devCer | Out-Null
        & $signtool sign /fd SHA256 /f $devPfx /p $devSecret $msixPath
        if ($LASTEXITCODE -ne 0) { throw "No se pudo firmar el MSIX de prueba. Código $LASTEXITCODE." }
    }
    finally {
        Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $devPfx -Force -ErrorAction SilentlyContinue
    }
}
elseif ($CertificatePfx) {
    $signArgs = @('sign', '/fd', 'SHA256', '/f', (Resolve-Path -LiteralPath $CertificatePfx).Path)
    if ($CertificatePassword) { $signArgs += @('/p', $CertificatePassword) }
    $signArgs += $msixPath
    & $signtool @signArgs
    if ($LASTEXITCODE -ne 0) { throw "No se pudo firmar el MSIX. Código $LASTEXITCODE." }
}

Get-Item -LiteralPath $msixPath

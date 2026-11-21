[CmdletBinding()]
param(
    [ValidateSet('local', 'nightly', 'release')] [string] $Channel = 'local',
    [ValidateRange(0, 65534)] [int] $BuildNumber = 0,
    [ValidatePattern('^\d{8}$')] [string] $BuildDate = [DateTime]::UtcNow.ToString('yyyyMMdd')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/ThixShot/ThixShot.csproj'
$output = Join-Path $root 'artifacts/packages'
# An isolated publish directory prevents stale files from entering the installer.
$staging = Join-Path $root 'artifacts/staging'
$publish = Join-Path $staging ('windows-' + [Guid]::NewGuid())
New-Item -ItemType Directory -Force $output, $publish | Out-Null
Push-Location $root
try {
    dotnet tool restore
    if ($LASTEXITCODE) { throw 'WiX konnte nicht wiederhergestellt werden.' }
    $properties = @("-p:Channel=$Channel", "-p:BuildNumber=$BuildNumber", "-p:BuildDate=$BuildDate")
    dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish @properties `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE) { throw 'Windows-Publish fehlgeschlagen.' }
    $files = @(Get-ChildItem -LiteralPath $publish)
    if ($files.Count -ne 1 -or $files[0].Name -ne 'ThixShot.exe') {
        throw ('Das MSI erwartet den vollständigen Einzeldatei-Publish. Inhalt: ' + ($files.Name -join ', '))
    }
    [xml] $settings = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
    $version = '{0}.{1}.{2}' -f $settings.Project.PropertyGroup.ThixShotMajor,
        $settings.Project.PropertyGroup.ThixShotMinor, $BuildNumber
    dotnet wix build packaging/windows/ThixShot.wxs -arch x64 -b "payload=$publish" `
        -d "AppVersion=$version" -pdbtype none -o (Join-Path $output 'ThixShot-win-x64.msi')
    if ($LASTEXITCODE) { throw 'Erstellung des MSI fehlgeschlagen.' }
} finally {
    Pop-Location
    $stagingRoot = (Resolve-Path -LiteralPath $staging).Path
    $created = Get-Item -LiteralPath $publish
    if ($created.Parent.FullName -eq $stagingRoot -and $created.Name -match '^windows-[0-9a-f-]{36}$') {
        Remove-Item -LiteralPath $created.FullName -Recurse -Force
    }
}

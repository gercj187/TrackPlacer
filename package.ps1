param (
    [switch]$NoArchive,
    [switch]$NoBuild,
    [string]$OutputDirectory = $PSScriptRoot
)

# Packt ZWEI Mods nach dem gewohnten Schema (Version.Build.Hotfix, Hotfix-Hochzählen, dist\ + backup\):
#   TrackPlacer      (Editor)  - info.json im Hauptordner,        Projekt TrackPlacer\TrackPlacer.csproj
#   TrackPlacerPlay  (Play)    - TrackPlacerPlay\Info.json,       Projekt TrackPlacerPlay\TrackPlacerPlay.csproj
# Ergebnis: dist\TrackPlacer_v<Version>.zip und dist\TrackPlacerPlay_v<Version>.zip, dazu ein Source-Backup.

$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

Write-Host ""
Write-Host ""
Write-Host ""
Write-Host "=== Packe die Mods ===" -ForegroundColor Cyan
Write-Host ""

$DistDir   = Join-Path $OutputDirectory "dist"
$BackupDir = Join-Path $OutputDirectory "backup"
$TmpDir    = Join-Path $DistDir "tmp"
New-Item -Path $DistDir   -ItemType Directory -Force | Out-Null
New-Item -Path $BackupDir -ItemType Directory -Force | Out-Null

# -------------------------------------------------
# VERSION PRÜFEN / AUTOMATISCH ERHÖHEN
#
# Schema: Version.Build.Hotfix
# 0.1.0   -> 0.1.01
# 0.00.2  -> 0.00.21
# 0.00.21 -> 0.00.22
# 1.23.45 -> 1.23.46
# -------------------------------------------------
function Get-NextVersion([string]$modId, [string]$modVersion)
{
    if ($modVersion -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
        throw "Ungültiges Versionsformat '$modVersion' ($modId). Erwartet wird Version.Build.Hotfix, z.B. 1.23.45."
    }
    $versionPart = $Matches[1]
    $buildPart   = $Matches[2]
    $hotfixPart  = $Matches[3]

    $zipPath = Join-Path $DistDir "${modId}_v${modVersion}.zip"
    if ($NoArchive -or -not (Test-Path $zipPath)) { return $modVersion }

    Write-Host "$modId ${modVersion} existiert bereits in dist." -ForegroundColor Yellow

    # Einstelliger Hotfix wird beim ersten Hochzählen erweitert: 0 -> 01, 2 -> 21 ...
    if ($hotfixPart.Length -eq 1) {
        $nextHotfix = "${hotfixPart}1"
    }
    else {
        $width = $hotfixPart.Length
        $number = [System.Numerics.BigInteger]::Parse($hotfixPart)
        $number = [System.Numerics.BigInteger]::Add($number, [System.Numerics.BigInteger]::One)
        $nextHotfix = $number.ToString().PadLeft($width, '0')
    }

    while ($true) {
        $candidateVersion = "${versionPart}.${buildPart}.${nextHotfix}"
        if (!(Test-Path (Join-Path $DistDir "${modId}_v${candidateVersion}.zip"))) { return $candidateVersion }

        Write-Host "$modId $candidateVersion existiert ebenfalls bereits in dist." -ForegroundColor Yellow
        $width = $nextHotfix.Length
        $number = [System.Numerics.BigInteger]::Parse($nextHotfix)
        $number = [System.Numerics.BigInteger]::Add($number, [System.Numerics.BigInteger]::One)
        $nextHotfix = $number.ToString().PadLeft($width, '0')
    }
}

# -------------------------------------------------
# Eine Mod packen
# -------------------------------------------------
function Pack-Mod([string]$infoPath, [string]$csproj, [string]$dllName, [string[]]$extraFiles)
{
    if (!(Test-Path $infoPath)) { throw "$infoPath fehlt!" }

    $modInfo    = Get-Content -Raw $infoPath | ConvertFrom-Json
    $modId      = $modInfo.Id
    $oldVersion = [string]$modInfo.Version
    $modVersion = Get-NextVersion $modId $oldVersion

    # info.json vor dem Build aktualisieren (auch die Kopie im Mods-Ordner trägt dann die neue Version)
    if ($modVersion -ne $oldVersion) {
        $modInfo.Version = $modVersion
        $modInfo | ConvertTo-Json -Depth 100 | Set-Content -Path $infoPath -Encoding UTF8
        Write-Host "Packe ${modId}: $oldVersion -> $modVersion" -ForegroundColor Yellow
    }

    Write-Host ""
    Write-Host "Mod-ID:  $modId"
    Write-Host "Version: $modVersion"

    if (-not $NoBuild) {
        Write-Host "Baue Release ..." -ForegroundColor Cyan
        & dotnet build (Join-Path $PSScriptRoot $csproj) -c Release --nologo | Out-Host  # nicht in den Rückgabewert
        if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen: $csproj (Exit-Code $LASTEXITCODE)." }
    }

    $dll = Join-Path $PSScriptRoot ((Split-Path $csproj -Parent) + "\bin\Release\net48\" + $dllName)
    if (!(Test-Path $dll)) { throw "$dllName fehlt ($dll) - Build ausgelassen oder fehlgeschlagen?" }

    $ZipOutDir = Join-Path $TmpDir $modId
    if (Test-Path $ZipOutDir) { Remove-Item $ZipOutDir -Recurse -Force }
    New-Item -Path $ZipOutDir -ItemType Directory -Force | Out-Null

    Copy-Item -Path $infoPath -Destination (Join-Path $ZipOutDir "Info.json") -Force
    Copy-Item -Path $dll -Destination $ZipOutDir -Force
    foreach ($optional in $extraFiles) {
        if (Test-Path $optional) { Copy-Item -Path $optional -Destination $ZipOutDir -Force }
    }

    if (-not $NoArchive) {
        $fileName = "${modId}_v${modVersion}.zip"
        Compress-Archive -Path (Join-Path $ZipOutDir "*") -DestinationPath (Join-Path $DistDir $fileName) -CompressionLevel Fastest -Force
        Write-Host "ZIP erstellt: dist\$fileName"
    }
    return "${modId}_v${modVersion}"
}

$editorTag = Pack-Mod "info.json" "TrackPlacer\TrackPlacer.csproj" "TrackPlacer.dll" @("LICENSE", "README.md", "TESTHINWEISE.md", "icon.png")
$playTag   = Pack-Mod "TrackPlacerPlay\Info.json" "TrackPlacerPlay\TrackPlacerPlay.csproj" "TrackPlacerPlay.dll" @("LICENSE", "TrackPlacerPlay\icon.png")

# =================================================
# SOURCE BACKUP - BIN/OBJ ABSOLUT AUSGESCHLOSSEN
# =================================================
if (-not $NoArchive) {
    $timestamp = Get-Date -Format "yyyy-MM-dd_HH-mm-ss"
    $sourceBackupName = "${editorTag}_${timestamp}.zip"
    $sourceBackupPath = Join-Path $BackupDir $sourceBackupName
    $sourceTmp = Join-Path $TmpDir "source"
    if (Test-Path $sourceTmp) { Remove-Item $sourceTmp -Recurse -Force }
    New-Item -Path $sourceTmp -ItemType Directory -Force | Out-Null

    $sourceFiles = Get-ChildItem -Path $PSScriptRoot -Recurse -File |
        Where-Object {
            $rel = $_.FullName.Substring($PSScriptRoot.Length).TrimStart('\','/')
            if ($rel -match '(^|[\\/])bin([\\/]|$)')    { return $false }
            if ($rel -match '(^|[\\/])obj([\\/]|$)')    { return $false }
            if ($rel -match '(^|[\\/])build([\\/]|$)')  { return $false }
            if ($rel -match '(^|[\\/])dist([\\/]|$)')   { return $false }
            if ($rel -match '(^|[\\/])backup([\\/]|$)') { return $false }
            # Quellformate + eingebettete Ressourcen (Icons, Texturen, Modelle) - ohne die baut das Projekt nicht
            return $_.Extension -in ".cs", ".csproj", ".txt", ".json", ".sln", ".ps1", ".md", ".png", ".jpg", ".obj", ".mtl"
        }

    foreach ($f in $sourceFiles) {
        $rel    = $f.FullName.Substring($PSScriptRoot.Length).TrimStart('\','/')
        $target = Join-Path $sourceTmp $rel
        New-Item -Path (Split-Path $target -Parent) -ItemType Directory -Force | Out-Null
        Copy-Item $f.FullName $target -Force
    }

    Compress-Archive -Path (Join-Path $sourceTmp "*") -DestinationPath $sourceBackupPath -CompressionLevel Optimal -Force
    Remove-Item $sourceTmp -Recurse -Force
    Write-Host "Source-Backup erstellt: backup\$sourceBackupName (Editor $editorTag, Play $playTag)"
}
else {
    Write-Host "Archivieren übersprungen (-NoArchive)"
}

if (Test-Path $TmpDir) { Remove-Item $TmpDir -Recurse -Force }

Write-Host ""
Write-Host "=== FERTIG ===" -ForegroundColor Green
Write-Host ""
Write-Host ""
Write-Host ""
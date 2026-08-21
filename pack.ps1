<#
.SYNOPSIS
    Builds RobotStudioUnityBridge (2026 and 2025 host targets) and packages each as a .rspak
    that RobotStudio's Add-Ins > Install can consume.

.DESCRIPTION
    RobotStudio's Install dialog only accepts .rspak (or .rmf), not a bare .rsaddin -- see
    README.md "Testing the Add-in" for how that requirement was reverse-engineered (the zip
    must contain a top-level "<Name>-<Version>/" folder holding manifest.xml and
    "RobotStudio/Add-In/<assembly>.rsaddin"). This script reproduces that layout so nobody has
    to hand-build it again.

    Two host builds exist because RobotStudio 2025 and 2026 load add-ins via incompatible
    runtimes (net48 vs. net10.0-windows) -- see src/RobotStudioUnityBridge2025's comments.
    Same source (linked, not copied), different <MinimumHostVersion>/<MinClientVersion>, so
    each build only installs into the RobotStudio version it actually targets.

.OUTPUTS
    dist/RobotStudioUnityBridge-<version>.rspak       (RobotStudio 2026)
    dist/RobotStudioUnityBridge2025-<version>.rspak   (RobotStudio 2025)
#>

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$distDir = Join-Path $repoRoot "dist"

function Build-AddinPackage {
    param(
        [string]$ProjectName,
        [string]$BuildOutSubdir,
        [string]$MinClientVersion,
        [string]$PkgPrefix
    )

    $projDir = Join-Path $repoRoot "src\$ProjectName"
    $csproj = Join-Path $projDir "$ProjectName.csproj"

    [xml]$csprojXml = Get-Content $csproj
    $version = $csprojXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $version) { throw "Couldn't read <Version> from $csproj" }

    Write-Host "Building $ProjectName $version..."
    dotnet build $csproj
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }

    $buildOut = Join-Path $projDir $BuildOutSubdir
    $pkgName = "$PkgPrefix-$version"

    $stageRoot = Join-Path $distDir "stage-$ProjectName"
    $stagePkg = Join-Path $stageRoot $pkgName
    $addInDir = Join-Path $stagePkg "RobotStudio\Add-In"

    if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $addInDir | Out-Null

    Copy-Item (Join-Path $buildOut "RobotStudioUnityBridge.dll") $addInDir
    Copy-Item (Join-Path $buildOut "RobotStudioUnityBridge.rsaddin") $addInDir

    @"
<?xml version="1.0" encoding="utf-8"?>
<DistributionPackage>
  <Title>RobotStudio Unity Bridge</Title>
  <DisplayVersion>$version</DisplayVersion>
  <Creator>bsu-mt</Creator>
  <Description>Exports a robot arm assembly simulation for playback in Unity.</Description>
  <MinClientVersion>$MinClientVersion</MinClientVersion>
</DistributionPackage>
"@ | Set-Content -Path (Join-Path $stagePkg "manifest.xml") -Encoding utf8

    $rspakPath = Join-Path $distDir "$pkgName.rspak"
    $zipPath = Join-Path $distDir "$pkgName.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    if (Test-Path $rspakPath) { Remove-Item $rspakPath -Force }

    Compress-Archive -Path $stagePkg -DestinationPath $zipPath
    Rename-Item $zipPath $rspakPath
    Remove-Item $stageRoot -Recurse -Force

    Write-Host "Wrote $rspakPath"
}

Build-AddinPackage -ProjectName "RobotStudioUnityBridge" -BuildOutSubdir "bin\Debug\net10.0-windows" `
    -MinClientVersion "26.1" -PkgPrefix "RobotStudioUnityBridge"

Build-AddinPackage -ProjectName "RobotStudioUnityBridge2025" -BuildOutSubdir "bin\Debug\net48" `
    -MinClientVersion "25.1" -PkgPrefix "RobotStudioUnityBridge2025"

<#
.SYNOPSIS
    Builds RobotStudioUnityBridge and packages it as a .rspak that RobotStudio's
    Add-Ins > Install can consume.

.DESCRIPTION
    RobotStudio's Install dialog only accepts .rspak (or .rmf), not a bare .rsaddin -- see
    README.md "Testing the Add-in" for how that requirement was reverse-engineered (the zip
    must contain a top-level "<Name>-<Version>/" folder holding manifest.xml and
    "RobotStudio/Add-In/<assembly>.rsaddin"). This script reproduces that layout so nobody has
    to hand-build it again.

.OUTPUTS
    dist/RobotStudioUnityBridge-<version>.rspak
#>

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$projDir = Join-Path $repoRoot "src\RobotStudioUnityBridge"
$csproj = Join-Path $projDir "RobotStudioUnityBridge.csproj"

[xml]$csprojXml = Get-Content $csproj
$version = $csprojXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "Couldn't read <Version> from $csproj" }

Write-Host "Building RobotStudioUnityBridge $version..."
dotnet build $csproj
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$buildOut = Join-Path $projDir "bin\Debug\net10.0-windows"
$pkgName = "RobotStudioUnityBridge-$version"

$distDir = Join-Path $repoRoot "dist"
$stageRoot = Join-Path $distDir "stage"
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
  <MinClientVersion>26.1</MinClientVersion>
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

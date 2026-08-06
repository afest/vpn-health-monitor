<#
.SYNOPSIS
    Собирает self-contained win-x64 публикацию VPN Health Monitor и компилирует установщик (T-334).

.DESCRIPTION
    Два шага: `dotnet publish` self-contained + single-file в ..\publish\win-x64\, затем ISCC
    компилирует VpnHealthMonitor.iss из этой публикации. Готовый установщик — в installer\Output\.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\installer\build.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot 'publish\win-x64'
$csproj = Join-Path $repoRoot 'VpnHealthMonitor\VpnHealthMonitor.csproj'

Write-Host 'Публикация self-contained win-x64...' -ForegroundColor Cyan
# Повторный publish в существующую $publishDir иногда пропускает копирование Content-файлов
# (tools\remove-protection.*) из-за инкрементального кэша MSBuild — папку чистим перед каждой сборкой,
# чтобы установщик не собрался тихо без аварийного скрипта.
Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue

& dotnet publish $csproj -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw 'dotnet publish завершился с ошибкой.'
}

$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) {
    throw 'ISCC.exe (Inno Setup 6) не найден. Установи: winget install --id JRSoftware.InnoSetup'
}

Write-Host "Компиляция установщика ($iscc)..." -ForegroundColor Cyan
& $iscc (Join-Path $PSScriptRoot 'VpnHealthMonitor.iss')
if ($LASTEXITCODE -ne 0) {
    throw 'ISCC завершился с ошибкой.'
}

Write-Host "Готово: $PSScriptRoot\Output\" -ForegroundColor Green

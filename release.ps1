<#
.SYNOPSIS
    Выпуск версии одной командой: dotnet publish -> Velopack -> фид обновлений.

.DESCRIPTION
    Три шага: self-contained publish в publish\win-x64, упаковка через vpk и, по флагу -Github,
    публикация в GitHub Releases. Обновления приложение берёт оттуда же.

    Требуется:
        dotnet tool install -g vpk
    Версия пакета Velopack в csproj и версия CLI vpk обязаны совпадать: расхождение даёт
    невнятные ошибки при установке.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version 1.2.0
    powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version 1.2.0 -Github
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    # Куда складывать пакеты и откуда приложение будет их брать при локальной проверке
    [string]$OutputDir = "Releases",

    # Публиковать в GitHub Releases (нужен gh/токен и заведённый remote)
    [switch]$Github,

    [string]$RepoUrl = "https://github.com/afest/vpn-health-monitor",

    # Имя пакета. Velopack ставит приложение в %LocalAppData%\<packId>.
    [string]$PackId = "vpn-health-monitor"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# SDK на этой машине стоит per-user: глобальный C:\Program Files\dotnet — runtime-only,
# publish на нём падает с «No .NET SDKs were found».
$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

$vpk = Join-Path $env:USERPROFILE ".dotnet\tools\vpk.exe"
if (-not (Test-Path $vpk)) {
    throw "vpk не найден ($vpk). Поставьте: dotnet tool install -g vpk"
}

# --- Заметки релиза ---
# Это текст, который человек прочитает в плашке «Доступно обновление», решая, обновляться сейчас
# или потом. Источник — секция `## <версия>` в CHANGELOG.md.
#
# Проверка стоит ДО сборки и роняет выпуск, а не предупреждает: файл без правила записи умирает,
# и «допишу потом» означает пустые заметки у всех, кто увидит обновление.
$changelogPath = "CHANGELOG.md"
if (-not (Test-Path $changelogPath)) {
    throw "Нет $changelogPath — заметки релиза брать неоткуда."
}
$changelog = [System.IO.File]::ReadAllText((Resolve-Path $changelogPath).Path)
$section = [regex]::Match($changelog, "(?ms)^##\s+$([regex]::Escape($Version))\b.*?(?=^##\s|\z)")
if (-not $section.Success) {
    throw "В $changelogPath нет секции '## $Version'. Заметки пишутся ДО выпуска: допишите 3-5 строк о том, что изменилось для человека, и запустите снова."
}
$notes = (($section.Value -split "`r?`n" | Select-Object -Skip 1) -join "`n").Trim()
if (-not $notes) {
    throw "Секция '## $Version' в $changelogPath пустая."
}
$notesPath = Join-Path $env:TEMP "vpn-health-monitor-notes-$Version.md"
[System.IO.File]::WriteAllText($notesPath, $notes, (New-Object System.Text.UTF8Encoding($false)))

# --- Версия в проекте ---
# Расходится с -Version — обновление приедет с одним номером, а приложение покажет другой.
$csprojPath = "VpnHealthMonitor\VpnHealthMonitor.csproj"
$csproj = [System.IO.File]::ReadAllText((Resolve-Path $csprojPath).Path)
$declared = [regex]::Match($csproj, '<Version>([^<]+)</Version>').Groups[1].Value
if ($declared -ne $Version) {
    throw "В $csprojPath указана версия $declared, а выпускается $Version. Приведите их в соответствие."
}

# --- Сборка ---
$publishDir = "publish\win-x64"
Write-Output "== dotnet publish $Version =="
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
& $dotnet publish $csprojPath -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish упал (код $LASTEXITCODE)" }

if (-not (Test-Path "$publishDir\VpnHealthMonitor.exe")) {
    throw "Нет $publishDir\VpnHealthMonitor.exe — сборка не состоялась"
}

# --- Пакет Velopack ---
# Setup.exe ставит приложение в %LocalAppData%\<packId> и не спрашивает прав администратора.
# Правила брандмауэра при этом остаются за приложением: их ставит сам монитор через UAC,
# установщик их не трогает.
Write-Output "== vpk pack $Version =="
& $vpk pack `
    --packId $PackId `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe "VpnHealthMonitor.exe" `
    --packTitle "VPN Health Monitor" `
    --packAuthors "afest" `
    --icon "VpnHealthMonitor\Heart.ico" `
    --releaseNotes $notesPath `
    --outputDir $OutputDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack упал (код $LASTEXITCODE)" }

# --- Публикация ---
if ($Github) {
    Write-Output "== vpk upload github =="
    & $vpk upload github --repoUrl $RepoUrl --publish --releaseName "VPN Health Monitor $Version" --tag "v$Version" --outputDir $OutputDir
    if ($LASTEXITCODE -ne 0) { throw "vpk upload упал (код $LASTEXITCODE)" }
} else {
    Write-Output "== локальный фид: $((Resolve-Path $OutputDir).Path) =="
    Write-Output "   приложение возьмёт обновления отсюда, если задать переменную:"
    Write-Output "   `$env:VPNHEALTHMONITOR_UPDATE_FEED = '$((Resolve-Path $OutputDir).Path)'"
}

Get-ChildItem $OutputDir -File | Sort-Object Length -Descending |
    Select-Object Name, @{n = "МБ"; e = { [math]::Round($_.Length / 1MB, 1) } } |
    Format-Table -AutoSize | Out-String | Write-Output

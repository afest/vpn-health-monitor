<#
.SYNOPSIS
    Аварийное снятие всех правил kill switch «VPN Health Monitor» (T-324).

.DESCRIPTION
    Нужен ровно в той ситуации, когда интерфейс приложения недоступен: защищённая программа
    осталась без интернета, само приложение не открывается или уже удалено, а правила Windows
    Firewall продолжают блокировать прямой выход.

    Работает через netsh, а не через Get-NetFirewallRule. Причины две:
      * без прав администратора Get-NetFirewallRule падает с «Access is denied» и возвращает ноль
        правил — команда из README выглядит успешной и не делает ничего;
      * имена правил содержат «[hash]», а -DisplayName трактует скобки как wildcard-класс символов
        и литерально их не находит.
    Имя правила вычленяется из вывода netsh по самому префиксу, без опоры на подпись поля, —
    поэтому скрипт одинаково работает на русской и английской Windows.

    Удаляются ТОЛЬКО правила с префиксом «VPN Health Monitor - ». Остальной фаервол не трогается.

.PARAMETER Silent
    Без вопросов и без паузы в конце. Режим для деинсталлятора.

.PARAMETER DryRun
    Показать, что было бы удалено, и ничего не менять.

.PARAMETER RulePrefix
    Префикс имён правил. Менять не нужно; параметр существует для проверки скрипта на тестовом
    наборе правил, чтобы не сносить боевые.

.PARAMETER NoElevate
    Не перезапускать себя с правами администратора (используется внутри перезапуска).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\remove-protection.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\remove-protection.ps1 -Silent
#>
[CmdletBinding()]
param(
    [switch]$Silent,
    [switch]$DryRun,
    [string]$RulePrefix = 'VPN Health Monitor - ',
    [switch]$NoElevate
)

$ErrorActionPreference = 'Stop'

function Get-MonitorRuleNames {
    param([string]$Prefix)

    $names = New-Object System.Collections.Generic.List[string]
    $output = netsh advfirewall firewall show rule name=all dir=out 2>$null
    foreach ($line in $output) {
        $text = [string]$line
        $index = $text.IndexOf($Prefix)
        if ($index -ge 0) {
            $name = $text.Substring($index).Trim()
            if ($name -and -not $names.Contains($name)) {
                $names.Add($name) | Out-Null
            }
        }
    }

    return $names
}

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

Write-Host ''
Write-Host 'VPN Health Monitor — аварийное снятие защиты' -ForegroundColor Cyan
Write-Host '--------------------------------------------'

$rules = Get-MonitorRuleNames -Prefix $RulePrefix

if ($rules.Count -eq 0) {
    Write-Host 'Правил приложения в фаерволе нет — снимать нечего. Если интернет всё равно не работает, дело не в этом приложении.' -ForegroundColor Green
    if (-not $Silent) { Write-Host ''; Read-Host 'Нажми Enter, чтобы закрыть' | Out-Null }
    exit 0
}

Write-Host ("Найдено правил: {0}" -f $rules.Count)
foreach ($rule in $rules) { Write-Host ("  · {0}" -f $rule) }
Write-Host ''

if ($DryRun) {
    Write-Host 'Режим проверки: ничего не удалено.' -ForegroundColor Yellow
    exit 0
}

# Удаление правил требует прав администратора. Перезапускаем себя один раз через UAC,
# чтобы человеку не пришлось знать про «запуск от имени администратора».
if (-not (Test-IsAdmin)) {
    if ($NoElevate) {
        Write-Host 'Нужны права администратора, а повысить их нельзя. Запусти скрипт от имени администратора.' -ForegroundColor Red
        exit 2
    }

    Write-Host 'Нужны права администратора — сейчас появится запрос UAC.' -ForegroundColor Yellow
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath), '-NoElevate')
    if ($Silent) { $arguments += '-Silent' }
    if ($RulePrefix -ne 'VPN Health Monitor - ') { $arguments += @('-RulePrefix', ('"{0}"' -f $RulePrefix)) }

    try {
        $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -Wait -PassThru
        exit $process.ExitCode
    } catch {
        Write-Host 'Запрос прав отклонён — правила не сняты.' -ForegroundColor Red
        if (-not $Silent) { Write-Host ''; Read-Host 'Нажми Enter, чтобы закрыть' | Out-Null }
        exit 3
    }
}

$removed = 0
$failed = 0
foreach ($rule in $rules) {
    $result = netsh advfirewall firewall delete rule name="$rule" 2>&1
    if ($LASTEXITCODE -eq 0) {
        $removed++
        Write-Host ("  снято: {0}" -f $rule) -ForegroundColor Green
    } else {
        $failed++
        Write-Host ("  НЕ снято: {0} — {1}" -f $rule, ($result -join ' ')) -ForegroundColor Red
    }
}

# Повторная проверка: отчитываемся по факту, а не по кодам возврата.
$left = Get-MonitorRuleNames -Prefix $RulePrefix

Write-Host ''
Write-Host ("Снято правил: {0}, не удалось: {1}, осталось в фаерволе: {2}" -f $removed, $failed, $left.Count)

if ($left.Count -eq 0) {
    Write-Host 'Защита снята полностью. Прямой выход в интернет для всех программ открыт — если пользуешься VPN, включи его обратно.' -ForegroundColor Green
    $exitCode = 0
} else {
    Write-Host 'Часть правил осталась. Запусти скрипт ещё раз от имени администратора или удали их вручную:' -ForegroundColor Red
    foreach ($rule in $left) { Write-Host ("  netsh advfirewall firewall delete rule name=""{0}""" -f $rule) }
    $exitCode = 1
}

if (-not $Silent) {
    Write-Host ''
    Read-Host 'Нажми Enter, чтобы закрыть' | Out-Null
}

exit $exitCode

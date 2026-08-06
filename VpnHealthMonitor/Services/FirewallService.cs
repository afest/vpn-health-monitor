using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using VpnHealthMonitor.Models;

namespace VpnHealthMonitor.Services;

/// <summary>
/// Per-app kill switch via Windows Firewall.
///
/// Model (validated against T-035): Windows Firewall evaluates Block before Allow,
/// so "allow only the VPN adapter" is not expressible. Instead we create a persistent
/// Block-outbound rule for each protected .exe scoped to the PHYSICAL NICs
/// (everything not recognised as virtual or VPN — see <see cref="AdapterClassifier"/>),
/// leaving loopback and any VPN/TUN adapter at default-allow. Result, fail-closed by construction:
///   * VPN up (TUN mode): app egresses via TUN (not in block list) -> works.
///   * VPN up (system-proxy mode): app egresses via loopback proxy -> works.
///   * VPN down: only the physical NIC path remains, and it is blocked -> no leak.
/// The rule is static and always on once applied; the app is not in the packet path,
/// so there is no detect-then-block race window.
///
/// Limitations (documented in README): cannot restrict to one exact VPN adapter;
/// a brand-new unrecognised physical interface would not be covered until rules are
/// re-applied; in system-proxy mode only proxy-aware apps stay usable.
/// </summary>
public sealed class FirewallService
{
    public const string RulePrefix = "VPN Health Monitor - ";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string KillSwitchFolder => Path.Combine(AppPaths.AppDataFolder, "killswitch");

    public static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Predictable rule name: "VPN Health Monitor - Block Direct - file.exe [hash]".</summary>
    public static string BuildRuleName(string path)
    {
        var file = string.Empty;
        try { file = Path.GetFileName(path); } catch { /* ignore */ }
        if (string.IsNullOrWhiteSpace(file))
        {
            file = "app";
        }

        return $"{RulePrefix}Block Direct - {file} [{ShortHash(path)}]";
    }

    /// <param name="blockedAdapters">
    /// Interface aliases the rules bind to. Chosen by <see cref="AdapterClassifier"/> and confirmed by the
    /// user (T-323) — the helper no longer decides this on its own, so what the user saw is what gets applied.
    /// </param>
    public Task<FirewallActionResult> ApplyAsync(
        IReadOnlyList<ProtectedApp> apps,
        IReadOnlyList<string> blockedAdapters,
        CancellationToken cancellationToken)
        => RunJobAsync("apply", apps, blockedAdapters, cancellationToken);

    public Task<FirewallActionResult> RemoveAsync(IReadOnlyList<ProtectedApp> apps, CancellationToken cancellationToken)
        => RunJobAsync("remove", apps, Array.Empty<string>(), cancellationToken);

    /// <summary>Remove every "VPN Health Monitor - *" rule (incl. orphans), without touching other rules.</summary>
    public Task<FirewallActionResult> RemoveAllAsync(CancellationToken cancellationToken)
        => RunJobAsync("remove_all", Array.Empty<ProtectedApp>(), Array.Empty<string>(), cancellationToken);

    /// <summary>
    /// Read current "VPN Health Monitor - *" rules. Returns null if the rules could not be read
    /// (on some machines Get-NetFirewallRule denies access to non-elevated callers) — the caller
    /// then falls back to the locally recorded state.
    /// </summary>
    public async Task<IReadOnlyList<FirewallRuleInfo>?> QueryRulesAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(KillSwitchFolder);
        var queryScript = Path.Combine(KillSwitchFolder, "killswitch-query.ps1");
        await File.WriteAllTextAsync(queryScript, QueryScript, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(queryScript);

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            return ParseRules(stdout);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Live status from actually-read firewall rules.</summary>
    public static ProtectionStatus ComputeStatus(ProtectedApp app, IReadOnlyList<FirewallRuleInfo> rules)
    {
        var fileExists = SafeFileExists(app.Path);
        var rule = rules.FirstOrDefault(r => string.Equals(r.Rule, app.RuleName, StringComparison.OrdinalIgnoreCase));

        if (rule is null)
        {
            return fileExists ? ProtectionStatus.RulesNotApplied : ProtectionStatus.FileNotFound;
        }

        if (!fileExists)
        {
            return ProtectionStatus.FileNotFound;
        }

        var enabledAndBlocking = rule.Enabled
            && string.Equals(rule.Action, "Block", StringComparison.OrdinalIgnoreCase)
            && PathsMatch(rule.Program, app.Path);

        return enabledAndBlocking ? ProtectionStatus.Protected : ProtectionStatus.Error;
    }

    /// <summary>
    /// Fallback status when firewall rules cannot be read (access denied to non-elevated caller).
    /// Based on the locally recorded apply state, which is only set after a confirmed elevated apply.
    /// </summary>
    public static ProtectionStatus ComputeStatusFromRecord(ProtectedApp app)
    {
        if (!SafeFileExists(app.Path))
        {
            return ProtectionStatus.FileNotFound;
        }

        return app.RulesAppliedAt.HasValue ? ProtectionStatus.Protected : ProtectionStatus.RulesNotApplied;
    }

    private Task<FirewallActionResult> RunJobAsync(
        string action,
        IReadOnlyList<ProtectedApp> apps,
        IReadOnlyList<string> blockedAdapters,
        CancellationToken cancellationToken)
    {
        var job = new
        {
            action,
            adapters = blockedAdapters.ToArray(),
            apps = apps.Select(a => new JobApp(a.Name, a.Path, a.RuleName, null, false)).ToArray()
        };
        return ExecuteJobAsync(job, cancellationToken);
    }

    /// <summary>
    /// Move a protected app's rule to a new path in a single elevated step: sweep dead orphan rules
    /// for the same exe (accumulated across prior versions), drop the recorded old rule, then create
    /// the rule for the new path. <paramref name="appWithNewPath"/> already carries the new Path/RuleName.
    /// </summary>
    public Task<FirewallActionResult> UpdatePathAsync(
        ProtectedApp appWithNewPath,
        string oldRuleName,
        IReadOnlyList<string> blockedAdapters,
        CancellationToken cancellationToken)
    {
        var job = new
        {
            action = "update_path",
            adapters = blockedAdapters.ToArray(),
            apps = new[] { new JobApp(appWithNewPath.Name, appWithNewPath.Path, appWithNewPath.RuleName, oldRuleName, true) }
        };
        return ExecuteJobAsync(job, cancellationToken);
    }

    private async Task<FirewallActionResult> ExecuteJobAsync(object job, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(KillSwitchFolder);

        var helperScript = Path.Combine(KillSwitchFolder, "killswitch-helper.ps1");
        var jobFile = Path.Combine(KillSwitchFolder, "killswitch-job.json");
        var resultFile = jobFile + ".result.json";

        await File.WriteAllTextAsync(helperScript, HelperScript, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(jobFile, JsonSerializer.Serialize(job, JsonOptions), new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);

        SafeDelete(resultFile);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = true, // required for Verb=runas (UAC elevation)
            Verb = "runas",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{helperScript}\" -JobFile \"{jobFile}\""
        };

        int exitCode;
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return FirewallActionResult.Failed("Не удалось запустить процесс с повышением прав.");
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            try { exitCode = process.ExitCode; } catch { exitCode = -1; }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 == ERROR_CANCELLED: user dismissed the UAC prompt.
            return FirewallActionResult.UserCancelled();
        }
        catch (Exception ex)
        {
            return FirewallActionResult.Failed(ex.Message);
        }

        return ReadResult(resultFile, exitCode);
    }

    private sealed record JobApp(string Name, string Path, string RuleName, string? OldRuleName, bool SweepDeadOrphans);

    private static FirewallActionResult ReadResult(string resultFile, int exitCode)
    {
        try
        {
            if (!File.Exists(resultFile))
            {
                return new FirewallActionResult(exitCode == 0, false, exitCode,
                    "Скрипт не записал результат.", Array.Empty<FirewallItemResult>(), Array.Empty<string>());
            }

            var json = StripBom(File.ReadAllText(resultFile, Encoding.UTF8));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            string? error = root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String
                ? errEl.GetString()
                : null;

            var physical = new List<string>();
            if (root.TryGetProperty("physical", out var physEl) && physEl.ValueKind == JsonValueKind.Array)
            {
                physical.AddRange(physEl.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
            }

            var items = new List<FirewallItemResult>();
            if (root.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in itemsEl.EnumerateArray())
                {
                    items.Add(new FirewallItemResult(
                        GetStr(it, "ruleName"),
                        GetStr(it, "path"),
                        GetStr(it, "state")));
                }
            }

            return new FirewallActionResult(ok, false, exitCode, error, items, physical);
        }
        catch (Exception ex)
        {
            return new FirewallActionResult(false, false, exitCode, ex.Message,
                Array.Empty<FirewallItemResult>(), Array.Empty<string>());
        }
    }

    private static IReadOnlyList<FirewallRuleInfo>? ParseRules(string stdout)
    {
        var json = StripBom(stdout).Trim();
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                // { "queryError": "..." } => rules could not be read.
                return root.TryGetProperty("queryError", out _)
                    ? null
                    : new List<FirewallRuleInfo> { ReadRule(root) };
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                var list = new List<FirewallRuleInfo>();
                foreach (var el in root.EnumerateArray())
                {
                    list.Add(ReadRule(el));
                }
                return list;
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static FirewallRuleInfo ReadRule(JsonElement el)
    {
        var enabledText = GetStr(el, "enabled");
        // Get-NetFirewallRule Enabled enum stringifies to "True"/"1" depending on host.
        var enabled = enabledText.Equals("True", StringComparison.OrdinalIgnoreCase)
            || enabledText.Equals("1", StringComparison.Ordinal);
        return new FirewallRuleInfo(GetStr(el, "rule"), enabled, GetStr(el, "action"), GetStrOrNull(el, "program"));
    }

    internal static bool PathsMatch(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(a).TrimEnd('\\'),
            Path.GetFullPath(b).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }

    private static string StripBom(string text)
        => text.TrimStart((char)0xFEFF);

    private static string GetStr(JsonElement el, string name)
        => GetStrOrNull(el, name) ?? string.Empty;

    private static string? GetStrOrNull(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => null
        };
    }

    private const string HelperScript = @"param([Parameter(Mandatory=$true)][string]$JobFile)
$ErrorActionPreference = 'Stop'
$resultFile = $JobFile + '.result.json'
$logFile = $JobFile + '.log'
Start-Transcript -Path $logFile -Force | Out-Null
$result = [ordered]@{ ok = $true; action = ''; physical = @(); items = @(); error = $null }
try {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) { throw 'Not elevated' }
    $job = Get-Content -Raw -LiteralPath $JobFile -Encoding UTF8 | ConvertFrom-Json
    $result.action = [string]$job.action

    # Адаптеры выбирает и показывает пользователю приложение (AdapterClassifier, T-323).
    # Скрипт их только применяет: что человек подтвердил на экране, то и уходит в правило.
    $phys = @($job.adapters | Where-Object { $_ -and ([string]$_).Trim() -ne '' } | Select-Object -Unique)
    $result.physical = @($phys)

    if ($result.action -eq 'remove_all') {
        Get-NetFirewallRule -DisplayName 'VPN Health Monitor - *' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
        $result.items += ([ordered]@{ ruleName = 'ALL'; path = ''; state = 'removed' })
    }
    elseif ($result.action -eq 'update_path') {
      foreach ($app in $job.apps) {
        $item = [ordered]@{ ruleName = [string]$app.ruleName; path = [string]$app.path; state = '' }
        try {
            $targetFile = [System.IO.Path]::GetFileName([string]$app.path)
            if ($app.sweepDeadOrphans) {
                Get-NetFirewallRule -DisplayName 'VPN Health Monitor - *' -ErrorAction SilentlyContinue | ForEach-Object {
                    $pf = $_ | Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue
                    $prog = [string]$pf.Program
                    if ($prog -and ([System.IO.Path]::GetFileName($prog) -ieq $targetFile) -and (-not (Test-Path -LiteralPath $prog))) {
                        $_ | Remove-NetFirewallRule -ErrorAction SilentlyContinue
                    }
                }
            }
            if ($app.oldRuleName) {
                Get-NetFirewallRule -DisplayName 'VPN Health Monitor - *' -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -eq $app.oldRuleName } | Remove-NetFirewallRule -ErrorAction SilentlyContinue
            }
            Get-NetFirewallRule -DisplayName 'VPN Health Monitor - *' -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -eq $app.ruleName } | Remove-NetFirewallRule -ErrorAction SilentlyContinue
            if (-not (Test-Path -LiteralPath $app.path)) {
                $item.state = 'file_not_found'
            } elseif (@($phys).Count -eq 0) {
                throw 'Adapter list for blocking is empty'
            } else {
                New-NetFirewallRule -DisplayName $app.ruleName -Description 'VPN Health Monitor per-app kill switch. Blocks direct egress on physical NICs.' -Direction Outbound -Program $app.path -InterfaceAlias $phys -Action Block -Profile Any -Enabled True | Out-Null
                $item.state = 'applied'
            }
        } catch {
            $item.state = 'error'
            $result.ok = $false
        }
        $result.items += $item
      }
    }
    elseif ($job.apps) {
      foreach ($app in $job.apps) {
        $item = [ordered]@{ ruleName = [string]$app.ruleName; path = [string]$app.path; state = '' }
        try {
            Get-NetFirewallRule -DisplayName 'VPN Health Monitor - *' -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -eq $app.ruleName } | Remove-NetFirewallRule -ErrorAction SilentlyContinue
            if ($result.action -eq 'apply') {
                if (-not (Test-Path -LiteralPath $app.path)) {
                    $item.state = 'file_not_found'
                } elseif (@($phys).Count -eq 0) {
                    throw 'Adapter list for blocking is empty'
                } else {
                    New-NetFirewallRule -DisplayName $app.ruleName -Description 'VPN Health Monitor per-app kill switch. Blocks direct egress on physical NICs.' -Direction Outbound -Program $app.path -InterfaceAlias $phys -Action Block -Profile Any -Enabled True | Out-Null
                    $item.state = 'applied'
                }
            } else {
                $item.state = 'removed'
            }
        } catch {
            $item.state = 'error'
            $result.ok = $false
        }
        $result.items += $item
      }
    }
} catch {
    $result.ok = $false
    $result.error = $_.Exception.Message
} finally {
    try { Stop-Transcript | Out-Null } catch { }
}
$result | ConvertTo-Json -Depth 6 | Out-File -LiteralPath $resultFile -Encoding utf8
if ($result.ok) { exit 0 } else { exit 1 }
";

    // Кодировка вывода — не косметика: пути защищённых программ могут содержать кириллицу
    // (D:\Проги\...), и в кодовой странице консоли они приходят мусором. Тогда PathsMatch не совпадёт
    // и приложение покажет «Ошибка» для живого работающего правила.
    private const string QueryScript = @"$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
try {
    $prefix = 'VPN Health Monitor - '
    $rules = Get-NetFirewallRule | Where-Object { $_.DisplayName -like ($prefix + '*') }
    $out = foreach ($r in $rules) {
        $p = $r | Get-NetFirewallApplicationFilter
        [PSCustomObject]@{ rule = $r.DisplayName; enabled = [string]$r.Enabled; action = [string]$r.Action; program = [string]$p.Program }
    }
    if ($null -eq $out) { '[]' } else { ConvertTo-Json -InputObject @($out) -Compress }
} catch {
    ConvertTo-Json -InputObject ([ordered]@{ queryError = $_.Exception.Message }) -Compress
}
";
}

public sealed record FirewallActionResult(
    bool Success,
    bool Cancelled,
    int ExitCode,
    string? Error,
    IReadOnlyList<FirewallItemResult> Items,
    IReadOnlyList<string> PhysicalAdapters)
{
    public static FirewallActionResult UserCancelled()
        => new(false, true, -1, "Операция отменена в окне UAC.", Array.Empty<FirewallItemResult>(), Array.Empty<string>());

    public static FirewallActionResult Failed(string error)
        => new(false, false, -1, error, Array.Empty<FirewallItemResult>(), Array.Empty<string>());
}

public sealed record FirewallItemResult(string RuleName, string Path, string State);

public sealed record FirewallRuleInfo(string Rule, bool Enabled, string Action, string? Program);

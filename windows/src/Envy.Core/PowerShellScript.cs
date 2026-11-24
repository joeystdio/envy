using System.Text;

namespace Envy.Core;

/// <summary>
/// The PowerShell hook (init.ps1) and generated variables (vars.ps1), for PowerShell 7 and Windows PowerShell 5.1.
/// The hook wraps the prompt function (and runs when the location changes), so it behaves like the bash hook: when the
/// state stamp changed or the shell moved into or out of a project folder, it puts back what it set and applies the
/// current set. It shares the exported bookkeeping (__ENVY_VARS, __ENVY_O_NAME) with the bash and cmd scripts.
/// </summary>
public static class PowerShellScript
{
    public static string Hook(string shellDir, string? appPath) => $$"""
        # Envy shell hook for PowerShell 7 and Windows PowerShell 5.1. Written by Envy; local changes are overwritten.
        # Dot-sourced from your PowerShell profile.
        #
        #   envy status   what's loaded in this shell
        #   envy reload   reload right now
        #   envy help     everything else (the envy command in bin\, which scripts and agents can run too)

        $global:__envy_dir = if ($env:ENVY_DIR) { $env:ENVY_DIR } else { {{Quoted(shellDir)}} }

        # The envy command on PATH, so programs started from this shell (coding agents too) can run it.
        $global:__envy_bin = [IO.Path]::Combine($global:__envy_dir, 'bin')
        if ([IO.File]::Exists([IO.Path]::Combine($global:__envy_bin, 'envy.exe')) -and
            (";$env:PATH;").IndexOf(";$($global:__envy_bin);", [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            $env:PATH = "$($global:__envy_bin);$env:PATH"
        }

        $global:__envy_gen = $null
        $global:__envy_sig = $null
        $global:__envy_active = ''
        $global:__envy_scopes = @()

        # Sets a variable, remembering the value it had before Envy set it.
        function global:__envy_set([string]$Name, [string]$Value) {
            if (" $env:__ENVY_VARS" -notlike "* $Name *") {
                $old = [Environment]::GetEnvironmentVariable($Name)
                if ($null -ne $old) { [Environment]::SetEnvironmentVariable("__ENVY_O_$Name", $old) }
                [Environment]::SetEnvironmentVariable('__ENVY_VARS', "$env:__ENVY_VARS$Name ")
            }
            [Environment]::SetEnvironmentVariable($Name, $Value)
        }

        # Puts every variable Envy set back to its earlier value, or removes it. ($null would set an empty string.)
        function global:__envy_revert {
            foreach ($n in "$env:__ENVY_VARS".Split(' ', [StringSplitOptions]::RemoveEmptyEntries)) {
                if ($n -cnotmatch '^[A-Za-z_][A-Za-z0-9_]*$') { continue }
                $o = [Environment]::GetEnvironmentVariable("__ENVY_O_$n")
                if ($null -ne $o) {
                    [Environment]::SetEnvironmentVariable($n, $o)
                    [Environment]::SetEnvironmentVariable("__ENVY_O_$n", [NullString]::Value)
                } else {
                    [Environment]::SetEnvironmentVariable($n, [NullString]::Value)
                }
            }
            [Environment]::SetEnvironmentVariable('__ENVY_VARS', [NullString]::Value)
            $global:__envy_active = ''
        }

        function global:__envy_apply([int[]]$Matched) {
            for ($i = 0; $i -lt $global:__envy_scopes.Count; $i++) {
                $s = $global:__envy_scopes[$i]
                if ($s.f.Count -gt 0 -and $Matched -notcontains $i) { continue }
                $global:__envy_active = if ($global:__envy_active) { "$($global:__envy_active), $($s.n)" } else { $s.n }
                foreach ($v in $s.v) {
                    $value = if ($v.ContainsKey('x')) { [string](& $v.x) } else { [string]$v.v }
                    __envy_set $v.k $value
                }
            }
        }

        function global:__envy_hook {
            $g = ''
            try { $g = [IO.File]::ReadAllText([IO.Path]::Combine($global:__envy_dir, 'state')).Trim() } catch { }
            if ($g -ne $global:__envy_gen) {
                $global:__envy_gen = $g
                $global:__envy_scopes = @()
                $vars = [IO.Path]::Combine($global:__envy_dir, 'vars.ps1')
                if ([IO.File]::Exists($vars)) { . $vars }
            }
            $here = $ExecutionContext.SessionState.Path.CurrentFileSystemLocation.ProviderPath
            $here = $here.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
            $m = @()
            for ($i = 0; $i -lt $global:__envy_scopes.Count; $i++) {
                foreach ($f in $global:__envy_scopes[$i].f) {
                    if ($here.StartsWith($f, [StringComparison]::OrdinalIgnoreCase)) { $m += $i; break }
                }
            }
            $sig = "$g|$($m -join ' ')"
            if ($sig -ne $global:__envy_sig) {
                $global:__envy_sig = $sig
                __envy_revert
                __envy_apply $m
            }
        }

        # Status and reload are about this shell, so they run here; everything else goes to the envy command. Some tools
        # copy functions into shells where this hook never ran; there, everything goes to the envy command.
        function global:envy {
            $dir = if ($global:__envy_dir) { $global:__envy_dir } elseif ($env:ENVY_DIR) { $env:ENVY_DIR } else { {{Quoted(shellDir)}} }
            $exe = [IO.Path]::Combine($dir, 'bin', 'envy.exe')
            $hooked = $null -ne $global:__envy_gen -and (Get-Command __envy_hook -ErrorAction Ignore)
            $command = if ($args.Count -gt 0) { [string]$args[0] } else { 'status' }
            if ($hooked -and $command -eq 'status') {
                "Envy: $(if ($global:__envy_active) { $global:__envy_active } else { 'nothing loaded here' })"
                if ($env:__ENVY_VARS) { "Variables: $($env:__ENVY_VARS.TrimEnd())" }
            } elseif ($hooked -and $command -eq 'reload') {
                $global:__envy_gen = '(reload)'
                $global:__envy_sig = $null
                __envy_hook
                envy status
            } elseif ([IO.File]::Exists($exe)) {
                & $exe @args
            } elseif ($command -eq 'open') {
                {{(appPath is null ? "[Console]::Error.WriteLine('Envy is not installed here.')" : $"Start-Process -FilePath {Quoted(appPath)}")}}
            } else {
                [Console]::Error.WriteLine('envy: open Envy once to install the envy command')
            }
        }

        # Runs the hook after the prompt you already have, so the prompt still sees the last command's status. Prompt
        # tools that replace the prompt function later in your profile are wrapped again once the shell is idle.
        function global:__envy_wrap_prompt {
            $current = $function:prompt
            if ($current -and "$current" -match '__envy_hook') { return }
            $global:__envy_prompt = $current
            function global:prompt {
                $p = if ($global:__envy_prompt) { & $global:__envy_prompt } else { "PS $($ExecutionContext.SessionState.Path.CurrentLocation)$('>' * ($NestedPromptLevel + 1)) " }
                __envy_hook
                $p
            }
        }

        if (-not $global:__envy_loaded) {
            $global:__envy_loaded = $true
            __envy_wrap_prompt
            $null = Register-EngineEvent -SourceIdentifier PowerShell.OnIdle -Action {
                __envy_wrap_prompt
                Unregister-Event -SubscriptionId $EventSubscriber.SubscriptionId
            }
            # PowerShell 7 can load a folder's project as soon as you cd into it, not only at the next prompt.
            if ($ExecutionContext.InvokeCommand.PSObject.Properties['LocationChangedAction']) {
                $global:__envy_location_action = $ExecutionContext.InvokeCommand.LocationChangedAction
                $ExecutionContext.InvokeCommand.LocationChangedAction = {
                    param($s, $e)
                    __envy_hook
                    if ($global:__envy_location_action) { & $global:__envy_location_action $s $e }
                }
            }
        }
        __envy_hook

        """;

    /// <summary>
    /// The generated vars.ps1: data the hook reads. Order is precedence (later wins): Global, projects loaded in every
    /// shell, then folder projects (deepest folder last). Folder prefixes end in a separator and match without regard
    /// to case. Expanding values are script blocks built from the parsed value, never from raw text.
    /// </summary>
    public static string Vars(EnvConfig config, bool maskSecrets = false, string? home = null)
    {
        var (alwaysOn, scoped) = Scopes.Ordered(config, home);
        var s = new StringBuilder();
        s.Append("# Generated by Envy. Do not edit; changes here are overwritten.\n");
        s.Append("# Later entries win: Global, then projects loaded in every shell, then folder projects (deepest folder last).\n\n");
        s.Append("$global:__envy_scopes = @(\n");
        foreach (var p in alwaysOn.Concat(scoped))
        {
            var vars = Scopes.Loadable(p);
            if (vars.Count == 0) continue;
            var prefixes = p.Folders
                .SelectMany(f => PathStyle.Variants(f, home))
                .Select(PathStyle.AsPrefix)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            s.Append($"    @{{ n = {Quoted(p.Name)}; f = @({string.Join(", ", prefixes.Select(Quoted))}); v = @(\n");
            foreach (var v in vars)
            {
                var value = maskSecrets && v.IsSecret ? $"v = {Quoted("••••••")}" : Value(v);
                s.Append($"        @{{ k = {Quoted(v.Key)}; {value} }}\n");
            }
            s.Append("    ) }\n");
        }
        s.Append(")\n");
        return s.ToString();
    }

    static string Value(EnvVar v)
    {
        if (!v.Expands) return $"v = {Quoted(v.Value)}";
        var parts = Expansion.Parse(v.Value).Select(t => t switch
        {
            Expansion.Literal l => Quoted(l.Text),
            Expansion.Home => "$(if ($env:USERPROFILE) { $env:USERPROFILE } else { $HOME })",
            Expansion.Reference r => $"[Environment]::GetEnvironmentVariable({Quoted(r.Name)})",
            _ => "''",
        }).ToList();
        return parts.Count == 0 ? "v = ''" : $"x = {{ -join @({string.Join(", ", parts)}) }}";
    }

    /// <summary>A PowerShell single-quoted string. PowerShell also ends those at typographic quotes, so they are doubled too.</summary>
    public static string Quoted(string s)
    {
        var b = new StringBuilder("'");
        foreach (var c in s)
        {
            if (c is '\'' or '‘' or '’' or '‚' or '‛') b.Append(c);
            b.Append(c);
        }
        return b.Append('\'').ToString();
    }
}

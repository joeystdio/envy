using System.Text;

namespace Envy.Core;

/// <summary>What every generated script shares: which scopes load, in which order.</summary>
public static class Scopes
{
    /// <summary>Variables of <paramref name="project"/> that make it into the shell.</summary>
    public static List<EnvVar> Loadable(Project project) =>
        project.Variables.Where(v => v.IsEnabled && v.HasValidKey).ToList();

    /// <summary>
    /// Order is precedence (later wins): Global, projects loaded in every shell (sidebar order), then folder projects
    /// with the deepest folder last.
    /// </summary>
    public static (List<Project> AlwaysOn, List<Project> Scoped) Ordered(EnvConfig config, string? home = null)
    {
        var alwaysOn = new List<Project>();
        if (config.Global.IsEnabled) alwaysOn.Add(config.Global);
        alwaysOn.AddRange(config.Projects.Where(p => p.IsEnabled && !p.IsFolderScoped));
        var scoped = config.Projects
            .Select((p, index) => (p, index))
            .Where(x => x.p.IsEnabled && x.p.IsFolderScoped && Loadable(x.p).Count > 0)
            .OrderBy(x => PathStyle.Depth(x.p, home)).ThenBy(x => x.index)
            .Select(x => x.p)
            .ToList();
        return (alwaysOn, scoped);
    }

    public static string CommentSafe(string s) => s.Replace('\n', ' ').Replace('\r', ' ');

    public static bool IsPath(string key) => string.Equals(key, "PATH", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The bash hook (init.sh) and generated variables (vars.sh) for Git Bash. The hook runs at every prompt and only does
/// work when the state stamp changed or the shell moved into or out of a project folder. It then puts back everything it
/// set earlier and applies the current set. Bookkeeping is exported, so child shells (of any kind) can undo what their
/// parent applied: __ENVY_VARS lists the managed names, and __ENVY_O_NAME keeps each overridden original. PATH is kept
/// in Windows form there, and converted with cygpath when bash reads or writes it.
/// </summary>
public static class BashScript
{
    public static string Hook(string shellDirPosix, string? appPathPosix) => $$"""
        # Envy shell hook for Git Bash. Written by Envy; local changes are overwritten.
        # Sourced from ~/.bashrc.
        #
        #   envy status   what's loaded in this shell
        #   envy reload   reload right now
        #   envy help     everything else (the envy command in bin/, which scripts and agents can run too)

        __envy_dir="${ENVY_DIR-}"
        if [ -z "$__envy_dir" ]; then __envy_dir={{SingleQuoted(shellDirPosix)}}; fi

        # The envy command on PATH, so programs started from this shell (coding agents too) can run it.
        case ":${PATH-}:" in
          *":$__envy_dir/bin:"*) ;;
          *) if [ -f "$__envy_dir/bin/envy.exe" ]; then export PATH="$__envy_dir/bin${PATH:+:$PATH}"; fi ;;
        esac

        __envy_gen=""
        __envy_sig=""
        __envy_m=""
        __envy_active=""
        __envy_path_set="${__envy_path_set-}"
        __ENVY_VARS="${__ENVY_VARS-}"

        __envy_match() { __envy_m=""; }
        __envy_apply() { :; }

        # PATH is a Windows list (C:\a;C:\b) everywhere except inside bash, which uses /c/a:/c/b.
        __envy_winpath() { if command -v cygpath >/dev/null 2>&1; then cygpath -w -p "$1"; else printf '%s' "$1"; fi; }
        __envy_posixpath() { if command -v cygpath >/dev/null 2>&1; then cygpath -u -p "$1"; else printf '%s' "$1"; fi; }

        # The folders of PATH inside Git Bash itself (/mingw64/bin, /usr/bin, …), which only its own startup adds, that $1
        # lacks, each followed by a colon. Drives (/c/…) and %TEMP% (/tmp/…) are Git for Windows' only other mounts.
        __envy_own_dirs() {
          local __envy_l="$PATH" __envy_d __envy_out=""
          while [ -n "$__envy_l" ]; do
            case "$__envy_l" in
              *:*) __envy_d="${__envy_l%%:*}"; __envy_l="${__envy_l#*:}" ;;
              *) __envy_d="$__envy_l"; __envy_l="" ;;
            esac
            case "$__envy_d" in ''|[!/]*|/[A-Za-z]|/[A-Za-z]/*|/tmp|/tmp/*) continue ;; esac
            case ":$1:$__envy_out" in *":$__envy_d:"*) ;; *) __envy_out="$__envy_out$__envy_d:" ;; esac
          done
          printf '%s' "$__envy_out"
        }

        # __envy_set NAME VALUE: export NAME, remembering the value it had before Envy set it.
        __envy_set() {
          case " $__ENVY_VARS" in
            *" $1 "*) ;;
            *)
              if eval "[ -n \"\${$1+x}\" ]"; then
                if [ "$1" = PATH ]; then
                  export __ENVY_O_PATH="$(__envy_winpath "$PATH")"
                else
                  eval "export __ENVY_O_$1=\"\${$1}\""
                fi
              fi
              __ENVY_VARS="$__ENVY_VARS$1 "
              ;;
          esac
          if [ "$1" = PATH ]; then __envy_path_set=1; fi
          export "$1=$2"
        }

        # Put every variable Envy set back to its earlier value, or unset it.
        __envy_revert() {
          local __envy_l="$__ENVY_VARS" __envy_n __envy_o
          while [ -n "$__envy_l" ]; do
            case "$__envy_l" in
              *" "*) __envy_n="${__envy_l%% *}"; __envy_l="${__envy_l#* }" ;;
              *) __envy_n="$__envy_l"; __envy_l="" ;;
            esac
            case "$__envy_n" in ''|[0-9]*|*[!A-Za-z0-9_]*) continue ;; esac
            if eval "[ -n \"\${__ENVY_O_$__envy_n+x}\" ]"; then
              if [ "$__envy_n" = PATH ]; then
                __envy_o="$(__envy_posixpath "$__ENVY_O_PATH")"
                # Set by the shell this one was started from, whose PATH lacks what Git Bash added in front of it at
                # startup (/mingw64/bin, /usr/bin): keep those, or ls, cygpath and the rest are gone.
                if [ -z "$__envy_path_set" ]; then __envy_o="$(__envy_own_dirs "$__envy_o")$__envy_o"; fi
                export PATH="$__envy_o"
              else
                eval "export $__envy_n=\"\${__ENVY_O_$__envy_n}\""
              fi
              unset "__ENVY_O_$__envy_n"
            else
              unset "$__envy_n"
            fi
          done
          __ENVY_VARS=""
          __envy_active=""
          __envy_path_set=""
        }

        __envy_hook() {
          local __envy_rc=$? __envy_g=""
          if [ -r "$__envy_dir/state" ]; then
            IFS= read -r __envy_g < "$__envy_dir/state" || :
          fi
          if [ "$__envy_g" != "$__envy_gen" ]; then
            __envy_gen="$__envy_g"
            __envy_match() { __envy_m=""; }
            __envy_apply() { :; }
            if [ -r "$__envy_dir/vars.sh" ]; then . "$__envy_dir/vars.sh"; fi
          fi
          __envy_match
          if [ "$__envy_gen|$__envy_m" != "$__envy_sig" ]; then
            __envy_sig="$__envy_gen|$__envy_m"
            __envy_revert
            __envy_apply
            if [ -n "$__ENVY_VARS" ]; then export __ENVY_VARS; else unset __ENVY_VARS; fi
          fi
          return $__envy_rc
        }

        # Status and reload are about this shell, so they run here; everything else goes to the envy command. Some tools
        # copy shell functions into their own shells without these variables or the hook having run (Claude Code's shell
        # snapshots, for one); there, everything goes to the envy command. Git Bash rewrites arguments that look like
        # paths (NAME=/v1 becomes NAME=C:/Program Files/Git/v1), so that's off except for exec, whose command may need it.
        function envy {
          local __envy_d="${__envy_dir-}" __envy_hooked=""
          [ -n "$__envy_d" ] || __envy_d="${ENVY_DIR-}"
          [ -n "$__envy_d" ] || __envy_d={{SingleQuoted(shellDirPosix)}}
          if [ -n "${__envy_gen-}" ] && type __envy_hook >/dev/null 2>&1; then __envy_hooked=1; fi
          case "${1:-status}" in
            status)
              if [ -z "$__envy_hooked" ] && [ -f "$__envy_d/bin/envy.exe" ]; then "$__envy_d/bin/envy.exe" status; return; fi
              printf 'Envy: %s\n' "${__envy_active:-nothing loaded here}"
              if [ -n "${__ENVY_VARS-}" ]; then printf 'Variables: %s\n' "${__ENVY_VARS% }"; fi
              ;;
            reload)
              if [ -z "$__envy_hooked" ] && [ -f "$__envy_d/bin/envy.exe" ]; then "$__envy_d/bin/envy.exe" reload; return; fi
              __envy_gen="(reload)"
              __envy_sig=""
              __envy_hook
              envy status
              ;;
            exec)
              if [ -f "$__envy_d/bin/envy.exe" ]; then "$__envy_d/bin/envy.exe" "$@"; else printf 'envy: open Envy once to install the envy command\n' >&2; return 1; fi
              ;;
            *)
              if [ -f "$__envy_d/bin/envy.exe" ]; then
                MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' "$__envy_d/bin/envy.exe" "$@"
              elif [ "$1" = open ]; then
                {{(appPathPosix is null ? "printf 'Envy is not installed here.\\n' >&2; return 1" : $"( {SingleQuoted(appPathPosix)} >/dev/null 2>&1 & )")}}
              else
                printf 'envy: open Envy once to install the envy command\n' >&2
                return 1
              fi
              ;;
          esac
        }

        case ";${PROMPT_COMMAND-};" in
          *";__envy_hook;"*) ;;
          *) PROMPT_COMMAND="__envy_hook${PROMPT_COMMAND:+;$PROMPT_COMMAND}" ;;
        esac
        __envy_hook

        """;

    /// <summary>
    /// The generated vars.sh. Folder patterns use the POSIX form of each folder ($PWD in Git Bash is /c/Users/…) and
    /// match without regard to case, as Windows does.
    /// </summary>
    public static string Vars(EnvConfig config, bool maskSecrets = false, string? home = null)
    {
        var (alwaysOn, scoped) = Scopes.Ordered(config, home);
        var s = new StringBuilder();
        s.Append("""
            # Generated by Envy. Do not edit; changes here are overwritten.
            # Later lines win: Global, then projects loaded in every shell, then folder projects (deepest folder last).

            __envy_match() {
              __envy_m=""

            """);
        if (scoped.Count > 0)
        {
            s.Append("  local __envy_nc=1\n  shopt -q nocasematch || __envy_nc=0\n  shopt -s nocasematch\n");
            for (var n = 0; n < scoped.Count; n++)
                s.Append($"  case \"$PWD/\" in {string.Join("|", FolderPatterns(scoped[n].Folders, home))}) __envy_m=\"$__envy_m {n + 1}\" ;; esac\n");
            s.Append("  [ $__envy_nc = 1 ] || shopt -u nocasematch\n");
        }
        s.Append("}\n\n__envy_apply() {\n");
        foreach (var p in alwaysOn) s.Append(Block(p, "  ", maskSecrets, withComment: true));
        for (var n = 0; n < scoped.Count; n++)
        {
            var p = scoped[n];
            s.Append($"  # {Scopes.CommentSafe(p.Name)}, inside {string.Join(", ", p.Folders.Select(Scopes.CommentSafe))}\n");
            s.Append($"  case \" $__envy_m \" in *\" {n + 1} \"*)\n");
            s.Append(Block(p, "    ", maskSecrets, withComment: false));
            s.Append("  ;; esac\n");
        }
        s.Append("  :\n}\n");
        return s.ToString();
    }

    static string Block(Project p, string indent, bool maskSecrets, bool withComment)
    {
        var vars = Scopes.Loadable(p);
        if (vars.Count == 0) return "";
        var s = new StringBuilder();
        if (withComment) s.Append($"{indent}# {Scopes.CommentSafe(p.Name)}\n");
        s.Append($"{indent}__envy_active=\"${{__envy_active:+$__envy_active, }}\"{SingleQuoted(p.Name)}\n");
        foreach (var v in vars)
        {
            var key = Scopes.IsPath(v.Key) ? "PATH" : v.Key;
            var value = maskSecrets && v.IsSecret ? SingleQuoted("••••••") : Value(v);
            if (key == "PATH") value = $"\"$(__envy_posixpath {value})\"";
            s.Append($"{indent}__envy_set {key} {value}\n");
        }
        return s.ToString();
    }

    /// <summary>The value as a bash word: single-quoted when literal, a concatenation of quoted parts when expanding.</summary>
    public static string Value(EnvVar v)
    {
        if (!v.Expands) return SingleQuoted(v.Value);
        var parts = Expansion.Parse(v.Value).Select(t => t switch
        {
            Expansion.Literal l => SingleQuoted(l.Text),
            Expansion.Home => "\"${USERPROFILE:-$HOME}\"",
            Expansion.Reference r when Scopes.IsPath(r.Name) => "\"$(__envy_winpath \"$PATH\")\"",
            Expansion.Reference r when Expansion.IsIdentifier(r.Name) => $"\"${{{r.Name}-}}\"",
            Expansion.Reference r => $"\"$(printenv {SingleQuoted(r.Name)})\"",
            _ => "",
        }).ToList();
        return parts.Count == 0 ? "''" : string.Concat(parts);
    }

    public static string SingleQuoted(string s) => "'" + SingleQuotedInner(s) + "'";

    static string SingleQuotedInner(string s) => s.Replace("'", "'\\''");

    /// <summary>case patterns matching each folder and everything below it, as given and with links resolved.</summary>
    internal static List<string> FolderPatterns(IEnumerable<string> folders, string? home)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = new List<string>();
        foreach (var folder in folders)
            foreach (var variant in PathStyle.Variants(folder, home))
            {
                var prefix = PathStyle.AsPrefix(PathStyle.ToPosix(variant));
                if (seen.Add(prefix)) output.Add(SingleQuoted(prefix) + "*");
            }
        return output;
    }
}

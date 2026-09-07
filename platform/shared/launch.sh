#!/bin/sh
set -eu
shared_dir=$(CDPATH= cd -P "$(dirname "$0")" && pwd)
studio_root=$(CDPATH= cd -P "$shared_dir/../.." && pwd)
action=${1:-run}
if [ "$#" -gt 0 ]; then shift; fi

find_dotnet() {
    if [ -n "${METROIDVANIA_STUDIO_DOTNET:-}" ]; then
        if [ -x "$METROIDVANIA_STUDIO_DOTNET" ]; then printf '%s\n' "$METROIDVANIA_STUDIO_DOTNET"; return; fi
        command -v "$METROIDVANIA_STUDIO_DOTNET" && return
        printf '%s\n' 'METROIDVANIA_STUDIO_DOTNET does not resolve to an executable.' >&2; return 1
    fi
    if command -v dotnet >/dev/null 2>&1; then command -v dotnet; return; fi
    for candidate in "${DOTNET_ROOT:-}/dotnet" "${HOME:-}/.dotnet/dotnet" /usr/local/share/dotnet/dotnet /usr/share/dotnet/dotnet /opt/homebrew/bin/dotnet /opt/homebrew/opt/dotnet/libexec/dotnet /usr/local/opt/dotnet/libexec/dotnet; do
        if [ -x "$candidate" ]; then printf '%s\n' "$candidate"; return; fi
    done
    printf '%s\n' 'ASP.NET Core Runtime 10 is required to run; .NET SDK 10 is required to build. Install it or set METROIDVANIA_STUDIO_DOTNET.' >&2
    return 1
}
find_node() {
    if [ -n "${METROIDVANIA_STUDIO_NODE:-}" ]; then
        if [ -x "$METROIDVANIA_STUDIO_NODE" ]; then printf '%s\n' "$METROIDVANIA_STUDIO_NODE"; return; fi
        command -v "$METROIDVANIA_STUDIO_NODE" && return
        printf '%s\n' 'METROIDVANIA_STUDIO_NODE does not resolve to an executable.' >&2; return 1
    fi
    if command -v node >/dev/null 2>&1; then command -v node; return; fi
    for candidate in /opt/homebrew/bin/node /usr/local/bin/node /usr/bin/node; do
        if [ -x "$candidate" ]; then printf '%s\n' "$candidate"; return; fi
    done
    printf '%s\n' 'Node.js 24 or later is required for building. Install it or set METROIDVANIA_STUDIO_NODE.' >&2
    return 1
}
dotnet=$(find_dotnet)
if [ "$action" = build ]; then
    node=$(find_node)
    exec "$node" "$shared_dir/build.mjs" --studio-root "$studio_root" --dotnet "$dotnet" "$@"
fi
case "$action" in run|stop|check) ;; *) printf '%s\n' 'Expected build, run, stop or check.' >&2; exit 1 ;; esac
# Preserve argument boundaries while translating the optional read-only flag.
explicit_build=false
option_value=false
remaining=$#
while [ "$remaining" -gt 0 ]; do
    argument=$1
    shift
    remaining=$((remaining - 1))
    if [ "$option_value" = true ]; then
        option_value=false
    else
        case "$argument" in
            --check) if [ "$action" = run ]; then action=check; continue; fi ;;
            --build-directory) explicit_build=true; option_value=true ;;
            --project|--port|--studio-root) option_value=true ;;
        esac
    fi
    set -- "$@" "$argument"
done
launcher="$studio_root/metroidvania-studio/launcher/MetroidvaniaStudio.Launcher.dll"
published=false
if [ -f "$launcher" ]; then published=true; else
    launcher="$studio_root/metroidvania-studio/launcher/bin/Release/net10.0/MetroidvaniaStudio.Launcher.dll"
fi
if [ "$action" = check ] && [ ! -f "$launcher" ]; then
    node=$(find_node)
    exec "$node" "$shared_dir/build.mjs" --studio-root "$studio_root" --dotnet "$dotnet" --check
fi
if [ "$action" = run ] && [ "$published" = false ]; then
    if [ "$explicit_build" = false ] && [ -f "$launcher" ]; then
        node=$(find_node)
        "$node" "$shared_dir/build.mjs" --studio-root "$studio_root" --dotnet "$dotnet" --ensure
    elif [ ! -f "$launcher" ]; then
        node=$(find_node)
        "$node" "$shared_dir/build.mjs" --studio-root "$studio_root" --dotnet "$dotnet"
    fi
fi
if [ ! -f "$launcher" ]; then printf '%s\n' 'The launcher build was not produced.' >&2; exit 1; fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
exec "$dotnet" "$launcher" "$action" --studio-root "$studio_root" "$@"

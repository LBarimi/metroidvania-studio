#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
target='@STUDIO_TARGET@'
case "$(uname -s)" in
  Darwin) os=osx; workspace="${HOME}/Library/Application Support/MetroidvaniaStudio/workspace" ;;
  Linux) os=linux; workspace="${XDG_DATA_HOME:-${HOME}/.local/share}/metroidvania-studio/workspace" ;;
  *) echo 'Use the Windows launch file on Windows.' >&2; exit 1 ;;
esac
case "$target:$os" in web:*|mac:osx|linux:linux) ;; *) echo 'Download the package for this operating system.' >&2; exit 1 ;; esac
case "$(uname -m)" in arm64|aarch64) arch=arm64 ;; x86_64|amd64) arch=x64 ;; *) echo 'This package supports ARM64 and x64.' >&2; exit 1 ;; esac
runtime="$root/app/runtime/$os-$arch"
if [ "$target" != web ]; then
  if [ ! -f "$runtime/dotnet" ]; then echo 'Extract the complete archive before starting.' >&2; exit 1; fi
  chmod u+x "$runtime/dotnet"
  export DOTNET_ROOT="$runtime"
  dotnet="$runtime/dotnet"
else
  dotnet=${METROIDVANIA_STUDIO_DOTNET:-}
  if [ -z "$dotnet" ]; then dotnet=$(command -v dotnet || true); fi
  if [ -z "$dotnet" ]; then
    for candidate in "${DOTNET_ROOT:-}/dotnet" /usr/local/share/dotnet/dotnet /usr/share/dotnet/dotnet "${HOME}/.dotnet/dotnet"; do
      if [ -x "$candidate" ]; then dotnet=$candidate; break; fi
    done
  fi
  if [ -z "$dotnet" ]; then echo 'Install ASP.NET Core Runtime 10, or use the package for your operating system.' >&2; exit 1; fi
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
action=run
case "${1:-}" in --stop) action=stop; shift ;; --check) action=check; shift ;; esac
exec "$dotnet" "$root/app/metroidvania-studio/launcher/MetroidvaniaStudio.Launcher.dll" "$action" --studio-root "$root/app" --project "$workspace" "$@"

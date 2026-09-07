#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
case "$(uname -s)" in Darwin) os=osx ;; Linux) os=linux ;; *) echo 'Use metroidvania-studio-cli.cmd on Windows.' >&2; exit 1 ;; esac
case "$(uname -m)" in arm64|aarch64) arch=arm64 ;; x86_64|amd64) arch=x64 ;; *) echo 'Supported architectures: ARM64 and x64.' >&2; exit 1 ;; esac
runtime="$root/app/runtime/$os-$arch"
dotnet=${METROIDVANIA_STUDIO_DOTNET:-}
if [ -z "$dotnet" ] && [ -f "$runtime/dotnet" ]; then
  chmod u+x "$runtime/dotnet"
  dotnet="$runtime/dotnet"
  export DOTNET_ROOT="$runtime"
fi
if [ -z "$dotnet" ]; then dotnet=$(command -v dotnet || true); fi
if [ -z "$dotnet" ]; then
  for candidate in "${DOTNET_ROOT:-}/dotnet" /usr/local/share/dotnet/dotnet /usr/share/dotnet/dotnet "${HOME}/.dotnet/dotnet"; do
    if [ -x "$candidate" ]; then dotnet=$candidate; break; fi
  done
fi
if [ -z "$dotnet" ]; then echo 'Install .NET Runtime 10 or use the download with a bundled runtime.' >&2; exit 1; fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
exec "$dotnet" "$root/app/metroidvania-studio/cli/MetroidvaniaStudio.Cli.dll" "$@"

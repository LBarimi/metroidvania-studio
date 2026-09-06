#!/bin/sh
set -eu
script_dir=$(CDPATH= cd -P "$(dirname "$0")" && pwd)
exec /bin/sh "$script_dir/../shared/launch.sh" run "$@"

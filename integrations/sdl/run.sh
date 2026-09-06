#!/bin/sh
set -eu
cd "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
if [ "$#" -eq 0 ]; then
  set -- samples/maps/Sample.map.json samples/catalog.json samples
fi
exec ./builds/metroidvania-studio-preview "$@"

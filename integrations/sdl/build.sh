#!/bin/sh
set -eu
cd "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
cmake -S . -B builds -DCMAKE_BUILD_TYPE=Release
cmake --build builds --config Release --parallel

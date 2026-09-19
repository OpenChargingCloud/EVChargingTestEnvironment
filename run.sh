#!/bin/bash
#
# Start the whole site with whatever was passed here, e.g.
#
#   ./run.sh --shared
#   ./run.sh --base-port 3347 --no-lc
#
# --help lists the switches.

set -e

cd "$(dirname "$0")"

dotnet run --project EVChargingTestEnvironment -- "$@"

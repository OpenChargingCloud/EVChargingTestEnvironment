#!/bin/bash
#
# Pull everything and build it.
#
# Two things are not fetched here, and both are things you do rather than
# things we ship:
#
#   The ISO 15118 schemas are ISO's, under a licence that grants use and not
#   redistribution:
#
#     bash libs/WWCP_ISO15118/tools/download-schemas.sh
#
#   The generated CSS a handful of the OCPP projects embed is made by an
#   editor extension that no command line runs:
#
#     bash tools/build-webassets.sh

set -e

cd "$(dirname "$0")"

git submodule foreach git pull
git pull
dotnet build EVChargingTestEnvironment.slnx

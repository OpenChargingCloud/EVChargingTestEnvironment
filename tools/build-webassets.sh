#!/bin/bash
#
# The generated CSS a handful of the OCPP and WWCP projects embed.
#
# Those projects keep their stylesheets as SCSS and embed the compiled CSS as a
# manifest resource. The compilation is described in a "compilerconfig.json"
# beside each project - the format Visual Studio's Web Compiler extension reads
# - and nothing on a command line reads it, so on a machine that has never had
# that project open in Visual Studio the CSS is simply absent and the build
# stops on a missing resource:
#
#   CSC : error CS1566: Fehler beim Lesen der Ressource "...events.css"
#
# The files are in .gitignore, which is right: they are generated. This script
# generates them, which is what was missing.
#
# Needs 'sass' (npm i -g sass). Idempotent: run it again and it overwrites what
# it wrote before.
#
# Usage:  bash tools/build-webassets.sh [<repository>]

set -e

# The repository to generate them in: the one this script lives in, or another
# checkout named on the command line. The sibling CLIs keep their own copies of
# the same submodules and hit the same missing resource, and a script that can
# only ever fix its own checkout would have to be copied into each of them.
root="${1:-$(dirname "$0")/..}"

if [ ! -d "$root/libs" ]
then
    echo "'$root' has no libs/ directory - is it a checkout of one of these repositories?" >&2
    exit 1
fi

cd "$root"

if ! command -v sass > /dev/null 2>&1
then
    echo "This needs 'sass'. Install it with:  npm install -g sass" >&2
    exit 1
fi

written=0
skipped=0

# Every project that describes a stylesheet compilation, in the submodules that
# have any. The -not -path keeps build output out of it.
while IFS= read -r config
do

    project=$(dirname "$config")

    # inputFile / outputFile pairs, one per line, read without jq: these files
    # are written by an editor extension and are plain, one key per line.
    paste -d'|' \
          <(grep -o '"inputFile"[^,}]*'  "$config" | sed 's/.*: *"//; s/"$//') \
          <(grep -o '"outputFile"[^,}]*' "$config" | sed 's/.*: *"//; s/"$//') |
    while IFS='|' read -r input output
    do

        [ -z "$input" ] && continue

        if [ ! -f "$project/$input" ]
        then
            echo "  skipped  $project/$input (no such file)"
            continue
        fi

        mkdir -p "$(dirname "$project/$output")"

        sass --no-source-map --style=expanded "$project/$input" "$project/$output"

        # The '.min.css' beside it, which those projects embed as well.
        minified="${output%.css}.min.css"
        sass --no-source-map --style=compressed "$project/$input" "$project/$minified"

        echo "  $project/$output"

    done

done < <(find libs -name compilerconfig.json -not -path '*/obj/*' -not -path '*/bin/*' | sort)

echo
echo "Done. These files are in .gitignore - they are generated, and this is what generates them."

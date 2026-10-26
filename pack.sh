#!/bin/sh
# Builds the plugin and zips it as a Playnite .pext.
set -e
cd "$(dirname "$0")"
dotnet build src -c Release
version=$(sed -n 's/^Version: //p' src/extension.yaml)
mkdir -p dist
out="$PWD/dist/FireshareShare-$version.pext"
rm -f "$out"
(cd src/bin/Release/net462 && zip -q "$out" ./*.dll extension.yaml icon.png)
echo "$out"

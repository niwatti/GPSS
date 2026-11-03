#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")/.."
dotnet restore Gpss.sln --locked-mode --disable-parallel --disable-build-servers -m:1
dotnet build Gpss.sln --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
# WPF execution is Windows-only; these five assemblies exercise pure/mocked paths.
for part in Core HidDirect XInput Application Cli; do
  dotnet test "tests/Gpss.$part.Tests/Gpss.$part.Tests.csproj" --no-build --no-restore --disable-build-servers -m:1
done

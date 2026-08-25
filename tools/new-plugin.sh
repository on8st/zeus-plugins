#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Scaffold a new plugin directory from the layout the first one proved.
#
# Usage: ./tools/new-plugin.sh <slug> "<Display Name>"
#   e.g. ./tools/new-plugin.sh ubersdr "UberSDR"
#
# The details it bakes in are the ones that cost a session to discover:
#   - the manifest is plugin.json, not manifest.json
#   - the id charset is ^[a-z][a-z0-9.]*[a-z0-9]$ — no hyphens
#   - the contracts are referenced Private=false, never shipped
#   - CopyLocalLockFileAssemblies, or NuGet deps are missing at load time
#   - abi 1 / sdk 1.4.0, or the host refuses to load it
set -euo pipefail

SLUG="${1:?usage: new-plugin.sh <slug> \"<Display Name>\"}"
NAME="${2:?usage: new-plugin.sh <slug> \"<Display Name>\"}"

[[ "$SLUG" =~ ^[a-z][a-z0-9]*$ ]] || {
  echo "slug must be lowercase letters and digits: the plugin id is built from it" >&2; exit 2; }

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DIR="$ROOT/$SLUG"
[ -e "$DIR" ] && { echo "$DIR already exists" >&2; exit 2; }

PASCAL="$(python3 -c 'import sys;print(sys.argv[1].capitalize())' "$SLUG")"
ASM="Zeus.Plugin.$PASCAL"
ID="be.on8st.zeus.plugins.$SLUG"

mkdir -p "$DIR"/{docs/design/source,prompts,src/"$ASM"/ui,tests/"$ASM".Tests,tools}

cat > "$DIR/Directory.Build.props" <<PROPS
<Project>

  <!--
    Where the Zeus station-engine source sits, for the Zeus.Plugins.Contracts
    project reference. Defaults to a sibling checkout; override with
    -p:ZeusEngineRoot=/path/to/station-engine
  -->
  <PropertyGroup>
    <ZeusEngineRoot Condition="'\$(ZeusEngineRoot)' == ''">\$(MSBuildThisFileDirectory)../../station-engine</ZeusEngineRoot>
    <ZeusContractsProject>\$(ZeusEngineRoot)/Zeus.Plugins.Contracts/Zeus.Plugins.Contracts.csproj</ZeusContractsProject>
  </PropertyGroup>

</Project>
PROPS

cat > "$DIR/src/$ASM/$ASM.csproj" <<CSPROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <LangVersion>latest</LangVersion>
    <!-- A plugin is loaded through AssemblyDependencyResolver, which reads the
         .deps.json and looks for assemblies beside it. A class library does not
         copy its NuGet dependencies to the output by default, so without this
         they are simply absent at load time. -->
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <!-- The host resolves the contracts from the default load context, so the
         plugin must NOT ship its own copy: Private=false. -->
    <ProjectReference Include="\$(ZeusContractsProject)" Private="false" ExcludeAssets="runtime" />
  </ItemGroup>
  <ItemGroup>
    <!-- plugin.json, not manifest.json. -->
    <None Include="plugin.json" CopyToOutputDirectory="PreserveNewest" />
    <None Include="ui/**/*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
CSPROJ

cat > "$DIR/src/$ASM/plugin.json" <<MANIFEST
{
  "schemaVersion": 1,
  "id": "$ID",
  "name": "$NAME",
  "version": "0.1.0",
  "author": "on8st",
  "description": "TODO — what it does, and what it is not.",
  "homepage": "https://github.com/on8st/zeus-plugins",
  "license": "GPL-2.0-or-later",
  "sdk": { "abi": 1, "minVersion": "1.4.0" },
  "entrypoint": {
    "assembly": "$ASM.dll",
    "type": "$ASM.${PASCAL}Plugin"
  },
  "capabilities": [],
  "permissions": {}
}
MANIFEST

cat > "$DIR/tests/$ASM.Tests/$ASM.Tests.csproj" <<TESTS
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/$ASM/$ASM.csproj" />
    <ProjectReference Include="\$(ZeusContractsProject)" />
  </ItemGroup>
</Project>
TESTS

cat > "$DIR/$ASM.slnx" <<SLN
<Solution>
  <Project Path="src/$ASM/$ASM.csproj" />
  <Project Path="tests/$ASM.Tests/$ASM.Tests.csproj" />
</Solution>
SLN

sed -e "s/be\.on8st\.zeus\.plugins\.wavelog/$ID/g" \
    -e "s/Zeus\.Plugin\.Wavelog/$ASM/g" \
    -e "s/Wavelog Synchroniser/$NAME/g" \
    "$ROOT/wavelog/tools/package.sh" > "$DIR/tools/package.sh"
chmod +x "$DIR/tools/package.sh"

printf 'bin/\nobj/\ndist/\n' > "$DIR/.gitignore"

cat > "$DIR/README.md" <<README
# $NAME

TODO — one paragraph: what it does, and what it is **not**.

## What you need

TODO

## Install

\`\`\`sh
./tools/package.sh            # prints the .zip and its sha256
\`\`\`

In Zeus: **Features → install local feature**, choose the zip.

## Status

**Nothing works yet.** Scaffold only.
README

cat > "$DIR/docs/design/source/design.md" <<DESIGN
# $NAME — design

Nothing is decided yet. Fill this in before writing code; the SSOT is this file,
and any rendering at \`docs/design/\` is derived from it.

## 1. What problem this solves

TODO

## 2. What is actually known

Record what has been **verified** and how, separately from what is assumed.
Assumptions that were never checked have been the source of every serious bug in
this repository — see \`../../../wavelog/docs/design/source/design.md\` §12.

## 3. Open questions
DESIGN

echo "$SLUG scaffolded:"
find "$DIR" -type f | sed "s|$ROOT/|  |" | sort
echo
echo "plugin id: $ID"
echo "next: fill in the design, then the entrypoint type ${PASCAL}Plugin"

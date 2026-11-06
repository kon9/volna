#!/usr/bin/env bash
# Real downloads of openly licensed media on this system, through the app's queue and the agent, each file checked
# with ffprobe. It needs the network and about fifteen minutes, so it runs before a release:
#   tools/acceptance.sh [output folder] [--only scenario,scenario]
set -euo pipefail
OUT="$(mkdir -p "${1:-acceptance-output}" && cd "${1:-acceptance-output}" && pwd)"
shift || true
export VOLNA_DATA="${VOLNA_DATA:-$OUT/data}"
dotnet run -c Release --project tools/Demo/Demo.csproj -- install
# Not in a scenario's own folder, which each scenario empties before it starts.
dotnet publish src/Volna.Agent/Volna.Agent.csproj -c Release -o "$OUT/build/agent" --nologo -v q
AGENT="$OUT/build/agent/volna-agent"
if [ -f "$AGENT.exe" ]; then AGENT="$AGENT.exe"; fi
dotnet run -c Release --project tools/Demo/Demo.csproj -- acceptance "$OUT" --agent "$AGENT" "$@"

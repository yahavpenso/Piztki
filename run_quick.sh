#!/bin/bash
# Builds Debug and runs one targeted off-screen selftest. Never runs anything if the build fails,
# because a stale exe would not know the PIZTKI_QUICK name and would run the whole on-screen test.
cd /c/Users/Public/Downloads/Piztki || exit 1
out=$(dotnet build -c Debug -v q 2>&1)
if ! echo "$out" | grep -q " 0 Error(s)"; then echo "$out" | grep -E " error " | sort -u | head -20; echo BUILD FAILED; exit 1; fi
D="$TEMP/piztki_$1"; rm -rf "$D"
PIZTKI_OFFSCREEN=1 PIZTKI_QUICK=$1 timeout ${2:-150} bin/Debug/net10.0-windows10.0.19041.0/Piztki.exe --selftest "$(cygpath -w "$D")"
echo exit=$?; ls "$D"

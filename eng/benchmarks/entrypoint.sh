#!/bin/sh
set -eu

# Fail before downloading game files if the isolated runtime/display cannot start.
# Each probe is bounded independently; the outer workflow still bounds the entire run.
printf '%s\n' 'Benchmark environment: native host probe'
timeout --signal=TERM --kill-after=5s 20s /harness/Nexa.Minecraft.Benchmarks --help
printf '%s\n' 'Benchmark environment: Java probe'
timeout --signal=TERM --kill-after=5s 20s /java/bin/java -version
printf '%s\n' 'Benchmark environment: starting virtual display for measured run'
# Use the same X server for the display probe and measured client. A successful probe of an
# already torn-down server says nothing about a second xvfb-run startup.
exec xvfb-run -a -s '-screen 0 1280x720x24' /bin/sh /benchmark-display-run.sh "$@"

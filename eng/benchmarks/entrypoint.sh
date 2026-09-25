#!/bin/sh
set -eu

# Fail before downloading game files if the isolated runtime/display cannot start.
# Each probe is bounded independently; the outer workflow still bounds the entire run.
printf '%s\n' 'Benchmark environment: native host probe'
timeout --signal=TERM --kill-after=5s 20s /harness/Nexa.Minecraft.Benchmarks --help
printf '%s\n' 'Benchmark environment: Java probe'
timeout --signal=TERM --kill-after=5s 20s /java/bin/java -version
printf '%s\n' 'Benchmark environment: virtual display probe'
timeout --signal=TERM --kill-after=5s 20s xvfb-run -a -s '-screen 0 1280x720x24' /harness/Nexa.Minecraft.Benchmarks --help
printf '%s\n' 'Benchmark environment: starting measured run'
exec xvfb-run -a -s '-screen 0 1280x720x24' /harness/Nexa.Minecraft.Benchmarks "$@"

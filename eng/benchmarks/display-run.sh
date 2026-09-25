#!/bin/sh
set -eu
printf '%s\n' 'Benchmark environment: virtual display ready'
timeout --signal=TERM --kill-after=5s 20s /harness/Nexa.Minecraft.Benchmarks --help
printf '%s\n' 'Benchmark environment: entering measured host'
exec /harness/Nexa.Minecraft.Benchmarks "$@"

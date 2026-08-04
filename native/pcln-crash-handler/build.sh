#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")"
OUT="pcln-crash-handler"
cc -O2 -o "$OUT" main.c
echo "Built $(pwd)/$OUT"

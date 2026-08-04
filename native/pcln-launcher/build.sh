#!/usr/bin/env sh
set -e
cd "$(dirname "$0")"
cc -O2 -o pcln-launcher main.c zip_store.c sha256.c install.c
echo "Built $(pwd)/pcln-launcher"

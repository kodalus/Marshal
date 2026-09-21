#!/usr/bin/env bash
# Aliasy przestrzeni nazw w XAML-u: każdy użyty ma być zadeklarowany.
set -euo pipefail
cd "$(dirname "$0")/.."
python3 tools/check-aliases.py

#!/usr/bin/env bash
# Czy ikony w repozytorium są tym, co rysuje tools/ikona.py.
#
# Skrypt nazywa się źródłem znaku, a plik obok niego kopią — dopóki nikt tego nie
# sprawdza, jest odwrotnie: kopia zostaje w tyle i nikt się nie dowiaduje. Tak stało
# się z ikoną pliku wykonywalnego na Windowsie, która przez kilka wydań pokazywała
# poprzedni znak.
set -euo pipefail
cd "$(dirname "$0")/.."

python3 tools/ikona.py > /dev/null

if ! git diff --quiet -- src/Marshal.UI/Assets src/Marshal.Android/Resources; then
  echo "Ikony rozjechały się z tym, co rysuje tools/ikona.py:"
  git diff --stat -- src/Marshal.UI/Assets src/Marshal.Android/Resources
  echo
  echo "Przerysowane pliki leżą już w drzewie — wystarczy je dołożyć do zmiany."
  exit 1
fi

echo "OK — ikony zgodne z tools/ikona.py."

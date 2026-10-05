#!/usr/bin/env bash
# Czy ikony w repozytorium są tym, co rysuje tools/ikona.py.
#
# Skrypt nazywa się źródłem znaku, a plik obok niego kopią — dopóki nikt tego nie
# sprawdza, jest odwrotnie: kopia zostaje w tyle i nikt się nie dowiaduje. Tak stało
# się z ikoną pliku wykonywalnego na Windowsie, która przez kilka wydań pokazywała
# poprzedni znak.
set -euo pipefail
cd "$(dirname "$0")/.."

# Pytanie jest o **to, co rysuje skrypt**, a nie o wszystko, co leży obok nierozliczone.
# Porównanie całych katalogów zapalało się przy każdej zmianie w układzie widgetu, bo
# ten mieszka w tym samym drzewie zasobów — a wtedy strażnik mówi „ikony się
# rozjechały" o rzeczy, która nie jest ikoną, i uczy się go pomijać.
rysowane=$(git ls-files \
  'src/Marshal.UI/Assets/marshal.*' \
  'src/Marshal.Android/Resources/mipmap*/ic_launcher*.png')

if [ -z "$rysowane" ]; then
  echo "Nie znalazłem w repozytorium żadnego pliku rysowanego przez tools/ikona.py."
  exit 1
fi

przed=$(git hash-object $rysowane)

python3 tools/ikona.py > /dev/null

po=$(git hash-object $rysowane)

if [ "$przed" != "$po" ]; then
  echo "Ikony rozjechały się z tym, co rysuje tools/ikona.py:"
  git diff --stat -- $rysowane
  echo
  echo "Przerysowane pliki leżą już w drzewie — wystarczy je dołożyć do zmiany."
  exit 1
fi

echo "OK — ikony zgodne z tools/ikona.py."

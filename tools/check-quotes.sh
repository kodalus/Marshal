#!/usr/bin/env bash
# Polski cudzysłów otwierający zamknięty prostym „"” kończy napis w połowie.
#
# Cztery razy w jednym dniu ten sam błąd: „Obszary", „Teraz", „Zapisz". Otwierający
# jest polski, zamykający wpisuje się odruchowo prosty — a kompilator widzi wtedy
# koniec napisu i wywala się kilkanaście znaków dalej, w miejscu bez związku
# z przyczyną. Sprawdzenie jest tańsze niż przebieg, który to wyłapie.
set -euo pipefail

blad=0

while IFS= read -r plik; do
  # Linie z napisem: cudzysłów otwierający, potem treść bez zamykającego polskiego,
  # a potem prosty cudzysłów. Komentarze pomijamy — tam prosty cudzysłów nie szkodzi.
  while IFS=: read -r numer tresc; do
    case "$tresc" in
      *///*|*"//"*) continue ;;
    esac

    # Komentarz w YAML-u i w powłoce: prosty cudzysłów tam nie szkodzi.
    case "${tresc#"${tresc%%[![:space:]]*}"}" in
      '#'*) continue ;;
    esac

    echo "$plik:$numer: polski cudzysłów zamknięty prostym"
    echo "    $tresc"
    blad=1
  done < <(grep -nP '„[^”"]*(?<!\\)"' "$plik" || true)
done < <(
  find src tests -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*'
  # Przebiegi CI też: tam ten sam błąd kończy napis powłoki, a powłoka mówi wtedy
  # „unexpected EOF while looking for matching" i nie mówi, w którym miejscu.
  # Kosztowało to jeden przebieg, w którym krok w ogóle się nie wykonał.
  find .github/workflows -name '*.yml'
)

if [ "$blad" -ne 0 ]; then
  echo
  echo 'Zamykający polski cudzysłów to ” (U+201D), nie ".'
  exit 1
fi

echo 'OK — cudzysłowy w napisach domknięte.'

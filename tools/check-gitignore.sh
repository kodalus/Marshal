#!/usr/bin/env bash
# Sprawdza .gitignore wobec listy przypadków w tools/gitignore-cases.txt.
# Format: IGN|ścieżka  — ma być ignorowana
#         KEEP|ścieżka — ma trafić do repozytorium
# Chroni przed wzorcami, które po cichu pochłaniają pliki źródłowe.
set -u
cd "$(dirname "$0")/.."
fail=0
while IFS='|' read -r want path; do
  [ -z "${want:-}" ] && continue
  if git check-ignore -q "$path"; then got=IGN; else got=KEEP; fi
  if [ "$want" != "$got" ]; then
    fail=$((fail+1))
    printf 'BŁĄD  oczekiwano %-4s otrzymano %-4s  %s\n' "$want" "$got" "$path"
    git check-ignore -v "$path" 2>/dev/null | sed 's/^/      reguła: /'
  fi
done < tools/gitignore-cases.txt
if [ "$fail" -eq 0 ]; then echo "OK — $(grep -c . tools/gitignore-cases.txt) przypadków"; fi
exit "$fail"

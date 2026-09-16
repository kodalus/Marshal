# tools

Skrypty pomocnicze uruchamiane ręcznie i w CI.

| Plik | Co robi |
|---|---|
| `check-gitignore.sh` | Sprawdza `.gitignore` wobec listy przypadków z `gitignore-cases.txt` |
| `gitignore-cases.txt` | `IGN\|ścieżka` — ma być ignorowana, `KEEP\|ścieżka` — ma trafić do repozytorium |

`.gitignore` to jedyny plik w projekcie, którego błąd nie daje żadnego sygnału:
nie wywala budowania ani testów, tylko po cichu gubi pliki. Stąd osobny test.

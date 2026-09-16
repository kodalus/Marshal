# Marshal

Planer zadań i projektów w metodzie GTD. Windows i Android, offline, jeden użytkownik,
synchronizacja przez Dysk Google, bez własnego backendu.

Aplikacja siostrzana do [Castellan](https://github.com/kodalus/Castellan) — ta pilnuje
zamku, ta układa porządek dnia. Wspólna konwencja: czysta architektura, EF Core + SQLite,
brak serwera, dane wyłącznie u użytkownika.

## Założenie

> Aplikacja nie może polegać na tym, że użytkownik zrobi przegląd.

GTD przegrywa z ADHD w dwóch miejscach: przegląd tygodniowy wymaga godziny skupienia bez
nagrody, a wybór z listy osiemdziesięciu akcji paraliżuje zamiast pomagać. Wszystko, co
w klasycznym GTD wychodzi na jaw dopiero podczas przeglądu — projekt bez następnej akcji,
sprawa czekająca od trzech tygodni, obszar życia milczący od pół roku — jest tutaj
**niezmiennikiem obliczalnym**, sprawdzanym przy każdym zapisie.

## Moduły

| Obszar | Co robi |
|--------|---------|
| Skrzynka | Wrzucanie bez pól, przetwarzanie drzewkiem decyzyjnym GTD |
| Dzisiaj | Pięć slotów wyboru na dzień, wydarzenia, ponaglenia, projekty zablokowane |
| Teraz | 3–5 zadań dobranych po dostępnym czasie i energii |
| Projekty | Drzewo: obszar → cel → projekt → zadania |
| Obszary | Dziesięć obszarów odpowiedzialności, tabela równowagi |
| Oczekiwane | Czekam na kogoś, z licznikiem dni i progiem per obszar |
| Kalendarz | Godzinowo, z Google Calendar i kanałów iCal |
| Przegląd | Kreator ośmiu kroków, wznawialny po każdej pojedynczej pozycji |
| Notatki | Markdown z podglądem, załączniki |
| Filtry | Warunki łączone, zapisywane do ulubionych |
| Kopia | Eksport i import JSON, offline |

## Stos

.NET 10, Avalonia UI 11, EF Core 10 + SQLite. Jeden projekt UI na Windows i Androida.

## Synchronizacja

Każde urządzenie zapisuje wyłącznie własny plik zmian w folderze na Dysku Google i nigdy
nie modyfikuje cudzego. Dzięki temu konflikt zapisu nie jest rozwiązywany — on nie
powstaje. To jedyny powód, dla którego projekt nie potrzebuje serwera.

Scalanie per pole, po hybrydowym zegarze logicznym (HLC), więc zmiana priorytetu na
telefonie i tytułu na desktopie w trybie offline daje po scaleniu oba.

## Dokumentacja

Specyfikacja techniczna: [`docs/marshal-spec.md`](docs/marshal-spec.md).

## Licencja

MIT.

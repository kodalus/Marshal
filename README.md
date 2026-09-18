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
| Kalendarz | Godzinowo, dzień/3 dni/tydzień/miesiąc, z Google Calendar i kanałów iCal |
| Przegląd | Kreator ośmiu kroków, wznawialny po każdej pojedynczej pozycji |
| Notatki | Markdown z podglądem, załączniki |
| Filtry | Warunki łączone, zapisywane do ulubionych |
| Kopia | Eksport i import JSON, offline |

## Kalendarz i udostępnianie

Wydarzenie z Google i zadanie z Marshala mają być pod ręką **tą samą rzeczą**: ta sama
karta, te same gesty, to samo kasowanie.

**Obszar wskazuje kalendarz Google.** Kalendarz rodzinny to obszar „Dzieci", firmowy to
„Praca". Z tego jednego przypisania wynikają dwie rzeczy naraz: zadania obszaru lądują
w jego kalendarzu same, a wydarzenia stamtąd należą do tego obszaru — mają jego barwę
i dają się przełożyć gdzie indziej jedną zmianą pola.

Dzięki temu obszar wydarzenia nie musi być nigdzie zapisany: wynika z tego, w którym
kalendarzu ono stoi. To odpowiedź na pytanie, na które Google nie ma pola.

**Udostępnianie idzie dwiema drogami Google, a nie trzecią własną.** Udostępniony
kalendarz znaczy „ta półka jest nasza wspólna"; gość przy wydarzeniu znaczy „spójrz na
to jedno" — osoba dostaje zaproszenie i może odmówić. Marshal trzyma tylko krótką listę
osób, żeby nie wpisywać adresu za każdym razem.

## Prywatność

Brak serwera, brak analityki, brak telemetrii. Dane zostają na urządzeniu, a przy
włączonej synchronizacji — na **twoim** koncie Google, przy poświadczeniach OAuth, które
zakładasz sama. Autor aplikacji nie ma do niczego dostępu i nie figuruje w tej relacji.

Pełny tekst: [`docs/prywatnosc.md`](docs/prywatnosc.md).

## Instalacja

APK na Androida i samodzielna paczka na Windows, obie z sumą SHA-256, leżą
w [wydaniach](https://github.com/kodalus/Marshal/releases). Aplikacji nie ma w żadnym
sklepie, więc Android poprosi o zgodę na instalację spoza sklepu, a Windows pokaże
ostrzeżenie SmartScreen — jedno i drugie mówi o pochodzeniu pliku, nie o jego zawartości.

**Pełna instrukcja dla kogoś, kto dostał link:**
[`docs/instalacja.md`](docs/instalacja.md).

Do synchronizacji i kalendarza Google potrzebne są **własne** poświadczenia; krok po
kroku opisuje je [`docs/google-dysk.md`](docs/google-dysk.md). Bez nich aplikacja działa
w całości, tylko offline i na jednym urządzeniu.

## Stos

.NET 10, Avalonia UI 11, EF Core 10 + SQLite. Jeden projekt UI na Windows i Androida.

## Synchronizacja

Każde urządzenie zapisuje wyłącznie własny plik zmian w folderze na Dysku Google i nigdy
nie modyfikuje cudzego. Dzięki temu konflikt zapisu nie jest rozwiązywany — on nie
powstaje. To jedyny powód, dla którego projekt nie potrzebuje serwera.

Scalanie per pole, po hybrydowym zegarze logicznym (HLC), więc zmiana priorytetu na
telefonie i tytułu na desktopie w trybie offline daje po scaleniu oba.

## Dokumentacja

| Dokument | O czym |
|----------|--------|
| [`docs/instalacja.md`](docs/instalacja.md) | Instalacja na Androidzie i Windows — dla kogoś, kto dostał link |
| [`docs/marshal-spec.md`](docs/marshal-spec.md) | Specyfikacja techniczna — model, niezmienniki, algorytmy, synchronizacja |
| [`docs/google-dysk.md`](docs/google-dysk.md) | Własny projekt Google Cloud, zgody, poświadczenia — krok po kroku |
| [`docs/android-podpis.md`](docs/android-podpis.md) | Podpisywanie APK i budowanie wydania |
| [`docs/prywatnosc.md`](docs/prywatnosc.md) | Polityka prywatności |

## Licencja

MIT.

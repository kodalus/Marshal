# Marshal — specyfikacja techniczna

Planer zadań i projektów w metodzie GTD. Windows i Android, offline, jeden użytkownik,
synchronizacja przez Dysk Google, bez własnego backendu.

Dokument roboczy, po polsku. Marshal dworu układał porządek dnia — kasztelan
(Castellan) pilnował zamku. Aplikacje siostrzane, wspólna konwencja nazw.

---

## 1. Przegląd

### 1.1 Zadanie

Przenieść GTD z Singularity do własnej aplikacji, zachowując mechanikę metody
i usuwając dwa miejsca, w których metoda przegrywa z ADHD:

1. **Przegląd tygodniowy.** Wymaga godziny nieprzerwanego skupienia i nie daje
   natychmiastowej nagrody. To najczęstszy punkt porzucenia GTD.
2. **Wybór z listy.** GTD nie ma priorytetów z założenia — wybierasz po kontekście,
   czasie i energii. Lista osiemdziesięciu następnych akcji to nie pomoc, tylko paraliż.

### 1.2 Kluczowa zasada

> Aplikacja nie może polegać na tym, że użytkownik zrobi przegląd.

Wszystko, co w klasycznym GTD wychodzi na jaw dopiero podczas przeglądu tygodniowego —
projekt bez następnej akcji, sprawa, na którą czekasz od trzech tygodni, zadanie leżące
w skrzynce od dziesięciu dni — jest **niezmiennikiem obliczalnym**. System sprawdza je
przy każdym zapisie i sam się zgłasza. Przegląd przestaje być warunkiem działania
systemu, a staje się porządkowaniem.

To ta sama zasada co w Castellanie („aplikacja nie powinna polegać na pamięci
użytkownika"), przeniesiona z pieniędzy na czas.

### 1.3 Druga zasada

> Wrzucenie myśli kosztuje dwie sekundy.

Capture nie ma pól. Tytuł i koniec. Żadnego projektu, terminu, priorytetu, szacowanego
czasu — nic. Wszystkie atrybuty dochodzą dopiero przy przetwarzaniu skrzynki, i wszystkie
są opcjonalne.

Wynika to wprost z GTD, gdzie zbieranie i przetwarzanie są osobnymi krokami wykonywanymi
w innym czasie. Formularz przy wrzucaniu zabija zbieranie, a system bez zbierania jest
pusty i przestaje być używany w ciągu tygodnia.

### 1.4 Trzecia zasada

> Kalendarz jest święty.

W kalendarzu ląduje wyłącznie to, co **musi** wydarzyć się w danym dniu o danej godzinie —
wizyta, spotkanie, odbiór dziecka. Nigdy „chciałabym w czwartek".

Konsekwencja dla modelu: **zadanie i wydarzenie to dwie różne encje**, nie jedna encja
z opcjonalną datą. Z chwilą, w której do kalendarza trafiają życzenia, przestaje on być
wiarygodny, a wtedy cały system przestaje działać — bo kalendarz jest jedynym miejscem,
któremu wolno ufać bezwarunkowo.

Zadania z wyznaczonym dniem wykonania są widoczne w widoku kalendarza, ale rysowane
inaczej (blok półprzezroczysty, bez ramki) i zawsze pod wydarzeniami. Wizualnie nie da
się ich pomylić.

### 1.5 Czwarta zasada

> Termin to nie to samo co moment wykonania.

`Deadline` — fakt zewnętrzny, nie podlega negocjacji (rozliczenie do 30., wniosek do
wtorku). `DoDate` — Twoja decyzja, kiedy się tym zajmiesz. Dwa osobne pola.

Większość aplikacji ma jedno pole „due date" i miesza te rzeczy, przez co przesunięcie
planu wygląda jak zerwanie terminu. Efekt: przesunięcia przestają boleć, więc prawdziwe
terminy też przestają.

### 1.6 Piąta zasada

> Waga to nie wybór.

Dwie różne rzeczy, których nie wolno trzymać w jednym polu:

- **Waga** (`Priority`) — jak bardzo rzecz jest ważna. Właściwość zadania. Przez rok
  sto zadań może zasłużyć na „wysoki" i to nie jest inflacja, tylko prawda.
- **Wybór** (`FocusDate`) — czym zajmuję się dzisiaj. Właściwość **dnia**, nie zadania.
  Najwyżej pięć, bo doba ma tyle, ile ma.

Limit nałożony na wagę kazałby odbierać zadaniu ważność, którą naprawdę ma, żeby zmieścić
się w liczbie dotyczącej czego innego. To ten sam błąd co jedno pole „due date" udające
termin i moment wykonania (1.5).

Konsekwencja praktyczna: limit pięciu działa bez oporu, bo dotyczy jednego dnia i zeruje
się co dobę. Nie ma czego gromadzić, więc nie powstaje sterta.

### 1.7 Ograniczenia

Nigdy nie wchodzi w zakres: własny serwer, konta użytkowników, drugi użytkownik, praca
zespołowa, przypisywanie wykonawców, komentarze, kanban, diagram Gantta, trekker nawyków,
Pomodoro, bot Telegram, zadania z e-maila, publiczne API, iOS, wersja webowa, zegarki,
publikacja w Google Play.

### 1.8 Horyzonty skupienia

GTD ma sześć poziomów („wysokości lotu"). Nie wchodzą do wersji 1 jako sześć osobnych
modułów, bo mają skrajnie różny stosunek wartości do kosztu. Kryterium jest to samo,
co w całym dokumencie: **czy aplikacja może to sprawdzić za użytkownika.**

| Poziom | GTD | Rozwiązanie tutaj |
|---|---|---|
| Pas startowy | Następne akcje | `Task` |
| H1 | Projekty | `Project` |
| H2 | **Obszary odpowiedzialności** | `Area` — pełnoprawna encja, rozdz. 5.2 |
| H3 | Cele 1–2 lata | `Project.ParentProjectId` — cel to projekt o dłuższym horyzoncie |
| H4 | Wizja 3–5 lat | Notatka z `IsPinned` |
| H5 | Sens i zasady | Notatka z `IsPinned` |

**H2 wchodzi jako encja**, bo jako jedyny z górnych poziomów przechodzi test
obliczalności: przy podziale wymaganym i jednokrotnym równowaga życia staje się
zapytaniem SQL (8.5, N10). To ta sama klasa mechanizmu co N1, piętro wyżej.

**H3 nie dostaje własnego pojęcia.** Większość tego, co trafia do „celów", to projekt
z podprojektami — „prawo jazdy" to teoria → egzamin teoretyczny → jazdy → egzamin
praktyczny → odbiór dokumentu. Zagnieżdżenie projektów załatwia to bez nowej encji i bez nowego miejsca
do zaniedbania.

**H4–H5 nie dostają modułu.** Nie istnieje zapytanie „ten projekt nie służy Twojej
wizji" — to wymaga osądu za każdym razem, więc moduł dodałby wprowadzanie danych bez
dodania mechanizmu. Wizja i zasady to tekst: notatka przypięta, pokazywana jako krok
zerowy kreatora przeglądu. Koszt zerowy, jedna flaga.

---

## 2. Etapy

| Etap | Zawartość | Rezultat |
|---|---|---|
| 0 | Szkielet, BD, testy, CI, okno desktopowe i ekran Androida | Pusta aplikacja startuje na obu platformach |
| 1 | Zadania, podzadania, projekty, skrzynka, drzewko przetwarzania | Działające GTD na jednym urządzeniu |
| 2 | Dzisiaj, Plany, Kiedyś, Archiwum, **obszary**, zagnieżdżanie projektów, tagi, priorytety, kolory | Pełna nawigacja po sekcjach |
| 3 | **Synchronizacja przez Dysk Google** | Telefon i desktop to jedna aplikacja — punkt bez odwrotu |
| 4 | Powtarzalność, terminy, przypomnienia | Zadania cykliczne przestają wymagać pamięci |
| 5 | Oczekiwane, wykrywanie zablokowanych projektów, równowaga obszarów, kreator przeglądu | Mechanizmy zastępujące dyscyplinę |
| 6 | Widok „Teraz", szacowany czas i energia | Aplikacja wybiera za Ciebie |
| 7 | Kalendarz godzinowy, odczyt z Google Calendar i iCal | Czas i zadania w jednym miejscu |
| 8 | Notatki w Markdown, załączniki | Materiały referencyjne |
| 9 | Filtry łączone, filtry zapisane | Własne widoki |
| 10 | Widget Androida, tryb ciemny, kopia zapasowa | Domknięcie |
| Później | Dwustronny zapis do Google Calendar | Osobno, po przeżyciu etapu 7 |

**Ostrzeżenie dotyczące etapów 1–2.** Dają aplikację działającą na jednym urządzeniu.
To jest gorsze niż Singularity i nie ma sensu z tym „żyć" — prawdziwa eksploatacja
zaczyna się od etapu 3. Przerwa między etapem 2 a 3 to najbardziej prawdopodobny moment
porzucenia projektu.

**Ostrzeżenie dotyczące etapu 7.** Zapis do Google Calendar jest wyłączony z wersji 1
celowo. Błąd w dwustronnej synchronizacji potrafi skasować wydarzenia w prawdziwym
kalendarzu — jedyne miejsce w całym projekcie, gdzie awaria niszczy dane poza aplikacją.

---

## 3. Decyzje techniczne

### 3.1 Platforma

**Avalonia UI 11, .NET 10 (LTS).** Windows (desktop) i Android (API 36, minimum API 29).

Uzasadnienie:

- XAML + MVVM + style + bindingi — wiedza z WPF przenosi się niemal bez strat.
- Jeden projekt UI na obie platformy. Desktop jest natywny, nie WebView.
- Desktop musi być pełnoprawny: przetwarzanie skrzynki i przegląd tygodniowy to praca
  klawiaturą, nie kciukiem.

**Uwaga — to odwrotna decyzja niż w Castellanie**, gdzie Avalonia została odrzucona na
rzecz MAUI. Tam liczył się wyłącznie Android i dostęp do `NotificationListenerService`;
desktopu nie było w zakresie. Tutaj desktop jest wymaganiem, a MAUI na Windows (WinUI)
wypada wyraźnie słabiej niż Avalonia. Przesłanki się zmieniły, więc decyzja też.

Warstwy `Domain` i `Application` są czystym C# bez zależności od UI — przenoszą się
między MAUI a Avalonią bez zmian. Wzorce z Castellana nadają się do skopiowania.

Odrzucone:

- **MAUI** — mobile-first, desktop drugiej kategorii.
- **Blazor Hybrid** — miałoby sens, gdyby w planach był web. Nie jest.
- **Uno Platform** — porównywalny, mniejsza społeczność, brak przewagi.

### 3.2 Przechowywanie danych

**EF Core 10 + SQLite**, migracje stosowane przy starcie (`Database.Migrate()`).

Ścieżka bazy:

| Platforma | Katalog |
|---|---|
| Windows | `%LOCALAPPDATA%\Marshal\` |
| Android | `Context.FilesDir` |

### 3.3 Identyfikatory

`Guid` wersji 7 (`Guid.CreateVersion7()`), generowane **na kliencie**. Warunek konieczny
synchronizacji: dwa urządzenia offline muszą móc tworzyć rekordy bez kolizji.

### 3.4 Czas

`DateTimeOffset` w ISO-8601 dla momentów, `DateOnly` dla dni.

Rozróżnienie krytyczne przy powtarzalności:

- **Dzień** (`DateOnly`) — termin, dzień wykonania, powtórzenie. Bez strefy. „Wtorek" jest
  wtorkiem niezależnie od tego, gdzie jesteś.
- **Moment** (`DateTimeOffset`) — wydarzenie kalendarzowe, przypomnienie. Ze strefą.

Strefa domyślna Europe/Warsaw, przechowywana jawnie w ustawieniach (nie brana z systemu),
żeby wyjazd nie przestawiał terminów.

### 3.5 Zegar logiczny

Każda zmiana dostaje znacznik **HLC** (Hybrid Logical Clock): `(czasŚcienny, licznik, idUrządzenia)`.

Zegar systemowy telefonu bywa przestawiony. Porównywanie po `DateTime.UtcNow` daje
niedeterministyczne scalanie — rekord „z przyszłości" wygrywa na zawsze. HLC jest
monotoniczny mimo cofnięcia zegara, a `idUrządzenia` rozstrzyga remisy deterministycznie:
oba urządzenia dochodzą do tego samego wyniku niezależnie od kolejności scalania.

### 3.6 Biblioteki

| Przeznaczenie | Wybór |
|---|---|
| UI | `Avalonia` 11 |
| MVVM | `CommunityToolkit.Mvvm` |
| DI | `Microsoft.Extensions.DependencyInjection` |
| ORM | `Microsoft.EntityFrameworkCore.Sqlite` 10 |
| Markdown | `Markdig` (parser) + `Markdown.Avalonia` (podgląd) |
| Dysk Google | `Google.Apis.Drive.v3` |
| Kalendarz Google | `Google.Apis.Calendar.v3` |
| iCal | `Ical.Net` |
| Testy | `xUnit`, `FluentAssertions` |
| Serializacja | `System.Text.Json` |

Bez mediatora, tak jak w Castellanie.

---

## 4. Architektura

### 4.1 Projekty

```
Marshal.Domain           encje, wartości, niezmienniki — czysty C#
Marshal.Application      scenariusze, interfejsy repozytoriów, algorytmy
Marshal.Infrastructure   EF Core, sync, Google API, pliki
Marshal.UI               Avalonia — widoki, ViewModele, style (współdzielone)
Marshal.Desktop          host Windows
Marshal.Android          host Android + widget (natywny)
Marshal.Tests            xUnit
```

`Domain` nie zna niczego. `Application` zna `Domain`. `Infrastructure` i `UI` znają
`Application`. Hosty znają `UI`.

### 4.2 Widget Androida

Widget na ekranie głównym **nie jest Avalonią**. To natywny `AppWidgetProvider`
(RemoteViews) w projekcie `Marshal.Android`, czytający bazę SQLite bezpośrednio przez
osobny, minimalny `DbContext` w trybie tylko do odczytu.

Zakres widgetu: lista 3–5 pozycji z widoku „Teraz", przycisk odhaczenia, przycisk
szybkiego wrzutu do skrzynki. Nic więcej — każda funkcja w widgecie jest utrzymywana
podwójnie.

---

## 5. Model domenowy

### 5.1 Pola wspólne (synchronizowalne)

Każdy agregat podlegający synchronizacji ma:

| Pole | Typ | Znaczenie |
|---|---|---|
| `Id` | `Guid` (v7) | generowany na kliencie |
| `CreatedAt` | `DateTimeOffset` | informacyjnie |
| `UpdatedAt` | `Hlc` | zegar logiczny, podstawa scalania |
| `Deleted` | `bool` | **nagrobek — nigdy fizyczne DELETE** |

Fizyczne usunięcie rekordu jest w systemie synchronizowanym niemożliwe: urządzenie
offline nie odróżni „skasowane" od „jeszcze nie znam" i wskrzesi rekord przy najbliższym
scaleniu.

### 5.2 Area (agregat)

Obszar odpowiedzialności — H2. Coś, co się **nigdy nie kończy** i co się utrzymuje na
jakimś poziomie. Odróżnienie od celu: cel się domyka, obszar generuje cele i projekty
w nieskończoność.

| Pole | Typ | Uwagi |
|---|---|---|
| `Name` | `string` | |
| `Color` | `string?` | dziedziczony przez projekty bez własnego koloru |
| `SortOrder` | `double` | |
| `IsActive` | `bool` | nieaktywny znika z podziału i z analizy równowagi |
| `QuietDays` | `int` | próg ciszy dla N10 |
| `DefaultNudgeDays` | `int` | domyślny próg ponaglenia „Oczekiwanych" dla zadań tego obszaru |

`DefaultNudgeDays` jest per obszar, nie globalny, bo jeden próg gwarantuje, że lista
„Oczekiwane" zamieni się w szum. Siedem dni dla urzędu jest absurdalnie agresywne —
urząd nie odpowiada w tydzień. Siedem dni dla osoby w domu jest za wolne.
`Task.WaitingNudgeDays` nadpisuje wartość obszaru przy konkretnym zadaniu.

**Obszar to nie tag.** Tag jest opcjonalny i wiele-do-wielu. Obszar jest wymagany
i jednokrotny, bo inaczej nie jest podziałem — projekt w trzech obszarach albo w żadnym
psuje każdą liczbę w tabeli równowagi. To jedyny powód, dla którego nie da się tego
zrobić tagami.

**Reguła rozstrzygania sporów**, gdy sprawa pasuje do dwóch obszarów:

> Przypisz do obszaru, **którego standard ucierpi, jeśli tego nie zrobisz.**

„Wizyta u pediatry" — ucierpi zdrowie dziecka, nie logistyka domu. „Wymiana opon" —
ucierpi dom, nie finanse, mimo że kosztuje.

Test na poprawnie nazwany obszar: czy działa zdanie *„utrzymuję X na poziomie, który
mnie satysfakcjonuje"*. „Utrzymuję projekty własne" nie działa — pojemnika się nie
zaniedbuje, tylko się go nie napełnia, więc pusty licznik nic nie znaczy. „Utrzymuję
twórczość" działa.

#### Dane początkowe

Zakładane migracją przy pierwszym uruchomieniu, edytowalne:

| # | Obszar | Zakres | `Quiet` | `Nudge` |
|---|---|---|---|---|
| 1 | Praca | Kontrakt, rozwój zawodowy, zabezpieczenie na wypadek zakończenia projektu | 14 | 5 |
| 2 | Dzieci | Przedszkole, żłobek, zajęcia, **zdrowie dzieci** | 14 | 5 |
| 3 | Zdrowie | **Moje** — leczenie, badania, sen, ruch, jedzenie, suplementy | 30 | 7 |
| 4 | Dom | Przestrzeń, naprawy, zakupy, samochód, logistyka tygodnia | 30 | 7 |
| 5 | Finanse | Budżet, zobowiązania, ubezpieczenia, oszczędności | 30 | 7 |
| 6 | Związek | | 21 | 3 |
| 7 | Rozwój własny | **Wejście** — nauka, medytacja, czytanie, praca wewnętrzna | 45 | 7 |
| 8 | Twórczość | **Wyjście** — to, co powstaje i wychodzi na zewnątrz | 45 | 7 |
| 9 | Sprawy urzędowe | Urzędy, podatki, ubezpieczenia, zapisy, dokumenty | 60 | 21 |
| 10 | Relacje | Rodzina i znajomi poza domem | 45 | 7 |

Progi ciszy przy obszarach o dużym ruchu (Praca, Dzieci) praktycznie nigdy nie zadziałają
i tak ma być — to nie są obszary, które się zaniedbuje. Wartość mechanizmu leży cała
w dolnej połowie tabeli. Wszystkie liczby są wstępne, do korekty po miesiącu używania.

Trzy granice są ustalone celowo, bo bez nich podział przecieka i liczniki kłamią:
**zdrowie moje kontra dzieci** (leczenie dziecka idzie pod „Dzieci"), **dom kontra finanse**
(przestrzeń i logistyka kontra przepływy), **rozwój kontra twórczość** (co biorę kontra
co wypuszczam).

Obszar 9 istnieje osobno, bo generuje najwięcej zadań z twardym `Deadline` i zasila
większość listy „Oczekiwane" — urząd, tłumacz, lekarz. Bez własnego miejsca sprawy
urzędowe rozpływają się między „dom" a „finanse" i wypadają, bo nikt się o nie nie
upomina do momentu, w którym jest za późno.

---

### 5.3 Task (agregat)

Skrzynka nie jest osobną encją — to zadanie w statusie `Inbox`. Dzięki temu przetworzenie
pozycji ze skrzynki jest zmianą statusu, a nie konwersją z utratą identyfikatora
i historii.

| Pole | Typ | Uwagi |
|---|---|---|
| `Title` | `string` | jedyne pole wymagane |
| `Note` | `string?` | Markdown |
| `Status` | enum | `Inbox`, `Next`, `Waiting`, `Scheduled`, `Someday`, `Done`, `Trashed` |
| `ProjectId` | `Guid?` | |
| `AreaId` | `Guid` | **wymagany**; dziedziczony z projektu, przy zadaniu samodzielnym wybierany wprost |
| `ParentTaskId` | `Guid?` | zagnieżdżanie bez ograniczeń |
| `Priority` | enum | `Brak`, `Niski`, `Normalny`, `Wysoki` |
| `Color` | `string?` | |
| `Deadline` | `DateOnly?` | fakt zewnętrzny |
| `DoDate` | `DateOnly?` | decyzja własna |
| `DoTime` | `TimeOnly?` | tylko jeśli dzień ma sens godzinowy |
| `DeferUntil` | `DateOnly?` | niewidoczne przed tą datą |
| `FocusDate` | `DateOnly?` | dzień, na który zadanie wybrano — **najwyżej 5 na dzień** (8.6) |
| `RollCount` | `int` | ile razy `DoDate` przesunęło się samo (8.7) |
| `FocusMissCount` | `int` | ile razy było wybrane na dany dzień i niewykonane (8.6) |
| `EstimatedMinutes` | `int?` | opcjonalne, na potrzeby „Teraz" |
| `Energy` | enum | `Nieznana`, `Niska`, `Średnia`, `Wysoka` |
| `WaitingForWho` | `string?` | tylko przy `Waiting` |
| `WaitingSince` | `DateOnly?` | tylko przy `Waiting` |
| `WaitingNudgeDays` | `int?` | puste = weź `Area.DefaultNudgeDays` |
| `Recurrence` | `Recurrence?` | typ własny, 5.7 |
| `CompletedAt` | `DateTimeOffset?` | |
| `SortOrder` | `double` | pozycja ręczna; wstawienie między sąsiadów to średnia |

Checklista wewnątrz zadania to podzadania z `ParentTaskId` — bez osobnego mechanizmu.

`Priority` **nie ma limitu** — zob. 1.6. Zadania bez projektu są w GTD całkowicie
poprawne („wymienić żarówkę" nie jest projektem) i nie są nigdzie zgłaszane jako brak.
Wymagany jest natomiast obszar, inaczej tabela równowagi (8.5) pokazywałaby część życia,
wyglądając na kompletną — a to gorsze niż jej brak.

### 5.4 Project (agregat)

| Pole | Typ | Uwagi |
|---|---|---|
| `Outcome` | `string` | **nazwa przez rezultat**, nie przez czynność |
| `Note` | `string?` | Markdown |
| `Status` | enum | `Active`, `Someday`, `Done` |
| `AreaId` | `Guid` | **wymagane** — bez tego podział przecieka |
| `ParentProjectId` | `Guid?` | zagnieżdżanie; projekt nadrzędny to H3, czyli cel |
| `Color` | `string?` | domyślnie z obszaru |
| `SortOrder` | `double` | |

Podprojekt dziedziczy `AreaId` z nadrzędnego i nie może go zmienić — inaczej cel
rozjechałby się po kilku obszarach i przestał być policzalny.

Głębokość zagnieżdżenia nieograniczona technicznie, ale w analizie równowagi (8.5)
liczone są **wszystkie** projekty w drzewie, nie tylko korzenie.

Pole `Outcome` zamiast `Name` to decyzja celowa: GTD wymaga, żeby projekt był opisany
stanem docelowym („Zimowe opony są na aucie"), nie czynnością („wymiana opon").
Formularz pyta „po czym poznasz, że to skończone?".

### 5.5 Tag, Note, Attachment

`Tag` — `Name`, `Color`.

`Note` (agregat, samodzielna notatka) — `Title`, `Body` (Markdown), `AreaId?`,
`IsPinned`, tagi, załączniki.

`IsPinned` obsługuje H4–H5: wizja i zasady to przypięte notatki, pokazywane jako krok
zerowy kreatora przeglądu (8.3). Nie ma osobnego modułu wizji — zob. 1.7.

`Attachment` — `OwnerType` (`Task` / `Note`), `OwnerId`, `FileName`, `SizeBytes`,
`Sha256`, `MimeType`. Plik trzymany pod nazwą będącą skrótem SHA-256, więc ten sam
załącznik dodany dwa razy zajmuje miejsce raz.

### 5.6 CalendarEvent

Wydarzenia pobierane z Google Calendar i z kanałów iCal. W wersji 1 **tylko do odczytu**
i **nie podlegają synchronizacji przez Dysk** — każde urządzenie pobiera je samodzielnie
ze źródła. Lokalna kopia to bufor, nie dane.

`ExternalId`, `SourceId`, `Title`, `Start`, `End`, `IsAllDay`, `Location`, `EtagOrVersion`.

### 5.7 Recurrence (typ własny, nie RRULE)

| Pole | Wartości |
|---|---|
| `Kind` | `Daily`, `EveryNDays`, `Weekly`, `Monthly`, `Yearly` |
| `Interval` | `int` |
| `DaysOfWeek` | zbiór dni (dla `Weekly`) |
| `DayOfMonth` | `int?` lub `LastDay` |
| `Anchor` | `FromScheduled` \| `FromCompletion` — domyślny **wyprowadzany z `Kind`** |
| `OnMissed` | `Skip` \| `Carry` \| `Accumulate` |
| `Until` | `DateOnly?` |
| `Count` | `int?` |

RRULE z iCal jest odrzucony celowo: obsługuje przypadki, których nigdy nie użyjesz,
a nie ma pojęcia `Anchor` ani `OnMissed` — czyli dokładnie tego, co jest tu istotne.
Import RRULE z Google Calendar odbywa się do tego typu, stratnie, z oznaczeniem.

**Wartość domyślna `Anchor` nie jest stała, tylko liczona z `Kind`:**

| `Kind` | Domyślny `Anchor` | Dlaczego |
|---|---|---|
| `Weekly`, `Monthly`, `Yearly` | `FromScheduled` | nazywasz konkretny dzień („poniedziałek", „15-go") — to z definicji rytm narzucony z zewnątrz |
| `Daily`, `EveryNDays` | `FromCompletion` | „co 3 dni" nie ma zaczepienia w świecie; gdyby miało, powiedziałabyś „w poniedziałki i czwartki" |

W razie wątpliwości `FromScheduled`, bo jego błąd jest widoczny: dryf „co poniedziałek"
na środy zauważysz w dwa tygodnie. Błąd `FromCompletion` jest niewidoczny — rytm jest
po prostu lekko nie taki.

**Anchor** — od czego liczyć następne wystąpienie:

- `FromScheduled` — od zaplanowanej daty. „Co poniedziałek śmieci": odhaczenie we wtorek
  nie przesuwa kolejnego poniedziałku.
- `FromCompletion` — od faktycznego wykonania. „Co 3 dni podlewanie": odhaczenie
  z dwudniowym opóźnieniem przesuwa cały rytm.

**OnMissed** — co z niewykonanym wystąpieniem:

- `Skip` — przepada. Dla rzeczy bez wartości po terminie.
- `Carry` — zostaje jako zaległe, kolejne wystąpienie go zastępuje. Zawsze widoczne
  jedno. **Domyślne.**
- `Accumulate` — każde niewykonane zostaje osobno. Trzy nieodhaczone treningi to trzy
  pozycje. Dla rzeczy naprawdę policzalnych.

---

## 6. Niezmienniki

Sprawdzane przy zapisie, nie podczas przeglądu. To jest realizacja zasady 1.2.

| # | Niezmiennik | Reakcja |
|---|---|---|
| N1 | Projekt `Active` ma ≥ 1 zadanie w statusie `Next` | oznaczenie **zablokowany**, pozycja w „Dzisiaj" |
| N2 | Zadanie `Waiting` ma `WaitingForWho` i `WaitingSince` | blokada zapisu |
| N3 | `Waiting` starsze niż `WaitingNudgeDays` (lub `Area.DefaultNudgeDays`) | pozycja „ponagl" w „Dzisiaj" |
| N4 | Pozycja w `Inbox` starsza niż 7 dni | licznik „skrzynka zalega" |
| N5 | Zadanie z `Deadline` w przeszłości, niewykonane | pozycja „przeterminowane" |
| N6 | `DeferUntil` ≤ dziś dla zadania `Someday` | propozycja powrotu do `Next` |
| N7 | Zadanie `Done` nie ma niewykonanych podzadań | ostrzeżenie, nie blokada |
| N8 | Zadanie `Scheduled` ma `DoDate` | blokada zapisu |
| N9 | Projekt `Done` nie ma zadań poza `Done`/`Trashed` | ostrzeżenie |
| N10 | Obszar `IsActive` ma ruch (zapis w projekcie lub zadaniu) w ciągu `QuietDays` | pozycja w kroku 8 przeglądu |
| N11 | Projekt i zadanie mają `AreaId`; podprojekt ma ten sam co nadrzędny | blokada zapisu |
| N12 | `Carry` przenoszone nie dłużej niż 30 dni | pozycja w przeglądzie: rytm do zmiany albo do skasowania |
| N13 | `FocusMissCount` < 4 | pozycja w przeglądzie: wybierane co tydzień i nierobione |
| N14 | Najwyżej 5 zadań z tym samym `FocusDate` | blokada zapisu, pytanie które schodzi |
| N15 | `RollCount` < 4 | pozycja w przeglądzie: przesunięte cztery razy — czy to prawdziwe zadanie |

N1 jest najważniejszy w całym dokumencie. „Projekt bez następnej akcji" to w klasycznym
GTD rzecz, którą wyłapuje się okiem raz w tygodniu. Tutaj jest zapytaniem SQL.

N10 jest tym samym mechanizmem piętro wyżej: „od trzech miesięcy nie zrobiłam nic dla
własnego zdrowia" to spostrzeżenie, które w klasycznym GTD przychodzi podczas przeglądu
miesięcznego albo nie przychodzi wcale. **N10 nigdy nie trafia do „Dzisiaj"** — to nie
jest sprawa na dziś i codzienne przypominanie o niej zamieniłoby ją w szum.

N12, N13 i N15 to jeden mechanizm w trzech miejscach: **licznik zamiast kary.** Zadanie
przenoszone od trzech miesięcy, wybierane co tydzień i nierobione, albo powtarzalne
i wiecznie zaległe — to nie jest lenistwo do zgromienia, tylko informacja, że zapis jest
nieprawdziwy. Zwykle odpowiedź brzmi „to nie jest jedno zadanie, tylko projekt" albo
„to nie jest moje". Jedno i drugie da się naprawić; czerwona plakietka nie daje się
naprawić niczym.

---

## 7. Drzewko przetwarzania skrzynki

Ekran przetwarzania pokazuje **jedną pozycję naraz**, na pełnym ekranie, bez listy
w tle — lista pozostałych pozycji jest rozpraszaczem i zachętą do przeskakiwania.

```
Co to jest?
└─ Wymaga działania?
   ├─ NIE ─┬─ Kosz                        → Status = Trashed
   │       ├─ Kiedyś-może                 → Status = Someday [+ DeferUntil]
   │       └─ Materiał referencyjny       → konwersja na Note
   └─ TAK ─┬─ Więcej niż jeden krok?      → utwórz Project, to zadanie zostaje
           │                                  pierwszą następną akcją
           ├─ < 2 minuty                  → „zrób teraz", odhaczenie na miejscu
           ├─ Nie moja                    → Status = Waiting, pyta o „kto"
           └─ Moja, później ─┬─ Musi być w konkretnym dniu?
                             │     TAK → Status = Scheduled, pyta o DoDate
                             │     NIE → Status = Next
                             └─ [opcjonalnie: projekt, tagi, priorytet,
                                 czas, energia, termin]
```

Wszystko w nawiasach kwadratowych jest opcjonalne i pomijane jednym klawiszem.
Przetworzenie pozycji ma dać się zrobić trzema naciśnięciami.

---

## 8. Algorytmy

### 8.1 Widok „Teraz"

Wejście: dostępne minuty (przyciski 15 / 30 / 60 / 120), poziom energii (3 przyciski,
domyślnie podpowiadany z pory dnia i historii odhaczeń).

```
kandydaci = zadania gdzie
    Status = Next
    i (DeferUntil jest puste lub ≤ dziś)
    i (EstimatedMinutes jest ustawione i ≤ dostępne minuty)
    i (Energy = Nieznana lub Energy ≤ energia)
    i projekt nadrzędny nie jest wstrzymany

punkty =   (Deadline ≤ dziś+2      ? 100 : 0)   // fakt zewnętrzny bije wszystko
         + (FocusDate = dziś       ?  60 : 0)   // Twoja dzisiejsza decyzja
         + (Deadline ≤ dziś+7      ?  40 : 0)
         + (odblokowuje projekt    ?  25 : 0)    // jedyna akcja w projekcie
         + (wiek w dniach, max     :  20)
         + (Priority = Wysoki      ?  15 : 0)    // etykieta, być może sprzed pół roku
         + (EstimatedMinutes ≤ 15  ?  10 : 0)    // premia za łatwy start

wynik = pierwsze 5, najwyżej jedno zadanie z jednego projektu
```

Reguła „jedno z projektu" jest celowa: pięć kroków tego samego projektu wygląda jak
praca, ale zamyka pole widzenia na resztę życia.

Waga `Priority` jest celowo niska (15) wobec wyboru na dziś (60). Nawet gdyby z czasem
wszystko zrobiło się „wysokie", przesuwa to wynik nieznacznie — **aplikacja słucha
przede wszystkim Twojej decyzji z dzisiaj, a nie etykiety sprzed pół roku.** To dlatego
`Priority` nie potrzebuje limitu (1.6): inflacja przestaje mieć skutki.

Zadania bez `EstimatedMinutes` **nigdy** nie trafiają do „Teraz". Zamiast tego, gdy
kandydatów jest mniej niż trzy, ekran proponuje „oszacuj 5 zadań" — mikrozadanie na
minutę, które samo się rozwiązuje w miarę używania.

### 8.2 Wykrywanie zablokowanych projektów

```sql
SELECT p.Id FROM Projects p
WHERE p.Status = 'Active' AND p.Deleted = 0
  -- brak własnej akcji do przodu
  AND NOT EXISTS (
      SELECT 1 FROM Tasks t
      WHERE t.ProjectId = p.Id AND t.Deleted = 0
        AND t.Status IN ('Next', 'Scheduled', 'Waiting')
  )
  -- i brak żywego podprojektu, który tę akcję ma
  AND NOT EXISTS (
      SELECT 1 FROM Projects c
      WHERE c.ParentProjectId = p.Id AND c.Deleted = 0
        AND c.Status = 'Active'
  )
```

**Drugi warunek jest konieczny od chwili wprowadzenia zagnieżdżania (5.4).** Cel
(„prawo jazdy") zwykle nie ma własnych zadań — ma podprojekty, które je mają.
Bez tego warunku każdy cel byłby zgłaszany jako zablokowany, N1 sypałby fałszywymi
alarmami i przestałabyś na niego patrzeć. Niezmiennik, który krzyczy bez powodu, jest
gorszy od braku niezmiennika, bo uczy ignorowania.

Sprawdzany jest sam fakt istnienia aktywnego podprojektu, nie jego kondycja: jeśli
podprojekt też jest pusty, zgłasza się on sam, na swoim poziomie. Wskazanie ma trafiać
tam, gdzie jest brakująca akcja, a nie w korzeń drzewa.

Przeliczane po każdym zapisie zadania lub projektu i po każdym scaleniu synchronizacji.

Projekt w statusie `Waiting` (jedyna akcja to oczekiwanie na kogoś) **nie jest**
zablokowany — czekanie to prawidłowy stan. Projekt, w którym wszystkie akcje są
`Waiting` dłużej niż 30 dni, jest zgłaszany osobno, jako „utknięty na kimś".

### 8.3 Kreator przeglądu tygodniowego

Siedem kroków, każdy z licznikiem pozycji do rozpatrzenia:

| # | Krok | Źródło |
|---|---|---|
| 1 | Opróżnij skrzynkę | `Status = Inbox` |
| 2 | Zaległe i przeterminowane | N5, `Carry` |
| 3 | Oczekiwane — ponaglić? | N3 |
| 4 | Projekty zablokowane | N1 |
| 5 | Projekty aktywne — nadal aktualne? | `Status = Active`, nietknięte od 14 dni |
| 6 | Kiedyś-może — coś dojrzało? | N6 + przegląd losowych 10 |
| 7 | Kalendarz — dwa tygodnie w przód | wydarzenia + `Deadline` |
| 8 | Równowaga obszarów | N10 + tabela z 8.5 |

Krok zerowy, przed pierwszym: przypięte notatki (wizja, zasady) — sam tekst, bez
żadnej akcji do wykonania. Jest tam po to, żeby reszta przeglądu działa się po jego
przeczytaniu, a nie żeby cokolwiek z nim zrobić.

**Stan zapisywany po każdej pojedynczej pozycji**, nie po kroku. Encja `ReviewSession`:
`StartedAt`, `CompletedAt?`, `CurrentStep`, `ProcessedIds` (JSON).

Przerwanie przez dziecko po czterech minutach to przegląd w 30% ukończony, wznawialny
dokładnie w tym miejscu, także na drugim urządzeniu. Przegląd rozłożony na pięć
wieczorów jest przeglądem zrobionym. Brak ekranu „zacznij od nowa" jest tu celowy
i jest połową wartości tego mechanizmu.

Bez presji terminu: nieukończony przegląd nie wygasa i nie generuje wyrzutu sumienia
w postaci czerwonej plakietki.

### 8.4 Rozwiązywanie powtórzeń

Przy odhaczeniu zadania z `Recurrence`:

```
1. Bieżące → Status = Done, CompletedAt = teraz
2. baza = (Anchor = FromCompletion) ? dziś : DoDate bieżącego
3. następne = pierwsza data > baza zgodna z Kind/Interval/DaysOfWeek/DayOfMonth
4. jeśli Until/Count wyczerpane → koniec, nie twórz
5. utwórz nowe zadanie: kopia pól, nowe Id, DoDate = następne
```

Przy przejściu dnia, dla zadań niewykonanych z `DoDate` w przeszłości:

| `OnMissed` | Działanie |
|---|---|
| `Skip` | `Status = Trashed`, tworzone następne wystąpienie |
| `Carry` | `DoDate` przesuwane na dziś, oznaczenie „zaległe od X", **bez** tworzenia nowego |
| `Accumulate` | bieżące zostaje z pierwotnym `DoDate`, tworzone następne |

Przypadki brzegowe do pokrycia testami: 31. dnia w miesiącu 30-dniowym, 29 lutego,
zmiana czasu, `Weekly` z pustym zbiorem dni, odhaczenie zadania z przyszłości,
odhaczenie dwa razy tego samego dnia, `Carry` przez trzy tygodnie z rzędu.

### 8.5 Równowaga obszarów

```
dla każdego obszaru IsActive:
    projektyAktywne = projekty w drzewie tego obszaru, Status = Active, nieusunięte
    ostatniRuch    = max(UpdatedAt) z projektów i zadań tego obszaru
    cisza          = dni od ostatniRuch
```

Wynik jako tabela, sortowana po `cisza` malejąco:

```
Obszar                aktywne projekty   ostatni ruch
Praca                        7             2 dni temu
Dom                          3             5 dni temu
Twórczość                    1            41 dni temu
Zdrowie                      0            94 dni temu
Relacje                      0        brak ruchu
```

Zasady wyświetlania, wszystkie celowe:

- **Bez ocen.** Żadnego „zaniedbany", żadnej czerwieni, żadnego procentu wypełnienia.
  Sama liczba dni niesie komplet informacji, a etykieta dokłada do niej wstyd, który
  nie pomaga podjąć działania.
- **Bez sugestii wyrównywania.** Równowaga nie znaczy równy rozkład. Trzy miesiące,
  w których wszystko idzie w jeden obszar, bywają prawidłową decyzją — tabela ma
  pokazać, że taka decyzja zapadła, a nie namawiać do jej odwrócenia.
- **Bez wykresu.** Osiem liczb czyta się szybciej niż wykres i nie sugeruje trendu,
  którego przy tej rozdzielczości nie da się odczytać.
- Widoczna **wyłącznie** w kroku 8 przeglądu i na ekranie „Obszary". Nigdy w „Dzisiaj".

### 8.6 Wybór na dziś

Ekran „Dzisiaj" ma **pięć slotów**. Wybór odbywa się rano, spośród zadań `Next`
i `Scheduled` z `DoDate ≤ dziś`.

```
przy próbie ustawienia FocusDate = D na szóstym zadaniu:
    pokaż obecne 5 i zapytaj, które schodzi
    schodzące → FocusDate = null (bez żadnej innej zmiany)
```

Dlaczego limit działa tu bez oporu, a na `Priority` działałby źle:

- dotyczy **jednego dnia**, nie odbiera niczemu ważności na zawsze,
- **zeruje się co dobę** — nie ma czego gromadzić, więc nie powstaje sterta,
- wymuszony wybór wypada przy porannym planowaniu, a nie przy wrzucaniu do skrzynki.

Na koniec dnia niewykonane `FocusDate` **po prostu wygasa**. Bez czerwieni, bez ekranu
podsumowania, bez „wykonano 2 z 5". Zadanie dostaje `FocusMissCount += 1` i wraca do
puli — licznik pracuje po cichu na potrzeby N13.

`FocusDate` to nie to samo co `DoDate`: `DoDate` może mieć dwadzieścia zadań na dziś
i ekran „Dzisiaj" pokaże dwadzieścia. `FocusDate` to te pięć, na które się piszesz.

### 8.7 Przesuwanie zaplanowanych

Zadanie `Scheduled` z `DoDate` w przeszłości, niewykonane, przy przejściu dnia:

```
DoDate = dziś
RollCount += 1
```

Uzasadnienie rozróżnienia: `Deadline` to fakt zewnętrzny, więc jego minięcie **zostaje**
przeterminowaniem (N5) — świat się nie przesunął. `DoDate` to obietnica dana sobie,
więc jej minięcie jest czymś innym.

Zostawienie zaległego na zawsze buduje stertę. Ciche cofnięcie do `Next` gubi informację,
że planowałaś i nie zrobiłaś. Przesunięcie z licznikiem zachowuje jedno i drugie —
a po czwartym razie N15 zadaje pytanie w przeglądzie.

---

## 9. Synchronizacja

### 9.1 Zasada

Każde urządzenie zapisuje **wyłącznie własny plik** w folderze aplikacji na Dysku Google.
Nigdy nie modyfikuje cudzego.

Dzięki temu **konflikt zapisu nie powstaje** — nie jest rozwiązywany sprytnym
algorytmem, tylko nie istnieje. To jedyny powód, dla którego ten projekt nie potrzebuje
serwera.

### 9.2 Układ na Dysku

```
Marshal/
  log/
    {deviceId}.jsonl          dopisywanie na koniec, nigdy nadpisanie
  snapshot/
    {deviceId}-{n}.json       zrzut stanu, gdy log przekroczy 2 MB
  files/
    {sha256}                  załączniki
  device/
    {deviceId}.json           nazwa urządzenia, ostatni kontakt
```

Folder zwykły, nie `appDataFolder` — użytkownik ma go widzieć i móc skopiować.

### 9.3 Format wpisu

```json
{"op":"upsert","e":"task","id":"0199...","hlc":"1757942400123.7.a3f1",
 "f":{"Title":"Zadzwonić do przychodni","Status":"Next","ProjectId":"0199..."}}
```

`f` zawiera **tylko pola zmienione**. Scalanie działa **per pole** — wygrywa wpis
z wyższym HLC dla tego konkretnego pola. Zmiana priorytetu na telefonie i tytułu na
desktopie w trybie offline daje po scaleniu oba, a nie jedno z nich.

### 9.4 Przebieg

```
1. Pobierz listę plików w log/ z Dysku (metadane: rozmiar, modifiedTime)
2. Dla każdego cudzego pliku pobierz zakres bajtów od zapisanego offsetu
3. Zastosuj wpisy: LWW per pole po HLC, remis rozstrzyga deviceId
4. Zapisz nowe offsety w SyncState
5. Dopisz własne niewysłane wpisy na koniec własnego pliku
6. Przelicz niezmienniki z rozdziału 6
```

Wyzwalacze: start aplikacji, powrót z tła, co 5 minut przy aktywnym oknie, ręcznie.
Nigdy w tle przy wyłączonej aplikacji.

### 9.5 Kompakcja

Gdy własny log przekroczy 2 MB: zapisz `snapshot/{deviceId}-{n}.json` ze stanem pełnym,
wyczyść log, zapisz w nim wpis `{"op":"snapshot","n":n}`. Pozostałe urządzenia widzą
wpis, wczytują zrzut i zerują offset.

### 9.6 Czego to nie daje

- Opóźnienie liczone w minutach, nie w sekundach. Przy jednym użytkowniku bez znaczenia.
- Brak natychmiastowego powiadomienia o zmianie na drugim urządzeniu.
- Wymaga konta Google — tego samego, co kalendarz.

### 9.7 Zależność od Google

Jedyna w projekcie. Zabezpieczenie: eksport całej bazy do JSON od etapu 1, offline,
bez konta. Zmiana dostawcy synchronizacji na Dropbox, OneDrive albo katalog sieciowy
to podmiana jednej implementacji `ISyncTransport` — format plików nie zależy od Dysku.

---

## 10. Integracja z kalendarzem

### 10.1 Wersja 1 — tylko odczyt

`Google.Apis.Calendar.v3`, zakres `calendar.readonly`. Przyrostowo przez `syncToken`,
pełne odświeżenie gdy token wygaśnie (410).

Kanały iCal przez `Ical.Net`, pobierane po URL, odświeżane co godzinę.

Konfiguracja: wybór kalendarzy do pokazywania, kolor na kalendarz.

### 10.2 Później — zapis

Zakres `calendar.events` i osobny kalendarz `Marshal`, do którego aplikacja pisze
wyłącznie utworzone przez siebie wydarzenia. **Nigdy nie modyfikuje i nie kasuje
wydarzeń z kalendarzy cudzych ani głównego.**

To ograniczenie sprowadza najgroźniejszą awarię w projekcie — skasowanie prawdziwych
wydarzeń — do awarii w kalendarzu, który można usunąć jednym kliknięciem.

### 10.3 OAuth

Jedno logowanie Google, dwa zakresy (`drive.file`, `calendar.readonly`), jeden ekran
zgody. `drive.file` daje dostęp wyłącznie do plików utworzonych przez aplikację —
nie widzi reszty Dysku.

Token odświeżania w `DPAPI` (Windows) i `EncryptedSharedPreferences` (Android).

---

## 11. Ekrany

| Ekran | Zawartość |
|---|---|
| **Dzisiaj** | **Pięć slotów wyboru (8.6)**, pod nimi wydarzenia dnia, zadania z `DoDate ≤ dziś`, ponaglenia (N3), zablokowane projekty (N1) |
| **Teraz** | 3–5 pozycji po wyborze czasu i energii (8.1) |
| **Skrzynka** | Licznik + przycisk „Przetwórz" → drzewko (rozdz. 7) |
| **Plany** | Oś czasu do przodu: `DoDate`, `Deadline`, wydarzenia |
| **Projekty** | Drzewo: obszar → cel → projekt → zadania. Zablokowane wyróżnione |
| **Obszary** | Dziesięć pozycji, tabela równowagi (8.5), edycja i `QuietDays` |
| **Kiedyś** | `Someday`, przegląd, przywracanie |
| **Oczekiwane** | `Waiting`, sortowane po liczbie dni |
| **Kalendarz** | Godzinowo: dzień / 3 dni / tydzień. Wydarzenia pełne, zadania półprzezroczyste |
| **Notatki** | Markdown, edytor i podgląd, tagi, szukanie |
| **Filtry** | Konstruktor warunków, zapisywanie do Ulubionych |
| **Przegląd** | Kreator siedmiu kroków (8.3) |
| **Archiwum** | `Done` i `Trashed`, szukanie, przywracanie |
| **Ustawienia** | Konto Google, kalendarze, kopia, motyw, strefa |

Wszystko poza „Kalendarzem" i „Notatkami" musi być w pełni obsługiwalne z klawiatury.

### 11.1 Zasady wyświetlania

- Nigdzie nie pokazywać liczby zaległych jako czerwonej plakietki z liczbą większą niż 9.
  „73 zaległe" jest informacją bezużyteczną i kosztem emocjonalnym. Zamiast tego: „zaległe".
- Widoku „Teraz" nie da się rozwinąć do pełnej listy. Pełna lista jest w „Projektach".
- Przy pustej skrzynce ekran przetwarzania nie jest dostępny.
- Tabela równowagi obszarów nie ma wersji „na skróty" w żadnym innym ekranie. Liczba
  „94 dni bez ruchu" obejrzana mimochodem między zadaniami jest kosztem bez pożytku;
  ma sens tylko wtedy, gdy siadasz do przeglądu i możesz coś z nią zrobić.

---

## 12. Kopia zapasowa

Eksport całości do jednego pliku JSON, offline, bez konta — jak w Castellanie.
Import z podmianą całości albo scaleniem po HLC.

Automatyczny zrzut do `snapshot/` na Dysku przy każdej kompakcji jest efektem ubocznym
synchronizacji i pełni rolę kopii historycznej.

---

## 13. Rozstrzygnięcia i pytania otwarte

### 13.1 Rozstrzygnięte

| # | Pytanie | Decyzja | Gdzie |
|---|---|---|---|
| 1 | Domyślny `Anchor` | Wyprowadzany z `Kind`, nie stały | 5.7 |
| 2 | Domyślny `OnMissed` | `Carry` + strażnik N12 | 5.7, N12 |
| 3 | Priorytety a „Teraz" | Rozdzielone: waga bez limitu, wybór 5 na dobę | 1.6, 8.1, 8.6 |
| 4 | Próg ponaglenia | Per obszar, nie globalny | 5.2 |
| 5 | Zadania bez projektu | Dopuszczalne na stałe, bez zgłaszania | 5.3 |
| 6 | `Scheduled` po terminie | Przesuwane na dziś z `RollCount`, N15 po czwartym | 8.7 |
| 7 | Nazwa | **Marshal** | — |
| 8 | Próg ciszy obszaru | Różny per obszar, wartości wstępne | 5.2 |
| 9 | Zadania bez obszaru | Nie istnieją — `AreaId` wymagany wszędzie | 5.3, N11 |

### 13.2 Otwarte

1. **Czy `Accumulate` jest potrzebne w wersji 1?** `Carry` pokrywa niemal wszystko,
   a `Accumulate` to jedyna ścieżka, która potrafi wyprodukować stertę. Do rozważenia
   usunięcie z wersji 1 przy zostawieniu w modelu.
2. **Wartości `QuietDays` i `DefaultNudgeDays`** są zgadnięte. Do korekty po miesiącu
   realnego używania — przed pierwszym kontaktem z życiem nie ma na czym oprzeć decyzji.
3. **Heurystyka podpowiadania energii** w „Teraz". Zaczynamy od pory dnia; wersja oparta
   na historii odhaczeń dopiero wtedy, gdy będzie historia.
4. **Repozytorium** — osobne czy katalog w Castellanie. Blokuje etap 0.

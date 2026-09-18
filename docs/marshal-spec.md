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
| 0 | Szkielet, BD, testy, CI, okno desktopowe i ekran Androida | ✅ Pusta aplikacja startuje na obu platformach (Android dopiero od 18.09, patrz niżej) |
| 1 | Zadania, podzadania, projekty, skrzynka, drzewko przetwarzania, pierwsza migracja, APK tylko `arm64-v8a`, wydanie z APK | Działające GTD na jednym urządzeniu |
| 2 | Dzisiaj, Plany, Kiedyś, Archiwum, **obszary**, zagnieżdżanie projektów, tagi, priorytety, kolory | Pełna nawigacja po sekcjach |
| 3 | **Synchronizacja przez Dysk Google** | Telefon i desktop to jedna aplikacja — punkt bez odwrotu |
| 4 | Powtarzalność, terminy, przypomnienia | Zadania cykliczne przestają wymagać pamięci |
| 5 | Oczekiwane, wykrywanie zablokowanych projektów, równowaga obszarów, kreator przeglądu | Mechanizmy zastępujące dyscyplinę |
| 6 | Widok „Teraz", szacowany czas i energia | Aplikacja wybiera za Ciebie |
| 7 | Kalendarz godzinowy, odczyt z Google Calendar i iCal | Czas i zadania w jednym miejscu |
| 8 | Notatki w Markdown, załączniki | Materiały referencyjne |
| 9 | Filtry łączone, filtry zapisane | ✅ Własne widoki |
| 10 | Widget Androida, tryb ciemny, kopia zapasowa | Domknięcie — poza tym, co wymaga sprzętu |
| Później | Dwustronny zapis do Google Calendar | Osobno, po przeżyciu etapu 7 |

**Etap 0 zamknięty 16.09.2026** — i zamknięty przedwcześnie w połowie androidowej.
Okno na Windowsie otwierało się od tamtego dnia. Aplikacja na Androidzie **nie
startowała ani razu** aż do 18.09.2026: motyw okna dziedziczył po systemowym
`Theme.DeviceDefault`, a okno Avalonii wywodzi się z `AppCompatActivity`, która przy
zakładaniu widoku wymaga motywu z rodziny AppCompat i bez niego rzuca wyjątkiem.
Tak było od pierwszego dnia projektu, w każdej wersji Avalonii 11.

Nie wyszło to przez dwa dni z powodu, który warto zapamiętać: **CI buduje APK, ale
go nie uruchamia.** Pakiet powstawał poprawnie, przebiegi były zielone, a zapis
„zweryfikowany na prawdziwym sprzęcie" w tym miejscu specyfikacji był nieprawdziwy —
i to on kazał wierzyć, że sprawa jest sprawdzona. Zielone budowanie nie jest dowodem
uruchomienia i wpis o weryfikacji wolno postawić dopiero po niej.

Do etapu 1 dołożone dwie rzeczy z budowania, które wyszły przy pierwszej instalacji:

- **APK tylko dla `arm64-v8a`.** Publikacja Release pakuje kod natywny dla czterech
  architektur, przez co pakiet waży 75 MB przy aplikacji pokazującej trzy napisy.
  Telefon używa jednej architektury.
- **Wydanie GitHub Release z APK w załącznikach.** Artefakty przebiegu wygasają po
  90 dniach i wymagają zalogowania, więc nie nadają się na stały sposób instalacji.

**Stan etapu 3 na 16.09.2026.** Mechanika gotowa i sprawdzona testami: zegar logiczny,
dziennik zmian z przechwytywania zapisu, scalanie per pole, porcje, kursory, trwała
tożsamość urządzenia. Sprawdzone na dwóch urządzeniach o osobnych bazach i osobnych
zegarach, przez składnicę na katalogu **i** przez udawany Dysk.

Niesprawdzone jest to, czego bez poświadczeń sprawdzić się nie da — samo wołanie API
Dysku — oraz logowanie na Androidzie, które wymaga osobnych poświadczeń i innej drogi
niż przeglądarka z portem pętli zwrotnej. Instrukcja w `docs/google-dysk.md`.

**Etap 4 zamknięty 16.09.2026** — mechanizm i okno. Powtarzalność, terminy, przejście
dnia i przypomnienia, wszystko z ekranem szczegółu jako wejściem (11.2).

Niesprawdzone zostaje jedno: **odezwanie się przy zamkniętej aplikacji**. Wymaga
powiadomień systemowych i sprzętu (8.4d). Przy otwartym oknie przypomnienia działają.

Przy okazji rozstrzygnięte dwie sprzeczności, które wyszły dopiero przy pisaniu:
`Accumulate` kontra 8.7 (zob. 8.4b) oraz `Skip` nadrabiający po jednym dniu na
uruchomienie. Obie były niewidoczne w samym tekście specyfikacji.

**Etap 5 zamknięty 16.09.2026.** Niezmienniki N1, N3, N4, N5, N6 i N10 działają jako
zapytania, nie jako rzeczy do zauważenia. Ekran „Oczekiwane", tabela równowagi, oznaczanie
projektów zablokowanych w drzewie i kreator przeglądu z wznawianiem po każdej pozycji.

Niezmienniki N12, N13 i N15 mają dane (licznik przesunięć, data pierwszego przegapienia),
ale nie mają jeszcze własnego kroku w przeglądzie — dojdą razem z wyborem na dziś
(`FocusDate`, etap 6), bo N13 bez niego nie ma czego liczyć.

**Etap 6 zamknięty 16.09.2026.** Widok „Teraz" z punktacją z 8.1 i powodem przy każdej
pozycji, wybór pięciu na dziś z wygaszaniem o północy, oszacowanie czasu i energii
w szczególe zadania. N12, N13 i N15 dostały własny krok w przeglądzie — teraz mają
co liczyć.

**Etap 7 zamknięty 16.09.2026** w części, którą da się zamknąć bez konta. Siatka
godzinowa (dzień / 3 dni / tydzień) z zadaniami i wydarzeniami, kopia kalendarzy
z odczytem przyrostowym, kanały iCal. Niesprawdzone zostaje wołanie API Google
i pobieranie kanału po sieci — jedno i drugie wymaga czegoś z zewnątrz.

**Etap 8 zamknięty 16.09.2026** poza jednym: notatki z edytorem i podglądem, gałąź
„materiał referencyjny" w drzewku, przypięte notatki w kroku zerowym przeglądu, oraz
składowanie załączników adresowane treścią, z osobną drogą przenoszenia plików.

Brakuje **wyboru pliku z dysku**. Ta część wymaga systemowego okna wyboru — na Androidzie
i Windowsie wygląda inaczej, potrzebuje uchwytu okna i nie da się jej sprawdzić inaczej
niż na sprzęcie. Cała reszta drogi załącznika działa i ma testy.

**Etap 9 zamknięty 16.09.2026.** Filtry łączone i zapisane widoki w Ulubionych (11.5).
Sprawdzanie warunków jest czystą funkcją w domenie i ma testy w całości; ekran jest
nad nią cienką warstwą.

**Etap 10 zamknięty 16.09.2026 w części, którą da się zamknąć bez sprzętu.** Kopia
zapasowa do jednego pliku JSON z wgrywaniem przez scalanie albo podmianę (12), ekran
ustawień, motyw, jawna strefa liczenia dni (3.4) i widget Androida (4.2).

Przy okazji wyszło, że **spec 3.4 nie była zrealizowana**: strefa miała być jawna od
początku, a dni liczyły się z zegara systemowego. Dwa miejsca dopisane tego samego dnia
wybrały do tego `LocalDateTime`, czyli jeszcze inaczej niż reszta kodu.

Niesprawdzone zostaje wszystko, co wymaga urządzenia: **widget na ekranie domowym**
(CI buduje APK, nie instaluje go) oraz **systemowe okno wyboru pliku** przy zapisie
i wczytaniu kopii — to samo okno, którego brakuje załącznikom z etapu 8. Sama kopia,
czyli to, co się w tym pliku znajdzie i jak się scala, ma testy.

**Ostrzeżenie dotyczące kopii zapasowej.** Kopia, której nigdy nie odtworzono, nie jest
kopią. Pierwsze odtworzenie na prawdziwych danych warto zrobić **zanim** będzie potrzebne
— na drugim urządzeniu albo na świeżej instalacji, nie na tej, która ma dane.

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
żeby wyjazd nie przestawiał terminów. Dzień wykonania i termin są bez strefy — ale to
strefa rozstrzyga, **który dzień jest dzisiaj**, więc z systemowej wyjazd na zachód
przesunąłby „dzisiaj" o dobę i zadania jutrzejsze zrobiłyby się dzisiejszymi.

Dzisiejszy dzień liczy **jedna właściwość zegara** (`IClock.Today`), a nie
`DateOnly.FromDateTime(clock.Now.???)` rozsiane po kilkunastu miejscach. Rozsiane było
groźne nie dlatego, że długie, tylko dlatego, że każde z tych miejsc mogło wybrać inną
z trzech właściwości — `DateTime`, `LocalDateTime`, `UtcDateTime` — a różnią się dokładnie
o tyle, żeby raz na dobę dać inny dzień.

Strefa i motyw są **lokalne, niesynchronizowane**. Zsynchronizowany motyw znaczyłby,
że ciemny włączony wieczorem przy komputerze zapala się rano na telefonie; strefa tym
bardziej — telefon jedzie z Tobą, komputer zostaje.

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

Zakres widgetu: **plan dzisiejszego dnia**, przycisk odhaczenia, przycisk szybkiego
wrzutu. Nic więcej — każda funkcja w widgecie jest utrzymywana podwójnie.

Plan to zadania z dzisiejszym dniem wykonania i zaległe — ten sam zbiór, co ekran
„Dzisiaj" (1.5) — **plus** wzięte na dziś (8.6). Kolejność chronologiczna: najpierw to,
co ma godzinę, potem reszta. Wiersz ma dwie linijki, bo sama nazwa nie odpowiada na
pytanie, po które się na plan patrzy — „czy mam teraz coś umówionego".

**Bez odhaczonych.** Plan odpowiada na pytanie „co jeszcze przede mną", a nie „co dziś
było". Odhaczone na liście, po którą sięga się w biegu, zajmuje miejsce rzeczy, która
czeka. Na siatce kalendarza zostają, bo tam pytanie brzmi inaczej: co się z dniem stało.

**Lista przewijana**, nie wiersze wpisane na sztywno. Stała liczba wierszy wystarczała,
dopóki widget pokazywał wybór na dziś — tego jest najwyżej pięć (N14). Plan całego dnia
nie ma takiego limitu, a wiersz, którego nie widać, jest w nim tym samym co wiersz,
którego nie ma. Kosztem jest osobna usługa i osobna fabryka widoków: wiersze listy
rozwija system w procesie ekranu domowego, nie nasz. Stąd też inny sposób na kliknięcie
— wzorzec zamiaru na liście i uzupełnienie przy wierszu — i stąd wymóg, żeby ten wzorzec
był **zmienny**, jako jedyny zamiar w tej aplikacji.

Treść planu liczy osobna usługa warstwy współdzielonej, nie kod widgetu. Widgetu nie da
się uruchomić w teście, więc wszystko, co da się z niego wyjąć, ma być wyjęte — inaczej
jedyną drogą sprawdzenia „czy plan ma właściwą treść i kolejność" jest patrzenie na
telefon.

Trzy poprawki wobec pierwotnego zapisu:

- **Plan dnia, nie sama piątka na dziś.** Widget pokazujący wyłącznie wzięte na dziś
  pomija rzeczy umówione z kimś na godzinę — daje więc obraz dnia bez spotkania
  o szesnastej. To jest gorsze od braku widgetu, bo wygląda na pełną odpowiedź.
- **Wybór na dziś, nie widok „Teraz".** Lista „Teraz" powstaje z punktacji zależnej od
  zadeklarowanego czasu i energii (8.1), a widget nie ma jak o nie zapytać i musiałby
  je zgadywać. Wybór na dziś jest już wybrany i odpowiada na to samo pytanie bez
  zgadywania czegokolwiek.
- **Te same usługi co aplikacja, nie osobny odczyt tylko do czytania.** Dla samego
  czytania osobny minimalny `DbContext` byłby prostszy. Rzecz jest w przycisku
  odhaczenia: zapis z pominięciem dziennika zmian i zegara logicznego zmieniłby wiersz
  lokalnie i **nigdy nie dotarłby na drugie urządzenie** — bez błędu i bez śladu.
  Drugie, uproszczone wejście do danych jest dokładnie tym rodzajem skrótu, który
  rozjeżdża bazy po cichu.

Stąd zależności składane **raz na proces**: widget wchodzi tą samą drogą co okno i bywa
pierwszy, a dwa złożenia dałyby dwa konteksty nad jednym plikiem.

Pięć wierszy wpisanych wprost w układ, bez `ListView` i `RemoteViewsService`: przy stałym
limicie pięciu pozycji (N14) cała ta machina obsługiwałaby liczbę, która się nie zmienia.
Wrzut otwiera aplikację, bo `RemoteViews` nie zna pola do wpisywania, a wszystko inne
znaczyłoby drugi ekran do utrzymywania.

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

### 5.6a Note (agregat)

Materiał referencyjny: rzecz, której **nie trzeba robić**, tylko mieć pod ręką. Powstaje
najczęściej z drzewka przetwarzania (rozdz. 7).

| Pole | Typ | Uwagi |
|---|---|---|
| `Title` | `string` | |
| `Content` | `string` | Markdown |
| `AreaId` | `Guid?` | **opcjonalny**, inaczej niż przy zadaniu |
| `FromTaskId` | `Guid?` | z czego powstała przy przetwarzaniu |
| `IsPinned` | `bool` | wchodzi do kroku zerowego przeglądu |

Obszar jest **opcjonalny**, i to jest różnica wobec zadania (N11). Notatka bywa ogólna —
„numer do przychodni" nie należy do żadnego obszaru odpowiedzialności bardziej niż do
innego, a wymuszony wybór byłby zmyśleniem. Przy zadaniu obszar jest wymagany, bo zadanie
bez obszaru psuje tabelę równowagi; notatka w żadnej liczbie nie występuje.

**Notatka przypięta to czwarty i piąty horyzont GTD** (1.8): wizja i sens, czyli rzeczy,
których nie da się odhaczyć. Dlatego w kroku zerowym przeglądu nie mają przy sobie
żadnego przycisku — są tam po to, żeby reszta działa się po ich przeczytaniu.

Konwersja z zadania wysyła je do kosza, a nie kasuje: kosz jest nagrobkiem, więc przy
scalaniu widać, że pozycja została rozstrzygnięta, a nie że przepadła.

### 5.6b Attachment (agregat)

| Pole | Typ | Uwagi |
|---|---|---|
| `Sha256` | `string` | skrót treści — **to jest adres pliku** |
| `FileName` | `string` | nazwa do pokazania, nie do adresowania |
| `Size` | `long` | |
| `TaskId` / `NoteId` | `Guid?` | przynajmniej jedno wymagane |

**Adresowany treścią.** Dwa takie same zdjęcia wgrane przy dwóch zadaniach zajmują jedno
miejsce, a plik raz zapisany nigdy się nie zmienia — ta sama własność, na której stoją
porcje dziennika (9.2), tylko tu wynika wprost z adresowania, a nie z umowy.

**Wpis wędruje dziennikiem, treść osobną drogą.** Dziennik jest tekstem i ma dać się
przeczytać notatnikiem; wrzucenie w niego zdjęcia rozsadziłoby format co do zasady.
Skutek: na drugim urządzeniu wpis potrafi być **przed** plikiem, i to jest stan normalny.
Załącznik bez treści pokazuje się jako „jeszcze się nie ściągnął", a nie znika i nie
wyrzuca błędu.

Usunięcie zostawia treść w składnicy. Dwa wpisy mogą wskazywać ten sam plik, a ustalanie,
który był ostatni, wymagałoby przejścia po całej bazie przy każdym usunięciu — za to samo
płaci się kilkoma kilobajtami.

### 5.7 Recurrence (typ własny, nie RRULE)

| Pole | Wartości |
|---|---|
| `Kind` | `Daily`, `EveryNDays`, `Weekly`, `Monthly`, `Yearly` |
| `Interval` | `int` |
| `DaysOfWeek` | zbiór dni (dla `Weekly`) |
| `DayOfMonth` | `int?` w zakresie 1–31, zawsze przycinane do długości miesiąca |
| `Anchor` | `FromScheduled` \| `FromCompletion` — domyślny **wyprowadzany z `Kind`** |
| `OnMissed` | `Skip` \| `Carry` \| `Accumulate` |
| `Until` | `DateOnly?` |
| `Count` | `int?` |

RRULE z iCal jest odrzucony celowo: obsługuje przypadki, których nigdy nie użyjesz,
a nie ma pojęcia `Anchor` ani `OnMissed` — czyli dokładnie tego, co jest tu istotne.
Import RRULE z Google Calendar odbywa się do tego typu, stratnie, z oznaczeniem.

**Osobnego pola „ostatni dzień miesiąca" nie ma.** Dzień miesiąca jest zawsze przycinany
do długości miesiąca, więc 31 w lutym daje 28 albo 29, a w kwietniu 30 — czyli
„ostatniego" to po prostu 31. Przycięcie nie zjada dnia na stałe: po lutym rytm wraca
do 31-go, bo bazą jest reguła, nie ostatnia data.

**Reguła siedzi w bazie jako jeden tekst JSON, nie jako osiem kolumn.** Osiem kolumn
dałoby osiem osobnych pól w dzienniku zmian, a scalanie per pole (9.4) potrafiłoby
złożyć rytm z połówek dwóch różnych decyzji: dni tygodnia z telefonu i odstęp
z komputera. Reguła jest **jedną decyzją** i wygrywa albo przegrywa w całości.

**Postać zapisu jest oddzielona od typu domenowego.** Reguły leżą w bazie i w dzienniku,
więc typ domenowy musi dać się zmieniać bez unieważniania tego, co już zapisane. Odczyt
przechodzi przez konstruktor, czyli sprawdzenia obowiązują także wartości, które przyszły
z pliku — nie tylko te wpisane w aplikacji. Reguła nie do odczytania jest pomijana,
a nie wywraca scalania (9.4).

**Wszystko liczone na dacie, nie na chwili.** To nie jest uproszczenie, tylko
rozstrzygnięcie: zmiana czasu nie może przesunąć dnia. Rytm liczony na chwilach
potrafiłby po przejściu na czas zimowy wypaść w niedzielę o 23:00; przy dacie ten
przypadek brzegowy nie istnieje.

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

**Każda pozycja pokazuje powód, dla którego wypłynęła** („termin za chwilę",
„odblokowuje projekt", „czeka 100 dni"). Powód jest częścią odpowiedzi, nie ozdobnikiem:
lista bez uzasadnienia każe wierzyć na słowo, a wtedy pierwszy chybiony wybór odbiera
ekranowi zaufanie w całości.

**Przycięcie wieku dotyczy punktów, nie faktu.** Rok i miesiąc dostają tyle samo punktów,
bo bez tego jedno zadanie sprzed roku zdominowałoby ekran na zawsze — ale powód podaje
wiek prawdziwy. „Czeka 20 dni" przy zadaniu sprzed stu dni byłoby zwyczajną nieprawdą.

`Energy.Nieznana` **nie jest tym samym co `Niska`**. Zadanie nieoszacowane przechodzi
przy każdym poziomie energii, bo nie ma podstaw, żeby je odsiać; zadanie oznaczone jako
lekkie zostało tak oznaczone świadomie. Sklejenie tych wartości ukryłoby brak decyzji
pod pozorem decyzji.

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

Dziewięć kroków (zerowy i dwa bez odhaczania), każdy z licznikiem pozycji:

| # | Krok | Źródło |
|---|---|---|
| 1 | Opróżnij skrzynkę | `Status = Inbox` |
| 2 | Zaległe i przeterminowane | N5, `Carry` |
| 3 | Oczekiwane — ponaglić? | N3 |
| 4 | Projekty zablokowane | N1 |
| 5 | Projekty aktywne — nadal aktualne? | `Status = Active`, nietknięte od 14 dni |
| 6 | Kiedyś-może — coś dojrzało? | N6 + przegląd losowych 10 |
| 7 | Kalendarz — dwa tygodnie w przód | wydarzenia + `Deadline` |
| 8 | Liczniki, które o coś pytają | N12, N13, N15 |
| 9 | Równowaga obszarów | N10 + tabela z 8.5 |

Krok zerowy, przed pierwszym: przypięte notatki (wizja, zasady) — sam tekst, bez
żadnej akcji do wykonania. Jest tam po to, żeby reszta przeglądu działa się po jego
przeczytaniu, a nie żeby cokolwiek z nim zrobić.

**Stan zapisywany po każdej pojedynczej pozycji**, nie po kroku. Encja `ReviewSession`:
`StartedAt`, `CompletedAt?`, `CurrentStep`, `ProcessedIds` (JSON).

**Wznowienie jest domyślne i nie pyta.** Ekran „masz niedokończony przegląd, chcesz
wrócić?" byłby pytaniem, na które jest tylko jedna sensowna odpowiedź, a przy okazji
dawałby okazję do zaczęcia od nowa — czyli do tego, przed czym cały ten mechanizm chroni.

**Krok 1 nie odhacza pozycji, tylko prowadzi do drzewka przetwarzania** (rozdz. 7).
Oznaczenie wrzutu jako „rozpatrzony" bez podjęcia decyzji zostawiłoby go w skrzynce,
a za tydzień trzeba by go oznaczyć znowu: bieżnia zamiast opróżniania. Licznik tego
kroku schodzi sam, w miarę jak skrzynka pustoszeje.

**Kroki 0 i 8 nie mają czego odhaczać** — pierwszy jest tekstem do przeczytania, drugi
tabelą do obejrzenia. Nie każdy krok przeglądu kończy się czynnością.

Zbiór rozpatrzonych pozycji jest jednym polem z tekstem JSON i scala się jak każde inne
pole: nowszy zapis wygrywa w całości. Dwa urządzenia prowadzące ten sam przegląd
**równocześnie** zgubiłyby część oznaczeń. Świadomie przyjęte — przegląd robi się
w jednym miejscu naraz, a scalanie zbiorów przez sumę wymagałoby własnej reguły scalania
dla jednego pola w całym modelu.

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

Odhaczone wystąpienie **zostaje odhaczone**, a następne jest nowym zadaniem z nowym
identyfikatorem. Nie przestawiamy daty w tym samym zadaniu: historia „robiłam to w każdy
poniedziałek prócz jednego" jest całą wartością powtarzalności, a zadanie wędrujące
w przyszłość jej nie niesie.

Termin i chwila przypomnienia przenoszą się na nowe wystąpienie **z zachowaniem odstępu**
od daty wykonania. „Zapłacić do 10-go" przy racie robionej 5-go to pięć dni zapasu, co
miesiąc tyle samo; skopiowane wprost byłyby od razu przeterminowane i N5 zacząłby kłamać.

### 8.4a Regułę nosi najnowsze wystąpienie

Przy tworzeniu kolejnego wystąpienia reguła **przechodzi na nie i znika z poprzedniego**.

To jedyna rzecz, która czyni przetwarzanie dnia powtarzalnym: zadanie bez reguły nie umie
zrodzić następnika, więc przejście dnia puszczone dwa razy nie zrobi dwóch kopii. Bez tego
`Accumulate` dokładałby po jednej pozycji na każde uruchomienie aplikacji — a aplikacja
startuje wiele razy dziennie i na dwóch urządzeniach, które się ze sobą nie umawiają.

### 8.4b Przejście dnia

Dla zadań niewykonanych z `DoDate` w przeszłości:

| `OnMissed` | Działanie |
|---|---|
| `Skip` | bieżące do kosza; tworzone **jedno** wystąpienie na dziś albo później |
| `Carry` | `DoDate` przesuwane na dziś, `CarriedSince` ustawiane raz, **bez** tworzenia nowego |
| `Accumulate` | bieżące zostaje jako zaległość bez dnia; tworzone następne |

**`Skip` nadrabia jednym krokiem.** Tydzień bez otwierania aplikacji ma dać jedno
wystąpienie, a nie siedem utworzonych i wyrzuconych po kolei. Nagrobek każdego
przeskoczonego dnia nie jest niczyją informacją, a rozjechałby się po wszystkich
urządzeniach. Przeskoczone wystąpienia **zużywają licznik serii**: „pięć razy" przespane
przez pięć dni jest serią skończoną, a nie serią czekającą na kolejne pięć okazji.

**`Carry` pamięta pierwszy przegapiony dzień.** `CarriedSince` ustawiane jest raz, przy
pierwszym przeniesieniu — „zaległe od 14 września", nie „od wczoraj". Bez tego N12 nigdy
nie doliczyłby trzydziestu dni i strażnik rytmu nie odezwałby się nigdy.

**`Accumulate` zostawia zaległość, nie datę.** To rozstrzygnięcie sprzeczności między tą
tabelą a 8.7: gdyby pominięte wystąpienie zostało zaplanowane na swoją dawną datę, 8.7
przesunęłoby je nazajutrz na dziś, potem znowu, i po czterech dniach każde odpaliłoby N15.
Mechanizm zjadałby sam siebie. Zaległe wystąpienie zostaje więc **następną akcją bez
wyznaczonego dnia** — bo tym właśnie jest — a dzień, na który było umówione, zostaje
w `CarriedSince` jako „zaległe od".

`Accumulate` domyka całą zaległość w jednym przebiegu, z **ogranicznikiem 120 wystąpień**.
Rok bez otwarcia aplikacji przy powtarzaniu codziennym dałby trzysta pozycji naraz, czyli
dokładnie to, przed czym ma chronić zasada 1.2. Reszta dochodzi przy kolejnym
uruchomieniu; nic nie ginie, tylko schodzi partiami.

### 8.4c Kiedy to się dzieje

Przy starcie aplikacji, przy powrocie z tła i po każdym scaleniu synchronizacji.

**Nie ma wyzwalacza o północy i nie będzie.** Aplikacja nie chodzi w tle, a „przejście
dnia" to nie zdarzenie w czasie, tylko zastana różnica między datą zapisaną a dzisiejszą.
Wołanie jest powtarzalne bez skutków ubocznych i to jest warunek, nie wygoda: dwa
urządzenia robią to samo, niezależnie, bez umawiania się, które ma.

Przypadki brzegowe pokryte testami: 31. dnia w miesiącu 30-dniowym, 29 lutego, zmiana
czasu, `Weekly` z pustym zbiorem dni, odhaczenie zadania z przyszłości, odhaczenie dwa
razy tego samego dnia, `Carry` przez trzy tygodnie z rzędu, `Skip` i `Accumulate` po
tygodniu nieobecności, przejście dnia puszczone dwa razy.

### 8.4d Przypomnienia

`ReminderAt` jest **chwilą**, nie dniem — o to właśnie chodzi w przypomnieniu — i jest
polem osobnym od `DoDate` i od `Deadline`, bo znaczy co innego niż oba: „kiedy chcę o tym
usłyszeć". Zadanie na wtorek może chcieć przypomnienia w poniedziałek wieczorem,
a zadanie z terminem za miesiąc — na tydzień przed.

**Przypomnienie z przeszłości też się odzywa.** Aplikacja nie chodzi w tle, więc chwila
przypomnienia prawie nigdy nie zastaje jej otwartej; odzywanie się wyłącznie „co do
minuty" znaczyłoby, że przypomnienia nie działają w ogóle.

**Co się synchronizuje, a co nie.** Chwila przypomnienia jest decyzją i wędruje między
urządzeniami. „Czy to urządzenie już pokazało" jest faktem o tym urządzeniu i zostaje
przy nim, w tabeli poza dziennikiem zmian.

Cena tego wyboru: przypomnienie potrafi odezwać się i na telefonie, i na komputerze.
Cena wyboru odwrotnego: telefon odgrywa je w torbie, zapisuje „pokazane", i nie
dowiadujesz się nigdy. Dwa razy usłyszeć jest gorzej niż raz, ale nieporównanie lepiej
niż nie usłyszeć wcale.

Zapis pokazania trzyma **chwilę, na którą było ustawione**, a nie samo „było". Bez tego
„przypomnij mi jednak o godzinę później" milczałoby.

**Czego nie ma:** odezwania się przy zamkniętej aplikacji. Wymaga powiadomień systemowych
— na Androidzie kanału i uprawnienia, na Windowsie zarejestrowanego skrótu w menu Start —
czyli jedynego kawałka, który wygląda inaczej na każdej platformie i którego nie da się
sprawdzić testem. Wydzielony interfejsem; wybieranie, co i kiedy pokazać, leży po stronie
sprawdzonej testami.

### 8.5 Równowaga obszarów

```
dla każdego obszaru IsActive:
    projektyAktywne = projekty w drzewie tego obszaru, Status = Active, nieusunięte
    ostatniRuch    = max(UpdatedAt) z projektów i zadań tego obszaru
    cisza          = dni od ostatniRuch
```

„Dni od" liczone są ze **ściennego członu zegara logicznego** i w czasie uniwersalnym.
Chwila pochodzi z urządzenia, które zmianę zrobiło, i jego strefy nie znamy; przy mierze
„ile dni bez ruchu" kilka godzin różnicy nie ma znaczenia, a udawanie dokładności,
której nie ma, miałoby.

**Brak ruchu to osobna wartość, nie zero dni.** Obszar, w którym nigdy nic się nie
zapisało, i obszar ruszony dzisiaj to dwie różne rzeczy, a pokazanie obu jako „0"
byłoby kłamstwem w najważniejszym miejscu tej tabeli.

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

**Kandydaci to `Next` oraz `Scheduled` z `DoDate ≤ dziś`** — i lista kandydatów musi być
na ekranie osobno, obok listy dzisiejszej. Większość następnych akcji nie ma dnia
wykonania, więc bez własnego miejsca nie dałoby się ich w ogóle wybrać.

**Zdjęcie z wyboru nie podbija licznika.** Zadanie zdjęte rano, żeby zrobić miejsce
innemu, nie jest zadaniem, którego nie zrobiłaś. Licznik podbija wyłącznie koniec dnia.

**Odhaczenie zdejmuje z wyboru.** Bez tego wygaszanie na koniec dnia policzyłoby zadania
wykonane jako pominięte i N13 zacząłby kłamać.

N14 jest limitem **ekranu, nie bazy**. Scalanie z drugiego urządzenia potrafi przynieść
szósty wybór i nie ma jak temu zapobiec bez ograniczeń między agregatami, których ten
model nie ma (9.1). Skutek jest nieszkodliwy: jednego dnia widać sześć pozycji zamiast
pięciu, a nazajutrz wszystko wygasa.

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

Każde urządzenie zapisuje **wyłącznie własne porcje** w folderze aplikacji na Dysku
Google. Nigdy nie modyfikuje cudzych ani własnych wcześniejszych.

Dzięki temu **konflikt zapisu nie powstaje** — nie jest rozwiązywany sprytnym
algorytmem, tylko nie istnieje. To jedyny powód, dla którego ten projekt nie potrzebuje
serwera.

### 9.2 Układ na Dysku

Dziennik urządzenia jest **ciągiem niezmiennych porcji**, nie jednym rosnącym plikiem.

```
Marshal/
  {deviceId}.{porcja}.jsonl   porcja raz zapisana nigdy się nie zmienia
  files/{sha256}              załączniki
```

Nazwa porcji to liczba uzupełniona zerami (`000001`), żeby porządek leksykograficzny
pokrywał się z chronologicznym — składnice sortują nazwy jako tekst, więc `10`
wypadłoby przed `9`. Ta sama sztuczka co przy zegarze logicznym (3.5).

**Dlaczego nie jeden plik z dopisywaniem.** Pierwsza wersja tego rozdziału zakładała
jeden plik na urządzenie i przesunięcie w bajtach jako kursor. Odpadła przy
implementacji: **Dysk Google nie ma operacji dopisania.** Da się wgrać nową wersję
całego pliku albo utworzyć nowy — nie ma czegoś takiego jak „dołóż te 200 bajtów na
koniec". Pobieranie całości i wgrywanie z powrotem przy każdej synchronizacji
odbierałoby tę jedną własność, na której wszystko stoi: przerwane wgranie niszczyłoby
własny dziennik, zamiast tylko nie dołożyć ostatniej porcji.

**Dlaczego płasko, bez katalogów na urządzenie.** Nazwy na Dysku nie są unikalne. Dwa
urządzenia zakładające jednocześnie katalog `telefon` dostaną dwa różne katalogi o tej
samej nazwie i każde będzie pisać do swojego. Identyfikator urządzenia siedzi więc
w nazwie pliku. Składnica na katalogu lokalnym używa `log/{deviceId}/{porcja}.jsonl`,
bo tam nazwy są unikalne z definicji.

Folder zwykły, nie `appDataFolder` — użytkownik ma go widzieć i móc skopiować.

### 9.3 Uprawnienie

`https://www.googleapis.com/auth/drive.file` — dostęp **wyłącznie do plików założonych
przez aplikację**. Jedyne uprawnienie Dysku, które nie wymaga przeglądu Google, więc
aplikacja nie wpada w limit stu użytkowników testowych. Wystarcza, bo dziennik zakłada
sama aplikacja.

„Aplikacja" to identyfikator klienta OAuth, a nie instalacja: drugie urządzenie z tym
samym identyfikatorem i tym samym kontem widzi porcje pierwszego. Reszta Dysku —
zdjęcia, dokumenty — jest poza zasięgiem aplikacji.

### 9.4 Format wpisu

```json
{"e":"Tasks","id":"0199...","hlc":"1757942400123.000007.a3f1",
 "f":{"Title":"Zadzwonić do przychodni","State":"Next","ProjectId":"0199..."}}
```

`f` zawiera **tylko pola zmienione jednym zapisem**. Grupowanie po zapisie, a nie po
polu, bo jeden zapis to jedna decyzja użytkownika.

Scalanie działa **per pole** — wygrywa wpis z wyższym znacznikiem dla tego konkretnego
pola. Zmiana priorytetu na telefonie i tytułu na komputerze w trybie offline daje po
scaleniu oba, a nie jedno z nich. Do tego potrzebna jest tabela `FieldStamp` ze
znacznikiem **bieżącej wartości każdego pola**; znacznik encji by nie wystarczył.

Uszkodzony wiersz jest pomijany, a nie przerywa scalania: może pochodzić z nowszej
wersji aplikacji, a reszta porcji jest zrozumiała.

### 9.5 Przebieg

```
1. Wyślij: zgrupuj niewysłane zmiany, zapisz jako kolejną własną porcję
2. Pobierz listę porcji wszystkich urządzeń
3. Dla każdego cudzego urządzenia weź porcje o nazwie większej od kursora
4. Zastosuj wpisy: nowszy znacznik wygrywa, per pole; remis rozstrzyga deviceId
5. Przesuń kursor po każdej przeczytanej porcji
6. Zapisz podniesiony zegar logiczny
```

Kursor jest **przyspieszeniem, nie warunkiem poprawności**. Ponowne zastosowanie tego
samego wpisu nic nie zmienia, bo jego znacznik nie jest już nowszy od zapisanego.
Gdyby kursor przepadł, synchronizacja przeczyta wszystko od początku i dojdzie do tego
samego stanu — tylko raz wolniej.

Scalanie działa w zasięgu, w którym dziennik zmian milczy. Bez tego zastosowanie
zdalnej zmiany zapisałoby ją jako zmianę lokalną, wysyłka odesłałaby ją z powrotem
i dwa urządzenia odbijałyby sobie te same wpisy bez końca — przy czym każdy obieg
z osobna wyglądałby na poprawny.

Wyzwalacze: start aplikacji, powrót z tła, **zapis z okna**, co 5 minut przy aktywnym
oknie, ręcznie. Na Androidzie dodatkowo praca okresowa co pół godziny — tam wyłączona
aplikacja nie znaczy wyłączonego urządzenia.

Praca okresowa na Androidzie idzie przez **WorkManager**, nie przez budzik. Budzik jest
narzędziem do „zrób coś o tej godzinie"; okresowość zrobiona z budzików to trzy osobne
rzeczy do dopilnowania i wszystkie trzy zawiodły po kolei: budzik niebudzący nie wybudza
telefonu, powtarzalny jest w uśpieniu odkładany bez ograniczenia, a łańcuch jednorazowych
urywa się, gdy system ubije odbiornik w połowie pracy. Objaw za każdym razem ten sam
i w żadnym razie nie wskazujący przyczyny: cisza.

WorkManager przeżywa uśpienie i restart, sam ponawia nieudany przebieg, sam pilnuje
warunku sieci — a przede wszystkim daje na pracę **dziesięć minut** zamiast dziesięciu
sekund, które dostaje odbiornik obudzony budzikiem. Pełny przebieg do Dysku bywa dłuższy
od dziesięciu sekund, więc ta granica i tak by go dobiła.

Zlecenie zgłaszane jest z zasadą **„zostaw, jeśli już jest"**. Zastąpienie przestawiałoby
odliczanie przy każdym otwarciu aplikacji, a przy zaglądaniu do niej co kwadrans przebieg
w tle nie ruszyłby ani razu.

Przypomnienia zostają przy budziku i to nie jest niekonsekwencja: tam pytanie brzmi
„odezwij się o szesnastej", a nie „zajrzyj kiedyś w ciągu kwadransa". Budzik przypomnień
jest dokładny, budzący i dopuszczony w uśpieniu, a przestawia się po każdym przebiegu
synchronizacji — to, co przyszło z Dysku, potrafi zmienić najbliższą godzinę.

Droga, której **nie** mamy: powiadomienie z serwera. Tak robi to każda aplikacja
z własnym kontem i jest to jedyny sposób, żeby telefon dowiedział się o zmianie od razu.
Dysk Google umie wysyłać powiadomienia o zmianach wyłącznie na publiczny adres HTTPS,
czyli na serwer — a brak serwera jest w tym projekcie rozstrzygnięciem (9.1), nie brakiem.
Cena tego rozstrzygnięcia to właśnie zaglądanie co pół godziny.

Zapis i przerwa to dwa różne powody, bo synchronizacja ma dwie strony. Zapis jest
powodem do **wysłania**: jest to jedyna chwila, w której wiadomo, że jest co wysyłać.
Przerwa jest powodem do **odczytu**: to, co dopisało drugie urządzenie, nie zapowiada
się po tej stronie niczym, więc trzeba po prostu zajrzeć.

Zapis zgłasza się znakiem podnoszonym przez jednostkę pracy — tamtędy przechodzi każdy
zapis z okna, więc jedno miejsce wystarcza. Znak przychodzi z wątku, na którym skończyła
się baza, i dlatego nie robi nic poza podniesieniem się; sam przebieg rusza z minutnika
okna. Zapisy samej synchronizacji znaku nie podnoszą — inaczej każdy przyjęty odcinek
prosiłby o kolejny przebieg i pętla nie miałaby końca.

### 9.5.1 Brama na bazę

Kontekst bazy jest pojedynczy na cały proces i nie jest bezpieczny dla dwóch rzeczy
naraz. Dopóki wszystko działo się z okna, wystarczało to samo z siebie: okno ma jeden
wątek. Odkąd synchronizacja rusza sama, druga strona pojawiła się naprawdę — pierwszym
objawem był błąd o dwóch instancjach tego samego znacznika pola, wyskakujący przy
zapisie zadania, który z zadaniem nie miał nic wspólnego.

Dlatego każde dotknięcie bazy przez synchronizację, jednostkę pracy i składnicę
kalendarza przechodzi przez jedną bramę przepuszczającą jedną pracę naraz. Brama jest
trzymana przez **całość** czynności, łącznie z jej oczekiwaniami: puszczona na czas
oczekiwania wpuszczałaby drugą pracę dokładnie w tę szczelinę, którą ma zamykać.

Z tego wynika kolejność w scalaniu: najpierw ściągnąć z sieci **wszystkie** potrzebne
porcje, dopiero potem nałożyć je jednym blokiem za bramą. Nakładanie przeplatane
pobieraniem trzymałoby bramę tak długo, jak długo trwa sieć — czyli zatrzymywałoby okno
na cały przebieg.

Brama nie obejmuje odczytów, które repozytoria robią wprost na kontekście. Zapisy są
ujęte w całości, a odczyt trwa milisekundy i nie zmienia stanu śledzenia, więc zderzenie
jest możliwe, ale rzadkie. Domknięcie tego znaczy przeprowadzenie każdego zapytania przez
tę samą bramę — praca do zrobienia wtedy, gdy okaże się potrzebna, a nie na zapas.

Przebiegi automatyczne, które się udały i nic nie przeniosły, **nie trafiają do
dziennika**: przebieg co pięć minut to blisko trzysta wpisów na dobę, a dziennik trzyma
pięćset. Awarie i przebiegi, które coś przeniosły, zostają zawsze.

### 9.6 Tożsamość urządzenia i ciągłość zegara

Identyfikator urządzenia **nie może brać się z nazwy maszyny**. Na Androidzie
`MachineName` zwraca `localhost` na każdym urządzeniu, a dwa urządzenia o jednym
identyfikatorze psują trzy rzeczy naraz: rozstrzyganie remisów zegara przestaje być
jednoznaczne, nazwy porcji wchodzą sobie w drogę, a każde urządzenie uznaje dziennik
drugiego za własny i przestaje go czytać. Żadna z tych rzeczy nie rzuca wyjątku.

Identyfikator nadawany jest raz, z prawdziwego źródła losowości, i leży w bazie
lokalnej. Człon czytelny z nazwy maszyny jest wyłącznie po to, żeby w składnicy dało
się poznać, z czego jest który plik.

Ostatni wydany znacznik zegara logicznego **musi przetrwać zamknięcie aplikacji**.
Inaczej zegar startuje od zera i opiera się wyłącznie na zegarze ściennym; wystarczy,
że ten cofnie się między uruchomieniami — poprawka z serwera czasu, zmiana strefy,
rozładowana bateria podtrzymania — a nowe zmiany dostają znaczniki wcześniejsze od
już wysłanych i przepadają przy scalaniu, bez śladu.

### 9.7 Kompakcja

Dziennik rośnie w nieskończoność. Docelowo: gdy porcji uzbiera się dużo, zapisz jedną
porcję-migawkę ze stanem pełnym i oznacz wcześniejsze jako zbędne. Pozostałe urządzenia
widzą wpis, wczytują migawkę i przestawiają kursor.

**Nie zrobione.** Przy tempie kilkuset zmian dziennie to problem na rok, nie na teraz.

### 9.8 Czego to nie daje

- Opóźnienie liczone w minutach, nie w sekundach. Przy jednym użytkowniku bez znaczenia.
- Brak natychmiastowego powiadomienia o zmianie na drugim urządzeniu.
- Wymaga konta Google — tego samego, co kalendarz.

### 9.9 Zależność od Google

Jedyna w projekcie. Zabezpieczenie: eksport całej bazy do JSON od etapu 1, offline,
bez konta. Zmiana dostawcy synchronizacji na Dropbox, OneDrive albo katalog sieciowy
to podmiana jednej implementacji `ISyncTransport` — format plików nie zależy od Dysku.
Składnica na katalogu lokalnym nie jest atrapą na czas testów: to działająca droga
przez katalog Dropboksa, OneDrive albo Syncthinga, i to na niej sprawdzane jest
scalanie.

---

## 10. Integracja z kalendarzem

### 10.1 Wersja 1 — tylko odczyt

`Google.Apis.Calendar.v3`, zakres `calendar.readonly`. Przyrostowo przez `syncToken`,
pełne odświeżenie gdy token wygaśnie (410).

Kanały iCal przez `Ical.Net` (**4.x**, nie 5.x — piąta wersja przepisała API dat,
a przy braku lokalnego kompilatora każde nietrafione założenie o jej kształcie kosztuje
pełny przebieg CI). Pobierane po adresie, odświeżane co godzinę, zawsze w całości: plik
iCal nie ma pojęcia odczytu przyrostowego i nie ma czego optymalizować.

Konfiguracja: wybór kalendarzy do pokazywania, kolor na kalendarz.

**Co się synchronizuje, a co nie.** Wybór kalendarzy i kolorów jest decyzją i wędruje
między urządzeniami. **Same wydarzenia nie** — to kopia cudzych danych, po którą oba
urządzenia sięgają do tego samego konta. Rozsyłanie jej podwajałoby ruch i stawiało
pytania o scalanie czegoś, czego nie jesteśmy właścicielem. Żeton odczytu przyrostowego
też zostaje przy urządzeniu, które go dostało.

**Odwołanie to nagrobek, nie usunięcie.** Przy odczycie przyrostowym Google przysyła
odwołanie jako zmianę wydarzenia; fizyczne skasowanie znaczyłoby, że kolejny odczyt
nie ma czego zaktualizować i odwołane wydarzenie wraca.

**Sprzątanie wyłącznie po odczycie pełnym.** Przy przyrostowym „nie przyszło" znaczy
„bez zmian", więc to samo sprzątanie skasowałoby cały kalendarz przy pierwszym odczycie,
w którym nic się nie zmieniło.

**Niedostępny kanał nie zatrzymuje pozostałych ani startu aplikacji.** Kalendarz jest
dodatkiem do zadań; brak sieci ma znaczyć „brak świeżych wydarzeń", a nie „aplikacja
się nie otwiera".

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
| **Oczekiwane** | `Waiting`, sortowane po liczbie dni, z progiem ponaglenia obszaru |
| **Kalendarz** | Godzinowo: dzień / 3 dni / tydzień. Wydarzenia pełne, zadania półprzezroczyste (11.3) |
| **Notatki** | Markdown, edytor i podgląd, tagi, szukanie |
| **Filtry** | Konstruktor warunków, zapisywanie do Ulubionych (11.5) |
| **Przegląd** | Kreator siedmiu kroków (8.3) |
| **Archiwum** | `Done` i `Trashed`, szukanie, przywracanie |
| **Ustawienia** | Konto Google, kalendarze, kopia (12), motyw, strefa (3.4) |

Wszystko poza „Kalendarzem" i „Notatkami" musi być w pełni obsługiwalne z klawiatury. Układ nawigacji — zob. 11.0.

### 11.0 Nawigacja

**Dwa układy, jedna nawigacja.** Szeroko — wszystkie ekrany naraz w jednym albo dwóch
rzędach, bo tam mieszczą się i są najkrótszą drogą do każdego z nich. Wąsko — cztery
pod kciukiem na pasku dolnym, reszta pod przyciskiem „Więcej”.

Piętnaście przycisków zawiniętych w pięć rzędów zjada na telefonie **trzecią część
ekranu, zanim pojawi się jakakolwiek treść**. To nie jest jeden układ do poprawienia:
te same przyciski na pulpicie są dobrym rozwiązaniem, a na telefonie złym, więc muszą
być dwa.

Czwórka na pasku: **Dzisiaj** (ekran startowy), **Teraz** (odpowiada na pytanie, co
robić), **Skrzynka** (jako jedyna rośnie sama, więc jako jedyna ma licznik),
**Kalendarz** (jako jedyny ma godzinę). Reszta to ekrany, do których siada się
świadomie — przegląd, obszary, archiwum — a nie zagląda między jednym a drugim.

O układzie rozstrzyga **faktyczna szerokość okna**, nie platforma: obrót telefonu
i zwężenie okna na pulpicie to ta sama zmiana, a wąskie okno na pulpicie ma ten sam
problem co telefon.

**Wrzut na dole, nad paskiem.** Na telefonie to jest miejsce, w którym stoi kciuk
i nad którym otwiera się klawiatura; pole do pisania na górze ekranu każe sięgać przez
całą jego wysokość. Przy zasadzie 1.3 — wrzut bez tarcia — to nie jest drobiazg.

Pole wrzutu zostaje **widoczne**, a nie schowane pod przyciskiem: przycisk otwierający
pole to jedno dotknięcie więcej przed każdą myślą, a myśl nieprzyjęta w sekundę wraca
do głowy zamiast do skrzynki.

### 11.1 Zasady wyświetlania

- Nigdzie nie pokazywać liczby zaległych jako czerwonej plakietki z liczbą większą niż 9.
  „73 zaległe" jest informacją bezużyteczną i kosztem emocjonalnym. Zamiast tego: „zaległe".
- Widoku „Teraz" nie da się rozwinąć do pełnej listy. Pełna lista jest w „Projektach".
- Przy pustej skrzynce ekran przetwarzania nie jest dostępny.
- Tabela równowagi obszarów nie ma wersji „na skróty" w żadnym innym ekranie. Liczba
  „94 dni bez ruchu" obejrzana mimochodem między zadaniami jest kosztem bez pożytku;
  ma sens tylko wtedy, gdy siadasz do przeglądu i możesz coś z nią zrobić.

### 11.2 Szczegół zadania

Nakładka nad ekranami, nie pozycja w nawigacji. **Jedno wejście** do terminu, dnia
wykonania, przypomnienia i rytmu — cztery osobne ekrany to cztery miejsca do znalezienia
zamiast jednego, a wszystkie cztery dotyczą tej samej decyzji: kiedy to ma się zdarzyć.

**Rytm pokazywany zdaniem, nie formularzem.** Pod polami stoi zdanie w rodzaju „co dwa
tygodnie, w poniedziałki i czwartki, licząc od wykonania". Sześć pól da się wypełnić źle
i nie zauważyć; zdanie da się przeczytać i od razu wiedzieć, czy o to chodziło. To jest
jedyny sposób sprawdzenia wpisanego rytmu **bez czekania dwóch tygodni na wynik**.

Zdanie i zapis biorą się z tego samego kodu, więc podsumowanie nie może pokazywać czegoś
innego niż to, co się zapisze. Reguła niepełna mówi, czego brakuje („co tydzień — ale
w które dni?"), zamiast udawać, że rytmu nie ma.

Zmiana rodzaju rytmu **przestawia zaczepienie na domyślne dla tego rodzaju** (5.7).
Zostawione ręcznie ustawione dałoby „co poniedziałek, licząc od wykonania" jako stan
domyślny — czyli rytm dryfujący na środy.

Lista „co z pominiętym" ma dwie pozycje: „zostaje jako zaległe" i „przepada".
`Accumulate` jest w modelu, ale **nie na liście** (13.2 pkt 1): jest jedyną ścieżką, która
potrafi wyprodukować stertę, a wybór, którego nie widać, nie kusi. Zadanie z tą wartością
przyniesione z drugiego urządzenia działa normalnie.

**Etykiety stanu na listach:** zaległość z datą pierwszego przegapienia, licznik
przesunięć, termin, rytm. Pierwsze przesunięcie nie jest pokazywane — zdarza się każdemu
i nie niesie informacji. Reszta bez czerwieni i bez wykrzykników, zgodnie z 11.1 i z
uzasadnieniem przy N12–N15: to licznik, nie kara.

**Odhaczenie z listy idzie tą samą drogą co ze szczegółu**, bo zadanie powtarzalne musi
przy okazji zrodzić kolejne wystąpienie (8.4) — a z listy tego nie widać.

**Zapisywane są tylko pola naprawdę zmienione.** Zapis „na wszelki wypadek" trafiłby do
dziennika jako świeża decyzja i wygrał scalanie ze zmianą, której naprawdę dokonano na
drugim urządzeniu (9.4).

### 11.3 Siatka godzinowa

Kolumny liczone są **w gronach rzeczy, które się ze sobą stykają**, nie na cały dzień.
Liczenie na cały dzień zwęziłoby poranne spotkanie do jednej trzeciej szerokości tylko
dlatego, że wieczorem coś się nakłada. Zwolniona kolumna jest używana ponownie, inaczej
dzień z wieloma krótkimi punktami rozpadłby się na nitki.

Wydarzenie przechodzące przez północ pojawia się **na obu dniach, przycięte** do granic
każdego z nich — inaczej na siatce drugiego dnia zaczynałoby się „minus godzinę temu".
Koniec dokładnie o północy nie wchodzi na dzień następny: spotkanie do 24:00 kończy się
dziś.

Bardzo krótkie wydarzenie ma **wysokość minimalną**. Pięciominutowe spotkanie narysowane
co do proporcji byłoby kreską, w którą nie da się trafić palcem.

**Zadanie bez godziny ląduje na pasku całodniowym, nie o północy.** Zgadywanie godziny
zrobiłoby z listy zadań kalendarz, w którym wszystko jest umówione — a to jest dokładnie
ten rodzaj planowania, który się nie utrzymuje. Godzina (`DoTime`) jest opcjonalna
i czyszczona razem z dniem wykonania, bo bez dnia nie znaczy nic.

**Tydzień zaczyna się w poniedziałek**, a nie „od dziś przez siedem dni": tydzień, który
zaczyna się w środę, nie wygląda jak tydzień.

### 11.4 Notatki

**Edytor i podgląd obok siebie, nie na przemian.** Przełącznik „pisz / oglądaj" każe
pamiętać, w którym trybie się jest — a przy notatce pisanej raz i czytanej dziesięć razy
to pytanie zadawane bez potrzeby.

Obsługiwany podzbiór Markdown: nagłówki, akapity, listy punktowane i numerowane, cytaty,
kod, pogrubienie, kursywa, odsyłacze. Tabele, obrazy i przypisy **nie** — notatka osobista
ich nie potrzebuje, a każdy z nich to osobny sposób rysowania.

Rozbiór robi Markdig; własny parser Markdown kończy się tym, że po roku obsługuje
osiemdziesiąt procent formatu, a każdy kolejny przypadek jest poprawką. Po naszej stronie
zostaje spłaszczenie drzewa do **płaskiej listy bloków** — podgląd notatki nie potrzebuje
pełnego modelu dokumentu, tylko czegoś, co da się przelecieć jednym przebiegiem, a poziom
zagnieżdżenia listy niesie sam blok.

Wyróżnienia **nie zagnieżdżają się**: pogrubiony kursywny odsyłacz jest w notatce
osobistej rzeczą, której nie ma, a jego obsługa oznaczałaby drzewo zamiast listy.

### 11.5 Filtry

**Płaska lista warunków, nie drzewo wyrażeń.** „Albo" mieszka **wewnątrz** pojedynczego
warunku (stan: następne albo zaplanowane), „i" **między** warunkami. Pełne wyrażenie
logiczne z nawiasami jest mocniejsze i w konstruktorze graficznym praktycznie nieużywane:
żeby je złożyć, trzeba myśleć o priorytetach operatorów, a żeby złożone przeczytać —
rozwinąć nawiasy w głowie. Ten podział ról pokrywa każdy widok, jaki faktycznie się układa,
i daje się przeczytać jednym zdaniem.

Jeden warunek na pole. Dwa warunki na to samo pole połączone „i" dawałyby zbiór pusty
w każdym ciekawym przypadku, a konstruktor, w którym da się kliknąć warunek gwarantujący
zero wyników, uczy nieufności do całego ekranu — więc drugi warunek zastępuje pierwszy.

**Daty wyłącznie względne.** Zaległe, dzisiaj, w tym tygodniu, w ciągu 30 dni, kiedyś
później, ustawione, nieustawione. Wybieraka daty nie ma i nie jest to uproszczenie do
nadrobienia: warunek „termin przed 20 września" zapisany do Ulubionych jest poprawny przez
cztery dni, a potem po cichu przestaje cokolwiek znaczyć — nadal się uruchamia, nadal coś
zwraca i nadal wygląda na działający. Zapisany widok musi opisywać położenie względem
dzisiaj, bo tylko takie zdanie jest prawdziwe również za miesiąc.

„W tym tygodniu" to **dziś i sześć następnych dni**, nie tydzień kalendarzowy. Tydzień
kalendarzowy kurczy się z każdym dniem i w sobotę pokazuje jeden dzień albo zero, czyli
filtr byłby najbardziej pusty wtedy, kiedy się go otwiera przy planowaniu weekendu.

**Wykonane i wyrzucone nie wchodzą, dopóki filtr sam o ten stan nie poprosi.** Cmentarz
i archiwum rosną bez końca i po roku są większe niż wszystko inne razem; gdyby wchodziły
domyślnie, każdy filtr trzeba by zaczynać od odejmowania — a filtr zaczynany od wykluczania
jest filtrem napisanym od tyłu. Wpisanie stanu do warunku działa dosłownie: „pokaż
wykonane" pokaże wykonane. Nagrobek (`Deleted`) nie wchodzi nigdy.

**Filtr bez warunków nie pasuje do niczego.** Logika mówi co innego — „i" po pustym zbiorze
jest prawdą — ale wszystko to tu kilkaset pozycji wysypanych na ekran w chwili, w której nie
poproszono jeszcze o nic. Pusty konstruktor znaczy „nie zaczęłam", a nie „pokaż bazę".

**Warunki sprawdzane w pamięci, nie tłumaczone na SQL.** Przetłumaczenie konstruktora na
wyrażenie, które EF Core umie zamienić na zapytanie, znaczyłoby drzewo z gałęzią na każde
pole i każde okno czasowe — najbardziej podatną na błędy część aplikacji, przy czym błąd
objawia się wyjątkiem dopiero przy uruchomieniu konkretnej kombinacji. Po drugiej stronie
wagi stoi baza jednej osoby: kilka tysięcy zadań to rząd wielkości jednego odczytu z dysku.
Przy stu tysiącach decyzja byłaby inna.

**Zapisany widok jest agregatem synchronizowanym**, a warunki siedzą w jednej kolumnie
JSON — z tego samego powodu co reguła powtarzania (5.7): filtr jest **jedną decyzją**,
więc wygrywa albo przegrywa w całości. Scalanie per pole potrafiłoby złożyć widok
z połówek dwóch różnych: warunek obszaru z telefonu i warunek stanu z komputera.
Kolejność warunków w zapisie jest ustalona, żeby ta sama decyzja dawała ten sam tekst.

Warunek nieczytelny — bo powstał w nowszej wersji aplikacji — odpada **pojedynczo**,
a reszta widoku działa. Widok nieczytelny w całości zostaje w Ulubionych z nazwą, zamiast
zniknąć bez słowa: wtedy wiadomo, co przepadło i co ułożyć na nowo.

Sens zapisywania nie jest w oszczędzeniu kliknięć. Filtr ułożony raz i nazwany
(„Telefony", „Kwadrans przed wyjściem", „Zaległe w domu") jest decyzją podjętą na spokojnie,
z której można skorzystać w chwili, w której na układanie warunków nie ma ani cierpliwości,
ani uwagi. Ulubione to zapas gotowych odpowiedzi na pytanie „co teraz", zrobiony wtedy,
kiedy dało się myśleć.

W ekranie wszystkie osie widoczne naraz, każda domyślnie wyłączona — zamiast przycisku
„dodaj warunek", który prowadzi do listy pól, a z niej do edytora zależnego od wybranego
pola. Tamta droga jest ogólniejsza i kosztuje trzy kliknięcia oraz pamiętanie, czego się
jeszcze nie ustawiło. Tutaj widać w jednym spojrzeniu, **czym filtr jest** — łącznie z tym,
co w nim nieustawione. Wyniki przeliczane po każdej zmianie, bez przycisku „szukaj":
filtr układa się metodą prób, a przycisk zamieniłby każdą próbę w osobną decyzję
„czy warto sprawdzić".

---

## 12. Kopia zapasowa

Eksport całości do jednego pliku JSON, offline, bez konta — jak w Castellanie.
Import z podmianą całości albo scaleniem po HLC.

Osobno od synchronizacji, a nie zamiast niej. Synchronizacja chroni przed utratą
urządzenia; kopia chroni przed **utratą Dysku, konta albo zaufania do nich** — i przed
przypadkiem, w którym błąd rozjechał dane i rozsiał je na oba urządzenia.

**Treść kopii to te same wiersze zmian, którymi mówi synchronizacja** (9.4), tylko
zebrane w jeden plik zamiast dopisywane do porcji. Nie jest to oszczędność kodu: gdyby
kopia miała własny format i własne scalanie, byłyby dwie implementacje reguły „nowsze
pole wygrywa", a rozjechałyby się przy pierwszej zmianie modelu — po cichu i tylko
u tego, kto akurat odtwarzał kopię.

Wiersze grupowane **po znaczniku pola**, nie po encji. Gdyby cała encja szła pod jednym
znacznikiem (tym z `UpdatedAt`), pola zmienione dawno dostałyby w kopii datę ostatniej
zmiany czegokolwiek w tym rekordzie — i po wgraniu wygrałyby ze świeższymi wartościami
z drugiego urządzenia. Kopia cofałaby dane, wyglądając na poprawną.

Eksportowane są **agregaty synchronizowane**, rozpoznawane po tym samym warunku co przy
scalaniu. Rzeczy lokalne (kursory, pokazane przypomnienia, pobrane wydarzenia kalendarza,
dziennik zmian) nie wchodzą: należą do urządzenia, a nie do danych, i odtworzą się same.
Treść załączników też nie — w kopii jest wpis, nie zawartość pliku.

Wgranie **nie trafia do dziennika zmian**: inaczej każdy odtworzony rekord poleciałby na
Dysk jako świeża zmiana i wskrzesił na drugim urządzeniu rzeczy skasowane po zrobieniu
kopii.

**Podmiana całości tylko na urządzeniu, które zaczyna od nowa** — po awarii, po
przesiadce na nowy sprzęt. Na urządzeniu podpiętym do synchronizacji podmiana jest
pozorna: czyszczenie nie zostawia śladu w dzienniku, bo dziennik niesie zmiany, a nie
usunięcia tabel, więc drugie urządzenie o niczym się nie dowie i przy najbliższym
scaleniu odda swój stan z powrotem. To nie jest usterka do naprawienia — to wynika
z tego, czym jest synchronizacja plikowa (9.8).

Plik jest jawny i czytelny bez aplikacji: zwykły JSON z nazwami tabel i pól. Kopia,
której nie da się obejrzeć notatnikiem, jest obietnicą, nie zabezpieczeniem.

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

1. **Czy `Accumulate` jest potrzebne w wersji 1?** Zaimplementowane w modelu razem
   z ogranicznikiem 120 wystąpień na przebieg i z zamianą zaległego wystąpienia na
   akcję bez dnia (8.4b) — obie rzeczy dokładnie po to, żeby sterty nie było.
   **Otwarte zostaje, czy interfejs ma je w ogóle proponować.** Wstępnie nie: `Carry`
   jest domyślne i pokrywa niemal wszystko, a wybór, którego nie widać, nie kusi.
2. **Wartości `QuietDays` i `DefaultNudgeDays`** są zgadnięte. Do korekty po miesiącu
   realnego używania — przed pierwszym kontaktem z życiem nie ma na czym oprzeć decyzji.
3. **Heurystyka podpowiadania energii** w „Teraz". Zaczynamy od pory dnia; wersja oparta
   na historii odhaczeń dopiero wtedy, gdy będzie historia.
4. **Repozytorium** — osobne czy katalog w Castellanie. Blokuje etap 0.

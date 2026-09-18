---
title: Instalacja
---

# Instalacja Marshala

Dla kogoś, kto dostał od kogoś link i chce z tego korzystać. Nie zakłada niczego poza
umiejętnością pobrania pliku.

Marshala **nie ma w żadnym sklepie** — ani w Google Play, ani w Microsoft Store.
Nie dlatego, że coś z nim nie tak: Google wymaga od kont osobistych kilkunastu testerów
przez dwa tygodnie, zanim dopuści aplikację do sklepu, a to jest wymóg pomyślany dla
czegoś zupełnie innego niż planer pisany dla siebie i rodziny. Pliki leżą więc
w [wydaniach na GitHubie](https://github.com/kodalus/Marshal/releases) i instaluje się
je ręcznie. Poniżej jest napisane, co przy tym powie system i dlaczego.

---

## Zanim zaczniesz: co działa bez niczego

Po instalacji Marshal **działa w całości offline**, na jednym urządzeniu, bez konta
i bez sieci. Wszystkie zadania, projekty, obszary, notatki, kalendarz z kanałów iCal,
kopia zapasowa — wszystko.

Konto Google jest potrzebne **wyłącznie** do dwóch rzeczy: synchronizacji między
telefonem a komputerem oraz kalendarza Google. Jeśli używasz jednego urządzenia
i nie potrzebujesz kalendarza Google, możesz pominąć całą trzecią część tej instrukcji.

---

## Android

Wymagany Android 10 lub nowszy.

### 1. Pobierz plik

Wejdź na [wydania](https://github.com/kodalus/Marshal/releases) **telefonem**
i pobierz `marshal.apk`.

Przeglądarka powie coś w rodzaju „Ten typ pliku może uszkodzić urządzenie" albo
„Ten plik zawiera wirusa". **Nie znalazła wirusa.** Ten komunikat pojawia się przy
każdym pliku `.apk` pobranym spoza sklepu, niezależnie od zawartości — jest ostrzeżeniem
o pochodzeniu, nie wynikiem badania. Wybierz „Pobierz mimo to".

### 2. Sprawdź, czy plik jest tym, który powstał (opcjonalnie)

W opisie wydania jest suma SHA-256. Menedżer plików na Androidzie zwykle potrafi ją
pokazać; jeśli nie, pomiń ten krok — sprawdzenie sumy jest po to, żeby wykluczyć
podmianę pliku po drodze, a nie żeby cokolwiek odblokować.

### 3. Zainstaluj

Dotknij pobranego pliku. Android powie, że ta przeglądarka nie ma zgody na instalowanie
nieznanych aplikacji, i zaproponuje ustawienia. Włącz zgodę **dla tej jednej aplikacji**
(przeglądarki albo menedżera plików), wróć i zainstaluj.

Po instalacji zgodę można wyłączyć — do samego działania Marshala nie jest potrzebna.

### 4. Pozwól aplikacji działać w tle — **to nie jest opcjonalne**

Bez tego Marshal działa poprawnie tylko wtedy, gdy masz go otwartego na ekranie.
Przy zamkniętej aplikacji **przypomnienia nie odezwą się, a synchronizacja nie
dojdzie** — i nie będzie po tym żadnego śladu poza tym, że nic się nie stało.

Nie da się tego załatwić uprawnieniem z instalacji. Producenci telefonów dokładają
własne oszczędzanie baterii ponad to, co robi Android, i ono zatrzymuje aplikacje
niezależnie od tego, o co poprosiły. Trzeba więc powiedzieć systemowi wprost, dwa razy.

**Bateria bez ograniczeń.** Ustawienia → Aplikacje → Marshal → **Bateria** → wybierz
**Bez ograniczeń** (bywa nazwane „Nieograniczone", „Nie optymalizuj", „Zezwalaj na
działanie w tle"). Ustawienie domyślne — „Optymalizowane" — znaczy, że system odkłada
pracę w tle na później, a „później" przy uśpionym telefonie trwa godzinami.

**Autostart.** Na telefonach Xiaomi, Redmi, POCO, Huawei, Honor, OPPO, realme
i vivo jest osobny przełącznik, bez którego system w ogóle nie pozwoli uruchomić
aplikacji, gdy ta nie jest na ekranie. Nazywa się zwykle **Autostart** albo
**Uruchamianie automatyczne** i leży w Ustawieniach aplikacji albo w aplikacji
zarządzania baterią (na Xiaomi: Ustawienia → Aplikacje → Zarządzaj aplikacjami →
Marshal → Autostart). Bez tego przypomnienia milkną **po każdym ponownym uruchomieniu
telefonu**.

**Na Xiaomi dodatkowo: zablokuj w ostatnich.** Otwórz listę ostatnich aplikacji,
przytrzymaj kafelek Marshala i wybierz kłódkę. Bez tego zamknięcie listy ostatnich
zabija proces razem z zaplanowanymi budzikami.

**Powiadomienia.** Przy pierwszym uruchomieniu aplikacja poprosi o zgodę na
powiadomienia. Jeśli ją odrzucisz, przypomnienia nie mają jak się pokazać —
wtedy włącz je w Ustawienia → Aplikacje → Marshal → Powiadomienia.

Sprawdzenie, czy zadziałało: zamknij aplikację na godzinę, otwórz i wejdź
w **Więcej → Dziennik**. Powinny tam być wpisy „Synchronizacja w tle".

### 5. Aktualizacje

Aplikacja nie aktualizuje się sama i nie ma sklepu, który by o tym przypomniał.
Są dwie drogi:

- **[Obtainium](https://github.com/ImranR98/Obtainium)** — aplikacja, która instaluje
  i aktualizuje programy prosto z GitHuba. Dodajesz w niej adres
  `https://github.com/kodalus/Marshal` raz i od tej pory dostajesz nowe wersje tak,
  jakby był sklep. **To jest zalecana droga.**
- Ręcznie: co jakiś czas pobrać nowy `marshal.apk` i zainstalować na wierzchu.
  Dane zostają — instalacja na wierzchu ich nie rusza.

---

## Windows

Wymagany Windows 10 albo 11, 64-bitowy.

### 1. Pobierz

Z tych samych [wydań](https://github.com/kodalus/Marshal/releases) pobierz
`marshal-windows.zip`. **Jeszcze nie rozpakowuj** — najpierw krok 2.

Archiwum zawiera środowisko uruchomieniowe, więc **nie trzeba niczego doinstalowywać**.
Stąd jego rozmiar.

### 2. Odblokuj archiwum

Windows oznacza pliki pobrane z internetu i przenosi to oznaczenie na wszystko, co
z nich wypakujesz. Odblokowanie archiwum przed rozpakowaniem załatwia sprawę raz;
zrobione po rozpakowaniu trzeba by powtórzyć na każdym pliku osobno.

Prawy przycisk na `marshal-windows.zip` → **Właściwości** → na dole zaznacz
**Odblokuj** → OK. Jeśli tego pola nie ma, to znaczy, że Windows nie oznaczył pliku
i nie ma czego odblokowywać.

### 3. Rozpakuj i uruchom

Rozpakuj archiwum tam, gdzie chcesz — nie ma instalatora, aplikacja jest katalogiem
z plikami i nie wpisuje się do rejestru. Potem uruchom:

`Marshal.Desktop.exe` w rozpakowanym katalogu. Skrót na pulpicie robi się prawym
przyciskiem na tym pliku → **Pokaż więcej opcji** → **Wyślij do** → **Pulpit**.

Przy pierwszym uruchomieniu SmartScreen powie „System Windows ochronił Twój komputer"
i pokaże jeden przycisk. **Kliknij „Więcej informacji", potem „Uruchom mimo to".**

To ostrzeżenie nie mówi nic o zawartości pliku. Windows sprawdza, czy plik jest podpisany
certyfikatem komercyjnym i czy ma już reputację; certyfikat kosztuje ponad tysiąc złotych
rocznie i aplikacja rozdawana rodzinie go nie ma. Ostrzeżenie pojawia się raz na wersję.

### 4. Gdzie są dane

Baza, załączniki i ustawienia leżą w katalogu danych użytkownika, nie w katalogu
aplikacji — skasowanie rozpakowanego katalogu nie kasuje zadań. Kopię zapasową robi się
w **Ustawieniach → Kopia zapasowa**.

---

## Pierwsze uruchomienie — czego się spodziewać

Instrukcja kończyła się dotąd na „zainstalowane", a to jest połowa pytania.

**Aplikacja zakłada na start dziesięć obszarów odpowiedzialności** — Praca, Dzieci,
Zdrowie, Dom, Finanse, Związek, Rozwój własny, Twórczość, Sprawy urzędowe, Relacje.
Nie są one na stałe: zmienia się im nazwy, dokłada własne i usuwa niepotrzebne na
ekranie **Projekty**. Są od początku, bo w tej metodzie każde zadanie poza skrzynką
musi należeć do jakiegoś obszaru — bez nich pierwsze zadanie nie dałoby się zapisać.

**Zacznij od skrzynki.** Pole na dole okna — „Wrzuć myśl i naciśnij Enter" — przyjmuje
sam tytuł i nic więcej. O to chodzi: wrzucanie ma kosztować dwie sekundy, a decyzje
podejmuje się później, na ekranie przetwarzania skrzynki.

**Na telefonie jest widget.** „Marshal — na dziś": plan dnia na ekranie domowym,
z odhaczaniem i przełączaniem między dniami. Dodaje się go jak każdy inny —
przytrzymaj puste miejsce na ekranie domowym → Widżety → Marshal.

**Kopię zapasową robi się w Ustawieniach.** Warto zrobić pierwszą, zanim zaczniesz
wpisywać cokolwiek ważnego — to jeden plik JSON, który odtwarza całość.

---

## Konto Google — synchronizacja i kalendarz

Ta część jest **opcjonalna** i potrzebna tylko wtedy, gdy chcesz mieć te same dane na
telefonie i na komputerze albo widzieć w Marshalu swój kalendarz Google.

### Najważniejsza rzecz do zrozumienia

**Zakładasz własny projekt w Google Cloud i używasz własnych poświadczeń.** Marshal nie
ma wspólnego dla wszystkich identyfikatora klienta. Znaczy to, że:

- twoje dane idą na **twój** Dysk, przez **twoją** zgodę,
- autor aplikacji nie figuruje w tej relacji i nie ma do niczego dostępu,
- nikt nie może ci tego wyłączyć ani podejrzeć.

Ceną jest kwadrans klikania w konsoli Google przy pierwszym uruchomieniu.

**Zrób to w tej kolejności:** przejdź całą instrukcję
**[`google-dysk.md`](google-dysk.md)** — zakładanie projektu, włączenie API, ekran zgody,
poświadczenia — a potem wróć tutaj po trzy rzeczy, na których najłatwiej się potknąć.
Poniższe trzy akapity **nie zastępują** tamtej instrukcji; one tylko ostrzegają przed
miejscami, w których łatwo skręcić w złą stronę.

### Potknięcie pierwsze: telefon też bierze poświadczenia „komputerowe"

Zakładając identyfikator klienta OAuth, wybierz typ **Aplikacja komputerowa** —
**także wtedy, gdy zakładasz go dla telefonu**. Typ „Android" wygląda na właściwy
i nie jest: poprosi o nazwę pakietu i odcisk klucza, którym podpisano APK, a Marshal
tą drogą nie chodzi i nic z tym nie zadziała.

Telefon używa **dokładnie tych samych** poświadczeń co komputer — tego samego
identyfikatora, tej samej tajemnicy, tego samego ekranu zgody. W konsoli Google nie
ma dla niego nic osobnego do zrobienia.

Dlaczego tak: zgoda wraca **na port pętli zwrotnej**, a przeglądarka telefonu sięga
do pętli zwrotnej tego samego telefonu. Google pozwala klientom komputerowym wracać
na dowolny taki port i nie wymaga zgłaszania go z góry — więc cała maszyneria
z odciskami podpisu jest tu niepotrzebna.

Poświadczenia wkleja się **na każdym urządzeniu osobno**: leżą w ustawieniach lokalnych
i celowo nie jadą przez synchronizację. Poświadczenia do Dysku nie mają jechać przez Dysk.

### Potknięcie drugie: tryb testowy i logowanie co tydzień

Ekran zgody zostawiony w trybie **testowym** sprawia, że Google unieważnia zgodę
**co siedem dni** — i trzeba logować się od nowa, na każdym urządzeniu.

Jeśli chcesz **samej synchronizacji**, przełącz swój ekran zgody na produkcyjny
(„Opublikuj aplikację"). Przy uprawnieniu do Dysku, którego Marshal używa, nie wymaga
to żadnej weryfikacji — to jedno kliknięcie i żeton przestaje wygasać.

Jeśli chcesz **dodatkowo kalendarza Google**, przejście do produkcji wymagałoby przeglądu
Google. Wtedy albo godzisz się na logowanie raz w tygodniu, albo podłączasz kalendarz
adresem `.ics` (kanał iCal), który nie wymaga konta w ogóle.

### Potknięcie trzecie: przeglądarka na telefonie nie wraca

Po zatwierdzeniu zgody na telefonie przeglądarka zwykle **nie wraca do aplikacji** —
zostaje na stronie, która się nie wczytuje. To nie jest błąd i nie trzeba zaczynać od nowa:
przytrzymaj pasek adresu, **Kopiuj**, wróć do Marshala i wklej adres w pole
**„Przeglądarka nie wróciła sama?"**, które widać na czas logowania. Kod zgody jest
w tym adresie w całości.

### Drugie urządzenie

Na drugim urządzeniu wklejasz **ten sam** identyfikator klienta, **tę samą** tajemnicę
i logujesz się na **to samo** konto Google. Dopiero wtedy oba widzą nawzajem swoje
zmiany: „aplikacja", której Dysk daje dostęp do własnych plików, to identyfikator
klienta OAuth, a nie instalacja.

Dwa różne identyfikatory na dwóch urządzeniach dałyby dwa osobne zestawy plików na
jednym Dysku — i dwie aplikacje, które się nawzajem nie widzą, choć obie działają.

---

## Gdy coś nie działa

| Objaw | Co to znaczy |
|---|---|
| „Ten plik zawiera wirusa" przy pobieraniu APK | Ostrzeżenie o pochodzeniu pliku, nie wynik badania. Pobierz mimo to. |
| SmartScreen blokuje na Windows | Brak certyfikatu komercyjnego. „Więcej informacji" → „Uruchom mimo to". |
| Konsola Google pyta o nazwę pakietu i odcisk klucza | Wybrany zły typ klienta. Ma być **Aplikacja komputerowa**, także dla telefonu. |
| Trzeba logować się do Google co tydzień | Ekran zgody w trybie testowym — zob. wyżej. |
| Kalendarz Google pusty mimo zalogowania | Kalendarze wybiera się w **Ustawieniach → Kalendarze**. Pobranie odświeża się samo co pięć minut. |
| Zmiana z jednego urządzenia nie dochodzi do drugiego | Oba muszą mieć te same poświadczenia i to samo konto. Zajrzyj do **Dziennika** — jest tam każdy przebieg synchronizacji razem z powodem niepowodzenia. |
| Przypomnienia nie przychodzą przy zamkniętej aplikacji | Bateria nie jest ustawiona na „bez ograniczeń" albo brakuje autostartu — zob. krok 4 instalacji na Androidzie. |
| Przypomnienia milkną po restarcie telefonu | Brak autostartu. To ten sam przełącznik. |
| Nic się nie synchronizuje, dopóki aplikacja jest zamknięta | To samo: bateria i autostart. Przy otwartej aplikacji odczyt idzie co minutę, przy zamkniętej co pół godziny — i tylko wtedy, gdy system na to pozwala. |
| „Aplikacja nie została zainstalowana" przy aktualizacji | Nowy plik podpisano innym kluczem niż zainstalowany. **Najpierw kopia zapasowa**, potem odinstaluj starą wersję i zainstaluj nową — odinstalowanie kasuje dane. |
| Nie wiadomo, co się stało | **Więcej → Dziennik.** Ostatnie 500 zdarzeń, z problemami osobno. |

Dziennik jest pierwszym miejscem do sprawdzenia przy czymkolwiek. Jest lokalny —
nie wychodzi z urządzenia ani do synchronizacji, ani do kopii.

---

## Co ta aplikacja o tobie wie

Nic. Nie ma serwera, analityki, telemetrii ani raportowania awarii. Dane są na twoim
urządzeniu, a przy włączonej synchronizacji także na twoim Dysku Google — pod twoją
zgodą, którą sam nadałeś własnemu projektowi.

Pełny tekst: [`prywatnosc.md`](prywatnosc.md).

---

## Czego nie ma

- Nie ma kont, logowania do Marshala ani chmury autora.
- Nie ma wersji na iOS, macOS ani Linuksa. Kod jest wieloplatformowy, ale wydania nie
  są budowane — na Linuksie trzeba zbudować samemu.
- Nie ma automatycznych aktualizacji poza Obtainium.
- Nie ma wsparcia w rozumieniu obsługi klienta. Sprawy zgłasza się przez
  [zgłoszenia w repozytorium](https://github.com/kodalus/Marshal/issues).

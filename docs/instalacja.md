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

### 4. Aktualizacje

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

### 1. Pobierz i rozpakuj

Z tych samych [wydań](https://github.com/kodalus/Marshal/releases) pobierz
`marshal-windows.zip` i rozpakuj, gdzie chcesz. Nie ma instalatora — aplikacja
jest katalogiem z plikami i nie wpisuje się do rejestru.

Archiwum zawiera środowisko uruchomieniowe, więc **nie trzeba niczego doinstalowywać**.
Stąd jego rozmiar.

### 2. Odblokuj archiwum przed rozpakowaniem

Windows oznacza pliki pobrane z internetu i potrafi z tego powodu blokować programy
w środku. Zanim rozpakujesz: prawy przycisk na `marshal-windows.zip` → **Właściwości**
→ na dole **Odblokuj** → OK. Jeśli pola „Odblokuj" nie ma, nie ma też problemu.

### 3. Uruchom

`Marshal.Desktop.exe` w rozpakowanym katalogu.

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

Pełna instrukcja krok po kroku: **[`google-dysk.md`](google-dysk.md)**. Tu tylko dwie
rzeczy, na których najłatwiej się potknąć.

### Potknięcie pierwsze: tryb testowy i logowanie co tydzień

Ekran zgody zostawiony w trybie **testowym** sprawia, że Google unieważnia zgodę
**co siedem dni** — i trzeba logować się od nowa, na każdym urządzeniu.

Jeśli chcesz **samej synchronizacji**, przełącz swój ekran zgody na produkcyjny
(„Opublikuj aplikację"). Przy uprawnieniu do Dysku, którego Marshal używa, nie wymaga
to żadnej weryfikacji — to jedno kliknięcie i żeton przestaje wygasać.

Jeśli chcesz **dodatkowo kalendarza Google**, przejście do produkcji wymagałoby przeglądu
Google. Wtedy albo godzisz się na logowanie raz w tygodniu, albo podłączasz kalendarz
adresem `.ics` (kanał iCal), który nie wymaga konta w ogóle.

### Potknięcie drugie: przeglądarka na telefonie nie wraca

Po zatwierdzeniu zgody na telefonie przeglądarka zwykle **nie wraca do aplikacji** —
zostaje na stronie, która się nie wczytuje. To nie jest błąd i nie trzeba zaczynać od nowa:
przytrzymaj pasek adresu, **Kopiuj**, wróć do Marshala i wklej adres w pole
**„Przeglądarka nie wróciła sama?"**, które widać na czas logowania. Kod zgody jest
w tym adresie w całości.

### Drugie urządzenie

Poświadczenia wkleja się **na każdym urządzeniu osobno** — leżą w ustawieniach lokalnych
i celowo nie jadą przez synchronizację. Poświadczenia do Dysku nie mają jechać przez Dysk.

Używasz **tych samych** poświadczeń i tego samego konta na telefonie i na komputerze;
wtedy oba widzą nawzajem swoje zmiany.

---

## Gdy coś nie działa

| Objaw | Co to znaczy |
|---|---|
| „Ten plik zawiera wirusa" przy pobieraniu APK | Ostrzeżenie o pochodzeniu pliku, nie wynik badania. Pobierz mimo to. |
| SmartScreen blokuje na Windows | Brak certyfikatu komercyjnego. „Więcej informacji" → „Uruchom mimo to". |
| Trzeba logować się do Google co tydzień | Ekran zgody w trybie testowym — zob. wyżej. |
| Kalendarz Google pusty mimo zalogowania | Kalendarze wybiera się w **Ustawieniach → Kalendarze**. Pobranie odświeża się samo co pięć minut. |
| Zmiana z jednego urządzenia nie dochodzi do drugiego | Oba muszą mieć te same poświadczenia i to samo konto. Zajrzyj do **Dziennika** — jest tam każdy przebieg synchronizacji razem z powodem niepowodzenia. |
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

---
title: Dysk Google i kalendarz
---

# Dysk Google jako składnica synchronizacji

Instrukcja jednorazowa. Po jej wykonaniu masz identyfikator klienta i tajemnicę,
które aplikacja podaje przy logowaniu.

## Co i dlaczego

Marshal nie ma serwera. Każde urządzenie zapisuje **własny** dziennik zmian i czyta
cudze; nikt nie pisze po cudzym, więc konflikt zapisu nie powstaje (spec 9). Dysk
Google jest tu wyłącznie miejscem na pliki — dowolnym, wymiennym. Ta sama mechanika
chodzi na katalogu Dropboksa albo Syncthinga i tak jest sprawdzana testami.

Dziennik to ciąg **niezmiennych porcji**. Porcja raz zapisana nigdy się nie zmienia,
bo Dysk Google nie umie dopisać do pliku — umie tylko założyć nowy albo wgrać nową
wersję całości. Gdyby przy każdej synchronizacji wgrywać cały dziennik od nowa,
przerwane wgranie niszczyłoby go w całości, zamiast tylko nie dołożyć ostatniej porcji.

## Krok 1 — projekt w Google Cloud

1. Wejdź na <https://console.cloud.google.com/>.
2. Góra strony, lista projektów → **Nowy projekt**. Nazwa: `Marshal`. Utwórz.
3. Upewnij się, że ten projekt jest wybrany (nazwa na górnym pasku).

Projekt jest darmowy. Dysk Google nie liczy sobie za API — liczy się tylko miejsce
na Twoim koncie, a dziennik zmian to kilobajty.

## Krok 2 — włączenie API Dysku

1. Menu boczne → **Interfejsy API i usługi** → **Biblioteka**.
2. Wyszukaj `Google Drive API` → **Włącz**.

## Krok 3 — ekran zgody

1. **Interfejsy API i usługi** → **Ekran zgody OAuth**.
2. Typ użytkownika: **Zewnętrzny**. (Wewnętrzny jest tylko dla kont firmowych
   Google Workspace.)
3. Nazwa aplikacji: `Marshal`. Adres pomocy i kontaktowy: Twój własny.
4. Zakresy: **nie dodawaj żadnego ręcznie.** Aplikacja prosi o
   `.../auth/drive.file`, a to uprawnienie jest **nieuznane za wrażliwe**, więc
   nie wymaga przeglądu Google ani weryfikacji.
5. Użytkownicy testowi: dodaj **swój adres Gmail**. Bez tego logowanie odbija się
   komunikatem „Dostęp zablokowany: aplikacja nie przeszła weryfikacji Google",
   błąd 403 `access_denied`. Dotyczy to **każdego** zakresu, także tych nieuznanych
   za wrażliwe: w trybie testowym decyduje lista, a nie rodzaj uprawnienia.

### Strona „Marka" — bez niej nie da się opublikować

Konsola rozbiła dawny jeden ekran zgody na kilka zakładek. Przycisk **Opublikuj
aplikację** zostaje szary, dopóki nie jest wypełniona **Marka** (*Branding*),
a komunikat pod nim mówi dokładnie to i podaje odnośnik.

Do wypełnienia są trzy rzeczy i tylko one są obowiązkowe:

1. **Nazwa aplikacji** — `Marshal`.
2. **Adres e-mail pomocy technicznej** — Twój własny.
3. **Dane kontaktowe dewelopera** — ten sam adres.

Do **samego testowania** tyle wystarczy. Do **przejścia w tryb produkcyjny** konsola
żąda dwóch rzeczy więcej i nie da się ich ominąć:

4. **Adres URL strony głównej.**
5. **Adres URL polityki prywatności.**

Ten akapit mówił kiedyś, że oba są opcjonalne. **Nie są** — komunikat pod szarym
przyciskiem wymienia je wprost. Dobra wiadomość jest taka, że nie trzeba do tego
zakładać ani kupować domeny.

### Strona bez domeny — GitHub Pages

Repozytorium Marshala wystawia katalog `docs/` jako stronę. Adresy wyglądają tak:

| Pole w konsoli | Co wpisać |
|---|---|
| Strona główna aplikacji | `https://<konto>.github.io/Marshal/` |
| Polityka prywatności | `https://<konto>.github.io/Marshal/prywatnosc` |

Włączenie: **Settings → Pages → Source: Deploy from a branch → `main` / `/docs`**.

Domena autoryzowana to wtedy `<konto>.github.io`. Google wymaga potwierdzenia jej
własności w Search Console, a to działa, bo `github.io` jest domeną publiczną —
adres konta jest z punktu widzenia Google osobną domeną, a nie podstroną cudzej.
Potwierdzenie idzie plikiem HTML, który GitHub Pages poda tak samo jak każdy inny.

Gdyby konsola nie przyjęła adresu z podkatalogiem, zostaje droga pewniejsza:
osobne repozytorium o nazwie **dokładnie** `<konto>.github.io`, które wystawia
stronę w korzeniu domeny. Wtedy adresy nie mają podkatalogu, a plik potwierdzający
leży tam, gdzie Search Console go szuka.

To jest darmowe i zajmuje kwadrans. Zakładanie własnej domeny dla aplikacji, z której
korzysta jedna rodzina, nie jest potrzebne.

### Tryb testowy kontra produkcyjny

To jest decyzja, nie formalność, i przy synchronizacji ma konkretną cenę.

**W trybie testowym Google wydaje żeton odświeżalny na siedem dni.** Po tygodniu
logowanie trzeba powtórzyć — niezależnie od zakresu i od tego, że nic się nie zmieniło.
Aplikacja sobie z tym radzi (kasuje nieważny żeton i pyta o zgodę jeszcze raz), ale
znaczy to przeglądarkę raz w tygodniu, na każdym urządzeniu.

**W trybie produkcyjnym żeton nie wygasa.** Przy samym `drive.file` przejście do
produkcji **nie wymaga żadnej weryfikacji**, bo to uprawnienie nie jest wrażliwe —
klikasz „Opublikuj aplikację" i tyle. Przy `calendar.readonly` jest inaczej: ono
**jest** wrażliwe, więc produkcja wymagałaby przeglądu Google.

Stąd zalecenie:

| Czego chcesz | Co ustawić |
|---|---|
| Sama synchronizacja (`drive.file`) | **Opublikuj aplikację** — żeton bezterminowy, bez weryfikacji |
| Dodatkowo kalendarz Google | **Też opublikuj** — zob. akapit niżej |
| Kalendarze bez logowania | Kanały iCal — nie wymagają niczego z tej instrukcji |

**Kalendarz a produkcja.** Ten wiersz mówił kiedyś „zostań w trybie testowym
i pogódź się z logowaniem co tydzień". To było zbyt ostrożne: siedmiodniowe wygasanie
żetonu jest cechą **trybu testowego**, a nie uprawnień. W produkcji żeton nie wygasa
również wtedy, gdy aplikacja nie przeszła weryfikacji — ceną jest jednorazowy ekran
„Google nie zweryfikował tej aplikacji", przez który przechodzi się przez
**Zaawansowane**, oraz limit stu użytkowników. Przy aplikacji dla jednej rodziny to
nie jest ograniczenie.

Jest to odwracalne: gdyby Google zablokowało uprawnienia kalendarza w produkcji bez
weryfikacji, wracasz do trybu testowego i nic nie tracisz. Sprawdź po tygodniu, czy
nadal jesteś zalogowana — to jest jedyny rzetelny dowód, że zadziałało.

### Dlaczego akurat `drive.file`

To uprawnienie daje dostęp **wyłącznie do plików założonych przez tę aplikację** —
nie widzi reszty Twojego Dysku. Dwie konsekwencje, obie na korzyść:

- Google nie wymaga przeglądu, więc nie wpadasz w tryb, w którym trzeba przechodzić
  weryfikację albo mieścić się w limicie stu użytkowników testowych bezterminowo.
- Gdyby coś poszło nie tak, najgorsze, co aplikacja może zrobić, to zepsuć własne
  pliki. Zdjęcia i dokumenty są poza jej zasięgiem.

„Aplikacja" to identyfikator klienta OAuth, a nie instalacja. Komputer i telefon
z tym samym identyfikatorem i tym samym kontem widzą nawzajem swoje porcje —
i o to chodzi.

## Krok 4 — poświadczenia

1. **Interfejsy API i usługi** → **Dane logowania** → **Utwórz dane logowania** →
   **Identyfikator klienta OAuth**.
2. Typ aplikacji: **Aplikacja komputerowa**.
3. Nazwa: `Marshal — komputer`.
4. Skopiuj **identyfikator klienta** i **tajemnicę klienta**.

Tajemnica klienta w aplikacji instalowanej u użytkownika **nie jest tajemnicą** —
da się ją wyciągnąć z pliku. Google o tym wie i dlatego dla tego typu aplikacji
nie traktuje jej jako zabezpieczenia; chroni Cię zgoda w przeglądarce, nie ona.
Mimo to nie wkładaj jej do repozytorium: nie dlatego, że coś kryje, tylko dlatego,
że cudze użycie obciąża Twój limit zapytań.

## Krok 4b — kalendarz (opcjonalnie)

Jeśli chcesz widzieć wydarzenia z Google Calendar na siatce godzinowej:

1. **Biblioteka** → `Google Calendar API` → **Włącz**.
2. W aplikacji: **Ustawienia → Konto Google** → zaznacz **„Czytaj i zmieniaj mój
   kalendarz Google"** i kliknij **Zapisz i zsynchronizuj**. Bez tego pola aplikacja
   o kalendarz **nie prosi wcale** — samo włączenie API w konsoli niczego nie daje.
3. Dalej w **Ustawieniach**, sekcja **Kalendarze**: **Dodaj kalendarz Google**
   z identyfikatorem `primary` (Twój główny) albo z identyfikatorem wklejonym
   z ustawień konkretnego kalendarza Google.

Zgoda z kalendarzem jest zapisywana osobno od zgody na sam Dysk, więc po zaznaczeniu
tego pola przeglądarka otworzy się jeszcze raz — i to jest poprawne, a nie usterka.

Uprawnienia do kalendarza **są** wrażliwe — inaczej niż `drive.file`. Aplikacja prosi
o dwa i o żadne więcej:

- `calendar.readonly` — wypisanie kalendarzy konta (samych wydarzeń to nie obejmuje);
- `calendar.events` — czytanie i zmienianie wydarzeń.

Pełnego `calendar` **nie** ma, bo dokładałoby prawo do zmiany ustawień i udostępniania
kalendarzy, czego ta aplikacja nie robi i nie ma powodu móc.

Cena jest konkretna: aplikacji z tymi uprawnieniami **nie da się opublikować bez
przeglądu** Google, więc trzeba zostać w trybie testowym, a tam żeton wygasa co siedem
dni. Dlatego kalendarz jest osobnym polem wyboru, a nie częścią logowania — decyzja
„wygoda kalendarza za cotygodniowe logowanie" jest Twoja, nie aplikacji.

Jeśli chodzi o kalendarze przedszkola albo zajęć, kanał `.ics` daje to samo na siatce
godzinowej i nie kosztuje nic.

Aplikacja **czyta i zapisuje** wydarzenia. Zadanie z dniem i godziną trafia na
kalendarz główny wskazany w **Ustawieniach → Kalendarze**, a udostępnienie przenosi
je na wybrany kalendarz współdzielony.

Zapis dotyczy **wyłącznie wydarzeń założonych przez Marshala** — tych, które mają
u nas swoje zadanie. Cudzych wydarzeń aplikacja nie zmienia i nie kasuje; odhaczenie
takiego dopisuje ptaszek do jego nazwy i na tym poprzestaje. To jest jedyne miejsce
w całym projekcie, w którym awaria sięga poza aplikację, więc granica jest tu wąska
celowo.

### Kalendarze z drugiego konta

Są na to dwie drogi i **pierwsza jest zwykle lepsza**.

#### Droga pierwsza: udostępnienie w Google

**Udostępnij kalendarz z drugiego konta temu, którym logujesz się w Marshalu.**
W kalendarzu Google: ustawienia tego kalendarza → *Udostępnij określonym osobom lub
grupom* → dodaj drugi adres → uprawnienie **Wprowadzanie zmian w wydarzeniach**, jeśli
chcesz z Marshala także zapisywać.

Od tej chwili kalendarz pojawia się na liście konta, którym się logujesz, i w Marshalu
jest zwykłym kalendarzem do podłączenia — z własną barwą i własnym obszarem.

Dlaczego lepsza: nic nie trzeba robić po raz drugi na telefonie, zgoda jest jedna,
a uprawnienia widać w jednym miejscu — w Google.

#### Droga druga: drugie konto w Marshalu

Gdy udostępnianie jest zablokowane — konta firmowe Google Workspace bywają zamknięte
poza domenę — **Ustawienia → Kalendarze → Konta Google → „Dodaj konto Google"**.
Otwiera się zwykłe okno zgody; zaloguj się tam na **drugie** konto.

Co warto wiedzieć:

- Drugie konto wnosi **wyłącznie swoje kalendarze**. Dysk z dziennikiem synchronizacji
  zostaje przy koncie głównym i to się nie zmienia — dlatego ta zgoda prosi o mniej niż
  główna: o sam kalendarz, bez Dysku.
- **Zgoda leży na urządzeniu.** Podłączony kalendarz dojedzie na telefon sam, ale zgody
  z nim nie ma — na telefonie trzeba kliknąć „Dodaj konto Google" jeszcze raz. Dopóki
  tego nie zrobisz, telefon pokaże przy tym kalendarzu zdanie o brakującej zgodzie,
  zamiast po cichu nic nie pobierać.
- Konto rozpoznawane jest **po adresie pocztowym**, bo tylko on znaczy to samo na obu
  urządzeniach. Ten sam kalendarz podłączony z dwóch różnych kont to dwa osobne
  podłączenia — i tak ma być, bo mają różne uprawnienia.
- „Odłącz konto" zdejmuje zgodę **z tego urządzenia**. Kalendarze z niego zostają na
  liście; na innym urządzeniu, gdzie zgoda dalej jest, pobierają się jak wcześniej.

#### Droga trzecia: prywatny adres `.ics`

Gdy nie ma ani udostępnienia, ani możliwości zalogowania się — zostaje **prywatny adres
`.ics`** tego kalendarza (ustawienia kalendarza → *Prywatny adres w formacie iCal*).
Marshal czyta go bez żadnych poświadczeń, ale wyłącznie do odczytu: wydarzeń z takiego
kanału nie da się zmieniać ani kasować.

### Kanały iCal — bez żadnych poświadczeń

Kalendarz przedszkola, zajęć czy szkoły zwykle udostępnia adres kończący się na `.ics`.
Taki kanał wystarczy wkleić — nie wymaga konta Google ani niczego z tej instrukcji.
Odświeża się co godzinę.

## Krok 5 — gdzie wkleić poświadczenia

W aplikacji: **Ustawienia → Konto Google — synchronizacja**. Dwa pola, identyfikator
klienta i tajemnica, potem **Zapisz i zsynchronizuj**.

Nigdzie indziej. W szczególności **nie do repozytorium i nie do żadnego pliku obok
kodu** — nie dlatego, że tajemnica klienta coś kryje (w aplikacji instalowanej
u użytkownika nie jest tajemnicą, zob. krok 4), tylko dlatego, że cudze użycie
obciąża Twój limit zapytań.

Poświadczenia lądują w bazie tego urządzenia, w tabeli ustawień lokalnych, i **nie
jadą przez synchronizację**. Poświadczenia do Dysku nie mają jechać przez Dysk;
na drugim urządzeniu wkleja się je jeszcze raz.

Przy pierwszym **Zapisz i zsynchronizuj** otworzy się przeglądarka i poprosi o zgodę.
Zgadzasz się raz — odświeżalny żeton zostaje w danych aplikacji i kolejne przebiegi
nie pytają. W trybie testowym „raz" znaczy „raz na tydzień" — zob. krok 3. Ścieżkę do katalogu z żetonem widać pod przyciskiem; skasowanie go cofa
do stanu sprzed logowania.

Na Dysku pojawi się katalog `Marshal`, a w nim pliki `{urządzenie}.{porcja}.jsonl`.
Można je otworzyć notatnikiem — to zwykły tekst, jeden wiersz na zapis.

### Dlaczego ręcznie, a nie w tle

Pierwsze przebiegi na żywym koncie mają być wywołane świadomie i mieć widoczny wynik.
Synchronizacja uruchamiana po cichu przy starcie znaczy, że pierwszy błąd zobaczysz
jako **brakujące zadania**, a nie jako komunikat — a przy synchronizacji to jest
najgorszy możliwy sposób dowiadywania się o problemie. Automat dochodzi wtedy, gdy
wiadomo, że droga działa.

### Gdy coś nie zadziała

Komunikat pod przyciskiem jest treścią błędu od Google, nie naszym „coś poszło nie tak".
Trzy najczęstsze przy pierwszym podejściu:

- **„Dostęp zablokowany: aplikacja nie przeszła weryfikacji", błąd 403
  `access_denied`** — Twojego adresu nie ma na liście użytkowników testowych, albo
  aplikacja nie jest opublikowana (krok 3, punkt 5 i akapit o trybach);
- **zły identyfikator klienta** — najczęściej spacja albo koniec wiersza wklejony
  razem z tekstem; aplikacja przycina jedno i drugie, więc jeśli to nadal wychodzi,
  poświadczenia są z innego projektu;
- **odmowa dostępu** — poświadczenia są typu innego niż **Aplikacja komputerowa**.

## Krok 6 — telefon

**W konsoli Google nie ma tu nic do zrobienia.** Żadnych nowych poświadczeń, żadnego
typu „Android", żadnego odcisku podpisu APK. Telefon używa **tych samych** poświadczeń
typu „aplikacja komputerowa", które masz z kroku 4, tego samego ekranu zgody i tej
samej listy użytkowników testowych.

W aplikacji na telefonie: **Ustawienia → Konto Google — synchronizacja** → wklej ten
sam identyfikator klienta i tę samą tajemnicę → **Zapisz i zsynchronizuj**. Otworzy się
przeglądarka telefonu i poprosi o zgodę.

**Po potwierdzeniu zgody przeglądarka zwykle nie wraca do aplikacji sama** — zostaje na
stronie, która się nie wczytuje. Wtedy: przytrzymaj pasek adresu, **Kopiuj**, wróć do
Marshala i wklej adres w pole **„Przeglądarka nie wróciła sama?"**, które pokazuje się
na czas logowania. Kod zgody jest w tym adresie w całości, więc logowanie kończy się
tak samo, jakby przeglądarka wróciła.

Sprawdzone na Androidzie 15: powrót sam nie nastąpił ani razu, wklejenie zadziałało
za pierwszym razem.

Poświadczenia wkleja się **na każdym urządzeniu osobno**, bo leżą w tabeli ustawień
lokalnych i nie jadą przez synchronizację (krok 5). Poświadczenia do Dysku nie mają
jechać przez Dysk.

### Dlaczego telefon nie potrzebuje własnych poświadczeń

Zwykła droga logowania na Androidzie to poświadczenia typu **Android** z odciskiem
podpisu APK, własny schemat adresu powrotu i podpisywanie każdego wydania tym samym
kluczem. Marshal idzie inaczej, bo nie musi: biblioteka Google wraca ze zgodą **na port
pętli zwrotnej**, a przeglądarka telefonu sięga do pętli zwrotnej tego samego telefonu.
Google pozwala klientom typu komputerowego wracać na dowolny port pętli zwrotnej i nie
wymaga zgłaszania go z góry.

Wymienione jest więc tylko otwieranie przeglądarki — na Androidzie otwiera się zamiar,
a nie proces. Nasłuch zostaje ten sam.

### Dlaczego przeglądarka nie wraca sama

Objaw jest charakterystyczny: strona **wisi**, a nie pokazuje odmowy połączenia. Gdyby
nikt nie nasłuchiwał na porcie, odmowa przyszłaby natychmiast. Czekanie znaczy, że
połączenie zostało przyjęte przez jądro, ale nikt go nie obsłużył — czyli proces
aplikacji jest uśpiony. Android odkłada do zamrażarki procesy, które zeszły w tło,
a przeglądarka schodzi z aplikacji dokładnie w chwili, w której ma ona zacząć czekać.

Utrzymanie procesu przy życiu wymagałoby usługi pierwszoplanowej, czyli stałego
powiadomienia „Marshal działa" i osobnego uprawnienia. Wklejenie adresu kosztuje mniej
i nie zależy od tego, jak system akurat gospodaruje pamięcią.

Cena drugiego wyboru: gdyby Google kiedyś przestało pozwalać klientom komputerowym na
pętlę zwrotną z telefonu, trzeba będzie założyć poświadczenia typu Android i podpisywać
wydania stałym kluczem. To jest praca do zrobienia wtedy, a nie zapas na wszelki wypadek.

### Co telefon robi sam

Ten akapit mówił kiedyś, że przy zamkniętej aplikacji nie dzieje się nic. Dziś dzieje
się to:

- **Synchronizacja przy otwartej aplikacji**: wysyłka pięć sekund po zapisie, odczyt
  co minutę i przy powrocie do okna.
- **Synchronizacja przy zamkniętej**: co pół godziny, robotą w tle (WorkManager).
- **Przypomnienia**: budzik systemowy, także przy zamkniętej aplikacji.
- **Pobranie kalendarzy**: co pięć minut, przyrostowo.

Zostaje jedno ograniczenie i wynika z oszczędzania baterii przez Androida: przy
zamkniętej aplikacji synchronizacja chodzi co pół godziny, a nie co minutę. Zmiana
zrobiona na komputerze dociera więc na uśpiony telefon z opóźnieniem — chyba że go
odblokujesz i otworzysz Marshala, bo wtedy odczyt idzie od razu.

## Czego jeszcze nie ma

- **Sprawdzenie na żywym koncie.** Cała logika składnicy — nazewnictwo, porządek,
  odsiewanie duplikatów, odmowa nadpisania — jest pokryta testami na udawanym Dysku.
  Nie sprawdzone jest samo wołanie API, bo do tego trzeba poświadczeń.
- **Sprawdzenie kalendarza na żywym koncie.** Rozbiór plików iCal ma testy na treści
  wpisanej wprost, a kopiowanie wydarzeń do bazy — testy na udawanym kanale.
  Niesprawdzone zostaje samo wołanie API Google i pobieranie po sieci.
- **Scalanie porcji.** Dziennik rośnie w nieskończoność. Docelowo stare porcje
  zwijają się w jedną migawkę (spec 9.5). Przy tempie kilkuset zmian dziennie to
  problem na rok, nie na teraz.

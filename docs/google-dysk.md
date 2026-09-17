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
5. Użytkownicy testowi: dodaj **swój adres Gmail**. Bez tego logowanie odbije się
   komunikatem o niezweryfikowanej aplikacji.

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
2. Do ekranu zgody **nie dodawaj** nic ręcznie. Aplikacja prosi dodatkowo o
   `.../auth/calendar.readonly`.

`calendar.readonly` **jest** uprawnieniem wrażliwym — inaczej niż `drive.file`. Przy
aplikacji w trybie testowym z Twoim adresem na liście działa bez przeszkód; przegląd
Google byłby potrzebny dopiero przy udostępnianiu jej innym ludziom.

Aplikacja **tylko czyta** kalendarz. Zapis jest świadomie odłożony (spec 10.2): błąd
w dwustronnej synchronizacji potrafi skasować prawdziwe wydarzenia i jest to jedyne
miejsce w całym projekcie, gdzie awaria niszczy dane poza aplikacją.

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
nie pytają. Ścieżkę do katalogu z żetonem widać pod przyciskiem; skasowanie go cofa
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

- **niezweryfikowana aplikacja** — Twojego adresu nie ma na liście użytkowników
  testowych (krok 3, punkt 5);
- **zły identyfikator klienta** — najczęściej spacja albo koniec wiersza wklejony
  razem z tekstem; aplikacja przycina jedno i drugie, więc jeśli to nadal wychodzi,
  poświadczenia są z innego projektu;
- **odmowa dostępu** — poświadczenia są typu innego niż **Aplikacja komputerowa**.

## Czego jeszcze nie ma

- **Logowanie na Androidzie.** Droga z kroku 5 otwiera przeglądarkę i nasłuchuje na
  porcie pętli zwrotnej; na Androidzie nie ma ani jednego, ani drugiego. Potrzebne
  są osobne poświadczenia typu **Android** (z odciskiem podpisu APK) i powrót przez
  własny schemat adresu. Sama składnica i klient Dysku zostają bez zmian.
- **Sprawdzenie na żywym koncie.** Cała logika składnicy — nazewnictwo, porządek,
  odsiewanie duplikatów, odmowa nadpisania — jest pokryta testami na udawanym Dysku.
  Nie sprawdzone jest samo wołanie API, bo do tego trzeba poświadczeń.
- **Sprawdzenie kalendarza na żywym koncie.** Rozbiór plików iCal ma testy na treści
  wpisanej wprost, a kopiowanie wydarzeń do bazy — testy na udawanym kanale.
  Niesprawdzone zostaje samo wołanie API Google i pobieranie po sieci.
- **Scalanie porcji.** Dziennik rośnie w nieskończoność. Docelowo stare porcje
  zwijają się w jedną migawkę (spec 9.5). Przy tempie kilkuset zmian dziennie to
  problem na rok, nie na teraz.

# Polityka prywatności — Marshal

Obowiązuje od 18 września 2026.

## W jednym zdaniu

Marshal nie ma serwera, nie zbiera niczego i nie wysyła niczego do autora aplikacji.
Wszystkie dane zostają na twoim urządzeniu, a jeśli włączysz synchronizację — na
twoim koncie Google, do którego dostęp masz tylko ty.

## Kto jest administratorem danych

**Ty.** Marshal jest aplikacją działającą wyłącznie na twoim urządzeniu i na twoim
koncie Google. Autor aplikacji nie prowadzi żadnej usługi, nie ma dostępu do twoich
danych i nie jest w stanie ich odczytać — nawet gdyby chciał, nie ma dokąd sięgnąć.

## Co aplikacja przechowuje i gdzie

| Co | Gdzie | Kto ma dostęp |
|----|-------|---------------|
| Zadania, projekty, obszary, notatki, filtry | Plik SQLite na twoim urządzeniu | Ty |
| Załączniki | Katalog aplikacji na twoim urządzeniu | Ty |
| Dziennik działań (diagnostyka) | Ten sam plik SQLite, ostatnie 500 wpisów | Ty |
| Poświadczenia Google (identyfikator i sekret klienta) | Ustawienia lokalne urządzenia | Ty |
| Żeton odświeżania Google | DPAPI (Windows), EncryptedSharedPreferences (Android) | Ty |
| Porcje zmian do synchronizacji | Folder aplikacji na **twoim** Dysku Google | Ty |
| Kopia zapasowa | Plik JSON tam, gdzie go zapiszesz | Ty |

Dziennik działań i poświadczenia są **lokalne** — nie wchodzą ani do synchronizacji,
ani do kopii zapasowej.

## Z czym aplikacja łączy się przez sieć

Marshal łączy się wyłącznie z usługami, które sama włączysz:

- **Dysk Google** (`drive.file`) — tylko po to, żeby zapisać i odczytać własne pliki
  synchronizacji. To uprawnienie daje dostęp **wyłącznie do plików utworzonych przez
  aplikację**; reszty twojego Dysku Marshal nie widzi.
- **Kalendarz Google** (`calendar.readonly`, `calendar.events`) — odczyt listy twoich
  kalendarzy oraz odczyt i zmiana wydarzeń w tych, które podłączysz. Wyłącznie wtedy,
  gdy włączysz kalendarz w ustawieniach.
- **Kanały iCal** — pobranie pliku spod adresu, który sam wpiszesz.

Nie ma żadnych innych połączeń. Brak analityki, brak telemetrii, brak raportowania
awarii, brak reklam, brak zewnętrznych bibliotek śledzących.

## Twoje własne poświadczenia Google

Marshal nie ma wspólnego dla wszystkich identyfikatora klienta OAuth. Zakładasz własny
projekt w Google Cloud i wklejasz jego poświadczenia do aplikacji
(zob. [`google-dysk.md`](google-dysk.md)). Znaczy to, że **twoja zgoda Google dotyczy
projektu należącego do ciebie**, a nie do autora aplikacji, i że autor nie figuruje
nigdzie w tej relacji.

## Komu dane są udostępniane

Nikomu — poza tym, co udostępnisz sama:

- Wydarzenie, któremu dopiszesz gościa, staje się widoczne dla tej osoby — tak działa
  kalendarz Google. Adresy osób z twojej listy są przechowywane u ciebie i wysyłane do
  Google wyłącznie w chwili dopisania gościa.
- Kalendarz udostępniony w Google widzą osoby, którym go udostępniłaś. Zadania z obszaru
  przypisanego do takiego kalendarza będą dla nich widoczne.

Autor aplikacji nie otrzymuje niczego.

## Dzieci

Aplikacja nie jest kierowana do dzieci i nie zbiera żadnych danych — także ich.

## Usunięcie danych

Odinstalowanie aplikacji usuwa bazę i załączniki z urządzenia. Pliki synchronizacji
usuwasz z Dysku Google tak jak każde inne. Zgodę OAuth odbierasz w ustawieniach konta
Google. Nie trzeba się z nikim kontaktować, bo nie ma kogo o to prosić.

## Zmiany

Ta polityka mieszka w repozytorium razem z kodem, więc jej historia jest jawna
i sprawdzalna — każda zmiana jest osobnym commitem.

## Kontakt

Sprawy zgłasza się przez zgłoszenia w repozytorium:
<https://github.com/kodalus/Marshal/issues>.

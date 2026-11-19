# Marshal — zasady pracy

Aplikacja GTD: kalendarz i zadania, Windows i Android, jeden zestaw widoków
Avalonia na obie platformy. .NET 10, EF Core 10 + SQLite, xUnit + FluentAssertions.
Dane są lokalne; synchronizacja idzie przez dziennik zmian na Dysku Google.

Ten plik zawiera to, czego nie widać z samego kodu, a czego złamanie kosztuje
przebieg CI albo cudzy dzień. Reguły są krótkie; powód stoi przy tych, przy
których sam powód powstrzymuje pomyłkę.

## Odpowiedzi

**Pierwsza linijka każdej odpowiedzi, w której dotykałeś repozytorium, mówi
o stanie zapisu — także wtedy, gdy nic nie zapisałeś.** Cisza o tym jest gorsza
od nadmiaru: nie widać po niej różnicy między „zrobione i wysłane" a „leży
niezapisane".

> **Zapisane i wysłane:** `a1277b3` — CI 476 zielone.
> **Nic do zapisania** — drzewo czyste, `main` = `origin/main` na `a1277b3`.
> **Zapisane i wysłane:** `abc1234` — CI jeszcze leci, dam znać.

## Język

Po polsku **wszystko**: teksty widoczne dla użytkownika, komentarze, dokumentacja
XML, opisy zapisów, nazwy w dzienniku działań. Nazwy w kodzie zostają angielskie.

Cudzysłów otwierający „ zamyka się znakiem ” (U+201D), nigdy prostym. Prosty
kończy napis w połowie, a kompilator wywala się kilkanaście znaków dalej,
w miejscu bez związku z przyczyną. Pilnuje tego `tools/check-quotes.sh`.

Nazwy testów bez polskich znaków diakrytycznych — taka jest konwencja w całym
`tests/`.

## Zapisy

Praca na `main`. **Bez pull requestów, dopóki autorka nie poprosi.**

Opis zapisu kończy się stopką `Co-Authored-By:` i `Claude-Session:` w postaci
podanej przez środowisko sesji.

Opis mówi, **co było zepsute i dlaczego tak wyszło**, a nie co zostało zmienione
— to drugie widać z różnicy. Ten projekt trzyma w opisach zapisów swoją pamięć
o usterkach i to jest celowe.

## Repozytorium jest publiczne

Nie trafiają tu: poświadczenia Google, klucz podpisujący Androida (jest wyłącznie
w sekretach GitHub Actions), żadne dane osobowe autorki ani jej rodziny.
W przykładach w dokumentacji używaj rzeczy neutralnych.

## Nie ma lokalnego kompilatora

W kontenerze **nie ma .NET SDK**. Jedynym sprawdzeniem jest przebieg CI, około
trzy i pół minuty. Z tego wynika sposób pracy:

- Przed wysłaniem puść wszystkich strażników: `tools/check-quotes.sh`,
  `check-xaml.sh`, `check-aliases.sh`, `check-icons.sh`, `check-gitignore.sh`.
  Chodzą w CI przed budowaniem, więc złapane u siebie oszczędzają całą rundę.
- `TreatWarningsAsErrors` jest włączone. Ostrzeżenie to błąd.
- **Każdą nową nazwę zmiennej i parametru sprawdź wobec nazw już widocznych
  w tym zakresie.** Kolizja (CS0136) albo nieużyty parametr konstruktora
  pierwotnego (CS9113) kosztuje pełny przebieg za literówkę; zdarzyło się to trzy
  razy w jednej sesji.
- Dokumentacja XML na parametrze konstruktora pierwotnego **klasy** to CS1587.
  Przy rekordach jest legalna — przy klasach używaj zwykłego `//`.

## Czego CI nie sprawdzi

Kodu widoku sprawdza tylko kompilację. Zachowania okna — przeciągania, wiązań,
tego, co kwadracik naprawdę pokazuje — nie ma w tym projekcie jak przebiec
testem. **Gdy poprawka siedzi w widoku, powiedz wprost, że jest rozumowana,
a nie zweryfikowana**, i napisz, co autorka ma sprawdzić na sprzęcie.

Tak samo: CI chodzi na Linuksie, a aplikacja na Windowsie i Androidzie. Usterka
zależna od platformy przejdzie CI bez mrugnięcia — tak było z wyłączonym
`InvariantGlobalization`.

## Migracje

Generowane **na zamówienie**: nazwa w `tools/migracja.txt`, wysłanie, CI zostawia
wynik na gałęzi `migracja-wygenerowana`. Zob. `tools/migracja.md`.

`LocalSettings` to tabela klucz–wartość, więc **nowe ustawienie nie wymaga
migracji**. Ustawienia są urządzeniowe i celowo niesynchronizowane.

## Czego nie łamać

Trzy rzeczy trzymają spójność między urządzeniami. Zmiana którejkolwiek bez
zrozumienia skutków podwaja serie albo gubi dni:

- **Okno serii jest czystą funkcją serii i dzisiejszej daty**
  (`SeriesWindow.Plan`). Dwa urządzenia liczą to samo bez umawiania się.
- **Tożsamość wystąpienia jest wyliczana, nie losowana**
  (`OccurrenceId.For(seria, dzień)`). Losowana rozjeżdżała serie na dwa łańcuchy.
- **Scalanie idzie po polach, nie po wierszach** — stempel HLC na każde pole
  osobno (`ChangeJournalInterceptor`, `FieldStamp`).

Nagrobek na dniu **minionym** jest nieodwracalny: okno nigdy nie planuje dni
przed dzisiejszym, więc taki dzień nie wraca ani sam, ani przyciskiem. Wszystko,
co zabiera przeszły dzień, wymaga pewności, a nie domysłu.

## Czego nie robić bez pytania

- Nie usuwać ani nie wyłączać testów, żeby przejść CI.
- Nie poprawiać przy okazji rzeczy niezwiązanych ze zleconą zmianą — zgłoś je.
- Nie wyłączać sprawdzania TLS i nie ruszać ustawień pośrednika sieciowego.

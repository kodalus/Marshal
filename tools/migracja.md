# Jak dodać migrację bazy

Do wygenerowania migracji trzeba SDK i narzędzi EF. Praca nad tym projektem odbywa
się tam, gdzie ich nie ma, więc robi to CI — na zamówienie.

Trzy pliki na migrację (sama migracja, jej projekt i odbicie całego modelu, razem
około dwóch tysięcy wierszy mechanicznego kodu) przepisane z ręki to nie oszczędność,
tylko zaproszenie do pomyłki, której nie widać w przeglądzie: rozjazd odbicia modelu
z encjami wychodzi dopiero po danych.

## Zamówienie

1. Zmień encje i ich konfiguracje.
2. Wpisz nazwę migracji do `tools/migracja.txt` — jedno słowo, po polsku, bez spacji.
3. Wypchnij do `main`.

CI wygeneruje migrację i przegeneruje skompilowany model, a wynik odłoży na gałąź
`migracja-wygenerowana`. Ten przebieg **zapali się** na kroku „Model skompilowany
zgodny z encjami" — i tak ma być: w repozytorium leży jeszcze stary.

## Wciągnięcie

```sh
git fetch origin migracja-wygenerowana
git checkout origin/migracja-wygenerowana -- src/Marshal.Infrastructure/Data/Migrations
git checkout origin/migracja-wygenerowana -- src/Marshal.Infrastructure/Data/Compiled
git rm tools/migracja.txt
git commit -m "Migracja <nazwa> z CI"
```

Plik z zamówieniem **musi** zniknąć razem z wciągnięciem. Zostawiony każe CI
generować drugą, pustą migrację przy następnym wypchnięciu.

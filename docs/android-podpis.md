# Podpisywanie APK stałym kluczem

Instrukcja jednorazowa. Po jej wykonaniu kolejne wydania **nadpisują** aplikację
na telefonie zamiast odmawiać instalacji.

## Po co

Android wiąże zainstalowaną aplikację z kluczem, którym została podpisana, i nie
pozwala nadpisać jej pakietem podpisanym innym. Odmowa brzmi „Aplikacja nie została
zainstalowana, bo powoduje konflikt z istniejącym pakietem" i nie mówi, o co chodzi.

Budowanie w CI podpisuje APK **kluczem domyślnym**, a bieżnia startuje za każdym razem
od zera — więc każdy przebieg daje inny klucz. Stąd konflikt przy każdej aktualizacji
i konieczność odinstalowania, czyli skasowania bazy na telefonie.

Klucz nie może leżeć w repozytorium, bo jest publiczne. Kto ma klucz, ten może wypuścić
„aktualizację Marshala" dla każdego, kto ma tę aplikację. Miejscem na niego są sekrety
repozytorium.

## Krok 1 — zrób klucz

Potrzebne jest `keytool` z zestawu Javy. Jeśli go nie ma:
`winget install Microsoft.OpenJDK.17`.

W katalogu **poza repozytorium** (np. w dokumentach). Jedna linijka, bo znak łamania
wiersza jest inny w PowerShellu (\`) niż w wierszu poleceń (^), a pomylony daje błąd,
z którego nie wynika, o co chodzi:

```
keytool -genkeypair -v -keystore marshal.keystore -alias marshal -keyalg RSA -keysize 4096 -validity 10000 -dname "CN=Marshal, O=Kodalus, C=PL"
```

Zapyta o hasło do magazynu, a potem o hasło do klucza — **podaj to samo**, bo tak
wygląda dalsza konfiguracja. Zapisz je w menedżerze haseł.

**Ten plik jest nie do odzyskania.** Zgubiony znaczy, że kolejnych aktualizacji nie da
się już nadpisać na żadnym telefonie, który ma Marshala — wszystkie wymagałyby
odinstalowania. Skopiuj go tam, gdzie trzymasz rzeczy, których nie wolno stracić.

## Krok 2 — zamień na tekst

Sekrety GitHuba przyjmują tekst, nie pliki. W PowerShellu:

```
[Convert]::ToBase64String([IO.File]::ReadAllBytes("marshal.keystore")) | Set-Clipboard
```

## Krok 3 — wstaw do repozytorium

GitHub → repozytorium **Marshal** → **Settings** → **Secrets and variables** →
**Actions** → **New repository secret**. Dwa wpisy:

| Nazwa | Wartość |
|---|---|
| `ANDROID_KEYSTORE` | to, co w schowku z kroku 2 |
| `ANDROID_KEYSTORE_PASSWORD` | hasło z kroku 1 |

Nazwa aliasu (`marshal`) jest wpisana w przebiegu na stałe — nie jest tajemnicą.

## Krok 4 — ostatnia deinstalacja

Aplikacja, którą masz teraz na telefonie, jest podpisana starym kluczem domyślnym,
więc pierwsze wydanie podpisane nowym **też** się nie nadpisze. Tej jednej deinstalacji
nie da się uniknąć.

Przed nią: **zsynchronizuj telefon** (Ustawienia → Zapisz i zsynchronizuj). Dziennik
zmian leży wtedy na Dysku i po ponownej instalacji wraca przy pierwszym logowaniu.
Wraca też kopia zapasowa, jeśli ją robisz.

Co trzeba wpisać ponownie po instalacji: identyfikator klienta i tajemnica Google —
poświadczenia leżą w ustawieniach lokalnych i świadomie nie jadą przez synchronizację.

Od następnego wydania aktualizacje nadpisują się już bez pytania.

## Gdyby przebieg został bez sekretu

Budowanie nie przestaje działać: APK powstaje jak dotąd, z kluczem domyślnym, a przebieg
zostawia ostrzeżenie. Nic się nie psuje — po prostu wraca konflikt przy instalacji.


## Skąd pobierać APK

Dwa miejsca z tym samym plikiem. **Właściwe jest wydanie**, nie artefakt przebiegu.

**Wydanie „najnowsza"**. Plik leży pod adresem, który się nie zmienia między
budowaniami:

```
https://github.com/kodalus/Marshal/releases/download/najnowsza/marshal.apk
```

Pojedynczy plik APK, bez logowania i bez archiwum. Otwierasz ten adres na telefonie,
pobierasz, instalujesz. Komputer nie bierze w tym udziału. Strona wydania ze SHA-256
i numerem budowania: `https://github.com/kodalus/Marshal/releases/tag/najnowsza`.

**Jeden pakiet, nie dwa.** Publikacja zostawia obok siebie pakiet podpisany
i niepodpisany; przebieg wybiera podpisany i nadaje mu stałą nazwę. Wcześniej
w archiwum były oba, czyli pytanie „który zainstalować" zadane komuś, kto nie ma jak
na nie odpowiedzieć — a niepodpisany i tak by się nie zainstalował.

**Artefakt przebiegu** (Actions → wybrany przebieg → `marshal-apk`) zostaje jako zapas
i do budowań spoza gałęzi głównej. Przychodzi zapakowany w ZIP i tylko po zalogowaniu,
więc trzeba go pobrać na komputerze i przełożyć na telefon.

### „Ten plik zawiera wirusa" przy pobieraniu ZIP-a

Windows blokuje wtedy plik **po reputacji, nie po zawartości**: widzi świeżo zbudowany,
nigdy wcześniej niewidziany plik wykonywalny w archiwum, bez podpisu uznawanego przez
Microsoft. Nasz APK jest podpisany kluczem z sekretów repozytorium, a nie certyfikatem
kupionym u dostawcy, którego Windows zna — i to wystarczy, żeby trafić w tę blokadę.

Komunikat mówi o wirusie, ale nie znaczy, że coś znaleziono. To samo zdanie Windows
pokazuje przy prawdziwym wykryciu i przy braku zaufania, i z zewnątrz nie da się ich
rozróżnić.

Co można zrobić, w kolejności od najlepszego:

1. **Pobrać z wydania na telefonie.** Windows znika z drogi w całości.
2. **Sprawdzić sumę kontrolną.** Każdy przebieg wypisuje SHA-256 pliku w kroku „Suma
   kontrolna" i w opisie wydania. Jeśli plik na telefonie ma tę samą sumę, to jest
   dokładnie ten plik, który powstał z tego kodu — i nic po drodze go nie podmieniło.
   To jedyna rzecz, którą da się o pliku stwierdzić bez zaufania komukolwiek.
3. Wymusić pobranie w przeglądarce („Zachowaj mimo to"). Działa, ale niczego nie
   sprawdza — więc ma sens dopiero po punkcie 2.

### Wydanie jest publiczne

Repozytorium jest publiczne, więc plik z wydania może pobrać każdy, kto zna adres.
Nie jest to wyciek: kod i tak jest jawny, a APK jest podpisany kluczem, którego nie ma
nikt poza sekretami repozytorium, więc podszyć się pod aktualizację Marshala się nie da.
Gdyby jednak sama dostępność pliku zaczęła przeszkadzać, drogą jest prywatne
repozytorium albo wydania zastąpione czymś, co wymaga logowania.

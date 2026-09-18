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

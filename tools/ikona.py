#!/usr/bin/env python3
"""Znak Marshala: marshal.png i marshal.ico z jednego opisu.

Skrypt, nie plik wrzucony raz i zapomniany. Obrazek bez źródła jest w repozytorium
ciężarem: przy zmianie tonacji nie ma czego poprawić i trzeba go rysować od nowa.

Rysunek: trzy paski jeden pod drugim — lista rzeczy do zrobienia — na granatowym
polu o zaokrąglonych rogach. Środkowy w barwie wyróżnienia, bo to ten, którym się
teraz zajmujemy. Bez PIL-a, bo zależność ściągana po to, żeby raz narysować
trzy prostokąty, kosztuje więcej niż sto linijek tutaj.

Wygładzanie przez rysowanie w czterokrotnej skali i uśrednienie — inaczej paski
mają schodkowe rogi w każdym rozmiarze poniżej stu punktów.
"""

import struct
import zlib
from pathlib import Path

TLO = (0x12, 0x17, 0x29)
PASEK = (0x4A, 0x55, 0x80)
WYROZNIONY = (0x7C, 0x6C, 0xF5)

SKALA = 4
ROZMIARY = [16, 24, 32, 48, 64, 128, 256]
DOCELOWY = Path(__file__).resolve().parent.parent / "src/Marshal.UI/Assets"


def zaokraglony(plotno, bok, x0, y0, x1, y1, promien, barwa):
    """Prostokąt o zaokrąglonych rogach, wpisany w płótno bok×bok."""
    for y in range(max(0, int(y0)), min(bok, int(y1) + 1)):
        for x in range(max(0, int(x0)), min(bok, int(x1) + 1)):
            # Poza narożnikami wystarczy prostokąt; w narożniku liczy się odległość
            # od środka łuku, bo inaczej róg jest ścięty po skosie, a nie okrągły.
            sx = x0 + promien if x < x0 + promien else (x1 - promien if x > x1 - promien else x)
            sy = y0 + promien if y < y0 + promien else (y1 - promien if y > y1 - promien else y)

            if (x - sx) ** 2 + (y - sy) ** 2 <= promien ** 2:
                plotno[y * bok + x] = barwa


def narysuj(bok):
    """Znak w podanym boku, jako lista pikseli RGBA."""
    dokladny = bok * SKALA
    plotno = [(0, 0, 0, 0)] * (dokladny * dokladny)

    zaokraglony(plotno, dokladny, 0, 0, dokladny - 1, dokladny - 1,
                dokladny * 0.22, TLO + (255,))

    # Trzy paski: równa wysokość, równe odstępy, szerokość wyznaczona marginesem.
    margines = dokladny * 0.22
    wysokosc = dokladny * 0.095
    odstep = dokladny * 0.085
    caloscPaskow = 3 * wysokosc + 2 * odstep
    gora = (dokladny - caloscPaskow) / 2

    for numer in range(3):
        y = gora + numer * (wysokosc + odstep)
        barwa = WYROZNIONY if numer == 1 else PASEK
        # Środkowy jest krótszy: równe trzy paski to szlaczek, a nie znak.
        prawa = dokladny - margines if numer != 1 else dokladny - margines * 1.55
        zaokraglony(plotno, dokladny, margines, y, prawa, y + wysokosc,
                    wysokosc / 2, barwa + (255,))

    return zmniejsz(plotno, dokladny, bok)


def zmniejsz(plotno, dokladny, bok):
    """Uśrednienie kwadratów SKALA×SKALA — stąd gładkie krawędzie."""
    wynik = []
    for y in range(bok):
        for x in range(bok):
            r = g = b = a = 0
            for dy in range(SKALA):
                for dx in range(SKALA):
                    piksel = plotno[(y * SKALA + dy) * dokladny + x * SKALA + dx]
                    r += piksel[0] * piksel[3]
                    g += piksel[1] * piksel[3]
                    b += piksel[2] * piksel[3]
                    a += piksel[3]

            if a == 0:
                wynik.append((0, 0, 0, 0))
            else:
                ile = SKALA * SKALA
                wynik.append((r // a, g // a, b // a, a // ile))

    return wynik


def png(piksele, bok):
    surowe = bytearray()
    for y in range(bok):
        surowe.append(0)  # bez filtrowania — obrazek jest mały, a kod czytelniejszy
        for x in range(bok):
            surowe.extend(piksele[y * bok + x])

    def kawalek(nazwa, dane):
        return (struct.pack(">I", len(dane)) + nazwa + dane
                + struct.pack(">I", zlib.crc32(nazwa + dane) & 0xFFFFFFFF))

    return (b"\x89PNG\r\n\x1a\n"
            + kawalek(b"IHDR", struct.pack(">IIBBBBB", bok, bok, 8, 6, 0, 0, 0))
            + kawalek(b"IDAT", zlib.compress(bytes(surowe), 9))
            + kawalek(b"IEND", b""))


def ico(obrazki):
    """Ikona windowsowa z osadzonymi PNG-ami — tak samo dla każdego rozmiaru."""
    naglowek = struct.pack("<HHH", 0, 1, len(obrazki))
    poczatek = 6 + 16 * len(obrazki)
    wpisy = bytearray()
    tresc = bytearray()

    for bok, dane in obrazki:
        wpisy.extend(struct.pack("<BBBBHHII", bok % 256, bok % 256, 0, 0, 1, 32,
                                 len(dane), poczatek + len(tresc)))
        tresc.extend(dane)

    return naglowek + bytes(wpisy) + bytes(tresc)


def main():
    obrazki = [(bok, png(narysuj(bok), bok)) for bok in ROZMIARY]

    (DOCELOWY / "marshal.png").write_bytes(dict(obrazki)[256])
    (DOCELOWY / "marshal.ico").write_bytes(ico(obrazki))

    print("marshal.png i marshal.ico zapisane w", DOCELOWY)


if __name__ == "__main__":
    main()

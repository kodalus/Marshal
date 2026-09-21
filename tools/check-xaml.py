"""Czy każdy plik XAML jest poprawnym XML-em.

Powód: prosty cudzysłów wpisany w treść atrybutu **urywa wartość atrybutu** i reszta
zdania staje się nazwami właściwości. Napis „Zamień w zadanie" wpisany prosto w Text=""
kończy atrybut po słowie „zadanie" — a w polszczyźnie cudzysłów w napisie zdarza się
często. Kompilator XAML-a zgłasza to dopiero w CI, po trzech i pół minuty; tu wychodzi
od razu i bez żadnego SDK.
"""

import glob
import sys
import xml.etree.ElementTree as ET

broken = []

for path in sorted(glob.glob("src/**/*.axaml", recursive=True)):
    try:
        ET.parse(path)
    except ET.ParseError as trouble:
        broken.append((path, trouble))

if broken:
    for path, trouble in broken:
        print(f"{path}: {trouble}")

    print(f"--- zepsutych plików: {len(broken)}")
    sys.exit(1)

print("OK — pliki XAML są poprawnym XML-em.")

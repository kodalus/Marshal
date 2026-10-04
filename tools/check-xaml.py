"""Dwa sprawdzenia XAML-a, które inaczej kosztują przebieg CI.

**Poprawny XML.** Prosty cudzysłów wpisany w treść atrybutu urywa wartość atrybutu
i reszta zdania staje się nazwami właściwości. Napis „Zamień w zadanie" wpisany prosto
w Text="" kończy atrybut po słowie „zadanie" — a w polszczyźnie cudzysłów w napisie
zdarza się często.

**Podmiana kontekstu osobno od reszty wiązań.** Element, który naraz przestawia
DataContext i deklaruje x:DataType, rozstrzyga tym typem **wszystkie** swoje wiązania —
także te, które miały pytać model stojący wyżej. Wygląda to niewinnie:

    <ScrollViewer IsVisible="{Binding Habits.IsEditing}"
                  DataContext="{Binding Habits}" x:DataType="vm:HabitsViewModel">

a kompilator odpowiada „Unable to resolve property or method of name 'Habits' on type
'HabitsViewModel'", bo IsVisible pyta już wnętrza. Lekarstwo jest w całym tym oknie
to samo: widoczność na jednym elemencie, podmiana kontekstu na drugim.

Oba zgłasza kompilator XAML-a dopiero po trzech i pół minuty w CI; tutaj wychodzą
od razu i bez żadnego SDK.
"""

import glob
import re
import sys
import xml.etree.ElementTree as ET

# Wiązanie ze wskazanym źródłem pyta kogoś innego niż kontekst, więc podmiana kontekstu
# na tym samym elemencie mu nie szkodzi.
ROOTED = re.compile(r"\{\s*(Binding\s+)?(\$parent|\$self|#|RelativeSource|ElementName)")

ELEMENT = re.compile(r"<[A-Za-z][\w.:]*((?:\s+[\w.:]+\s*=\s*\"[^\"]*\")+)\s*/?>", re.S)
ATTRIBUTE = re.compile(r"([\w.:]+)\s*=\s*\"([^\"]*)\"", re.S)

broken = []
mixed = []

for path in sorted(glob.glob("src/**/*.axaml", recursive=True)):
    try:
        ET.parse(path)
    except ET.ParseError as trouble:
        broken.append((path, trouble))
        continue

    text = open(path, encoding="utf-8").read()
    line = 1
    at = 0

    for element in ELEMENT.finditer(text):
        line += text.count("\n", at, element.start())
        at = element.start()

        attributes = dict(ATTRIBUTE.findall(element.group(1)))

        if "x:DataType" not in attributes:
            continue

        if not attributes.get("DataContext", "").lstrip().startswith("{Binding"):
            continue

        for name, value in attributes.items():
            if name in ("DataContext", "x:DataType"):
                continue

            if "{Binding" in value and not ROOTED.search(value):
                mixed.append((path, line, name, value.strip()))

for path, trouble in broken:
    print(f"{path}: {trouble}")

for path, line, name, value in mixed:
    print(
        f"{path}:{line}: „{name}\" pyta wnętrza, bo ten sam element podmienia kontekst "
        f"i deklaruje x:DataType — {value}")

if broken or mixed:
    if broken:
        print(f"--- zepsutych plików: {len(broken)}")

    if mixed:
        print(f"--- wiązań po złej stronie podmiany kontekstu: {len(mixed)}")

    sys.exit(1)

print("OK — XAML poprawny, a podmiana kontekstu stoi osobno od reszty wiązań.")

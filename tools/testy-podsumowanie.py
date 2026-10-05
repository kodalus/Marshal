#!/usr/bin/env python3
"""Nieudane testy z pliku .trx — zwięźle, jako adnotacje GitHuba.

Log przebiegu ma kilkaset wierszy na jeden nieudany test: komunikat, ślad stosu
i to samo powtórzone w podsumowaniu. Przy czterystu nieudanych testach nie da się
z niego odczytać niczego bez przewijania, a przez API GitHub nie daje się go
przeszukać — wydaje go spod adresu, którego pośrednik tej sesji nie przepuszcza.

Adnotacje czyta się jednym zapytaniem i są tym, czego się szuka: nazwa testu
i jedna linijka powodu.
"""

import glob
import re
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def main() -> int:
    files = sorted(glob.glob(sys.argv[1] if len(sys.argv) > 1 else "artefakty/testy/*.trx"))

    if not files:
        print("Brak pliku .trx — nie ma czego podsumowywać.")
        return 0

    failures: list[tuple[str, str]] = []

    for path in files:
        for result in ET.parse(path).getroot().iter(f"{{{NS['t']}}}UnitTestResult"):
            if result.get("outcome") != "Failed":
                continue

            name = result.get("testName") or "(bez nazwy)"
            message = "".join(
                (node.text or "") for node in result.iter(f"{{{NS['t']}}}Message"))

            # Jedna linijka powodu. Pełny komunikat niesie ślad stosu i akapity
            # o tym, jak wyciszyć ostrzeżenie — czyli to, czego się tu nie szuka.
            short = re.sub(r"\s+", " ", message).strip()
            failures.append((name, short[:300]))

    if not failures:
        print("Wszystkie testy przeszły.")
        return 0

    # Po powodzie, nie po nazwie: czterysta testów wywróconych jedną przyczyną
    # to jedna rzecz do naprawienia, a nie czterysta.
    groups: dict[str, list[str]] = {}

    for name, why in failures:
        groups.setdefault(why, []).append(name)

    print(f"Nieudanych testów: {len(failures)}, powodów: {len(groups)}")

    for why, names in sorted(groups.items(), key=lambda z: -len(z[1])):
        print(f"::error::{len(names)}× {why} — np. {names[0]}")
        print(f"  {len(names)}× {why}")

        for name in names[:12]:
            print(f"      {name}")

        if len(names) > 12:
            print(f"      … i {len(names) - 12} dalszych")

    return 0


if __name__ == "__main__":
    sys.exit(main())

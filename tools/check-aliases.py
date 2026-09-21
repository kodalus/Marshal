"""Czy każdy alias przestrzeni nazw użyty w XAML-u jest w tym pliku zadeklarowany.

Kompilator XAML-a to łapie, ale dopiero w CI — a tutaj nie ma z czym zbudować
projektu. Ten sam błąd kosztował przebieg: alias został usunięty jako „nieużywany"
na podstawie policzenia wystąpień, a policzone zostało to jedno, które go używało.
"""
import io
import pathlib
import re
import sys

# <alias:Typ ... />, </alias:Typ>, atrybut przypięty: alias:Wlasciwosc="..."
PREFIX = re.compile(r'</?([A-Za-z][\w]*):[A-Za-z_]|(?<![\w"])([A-Za-z][\w]*):[A-Za-z_]\w*\s*=')

# x:DataType="alias:Typ", x:TypeArguments="alias:Typ"
TYPED = re.compile(r'x:(?:DataType|TypeArguments)\s*=\s*"([^"]*)"')

# rzutowanie w wiązaniu: ((alias:Typ)DataContext)
CAST = re.compile(r'\(([A-Za-z][\w]*):[A-Za-z_]')

RESERVED = {'xmlns', 'xml'}


def aliases(text: str) -> set[str]:
    used: set[str] = set()

    for first, second in PREFIX.findall(text):
        used.add(first or second)

    for value in TYPED.findall(text):
        used.update(part.split(':')[0] for part in value.split(',') if ':' in part)

    used.update(CAST.findall(text))

    return used - RESERVED


def main() -> int:
    missing = []

    for path in sorted(pathlib.Path('src').rglob('*.axaml')):
        text = io.open(path, encoding='utf-8').read()
        declared = set(re.findall(r'xmlns:([A-Za-z][\w]*)\s*=', text))

        for alias in sorted(aliases(text) - declared):
            missing.append(f'{path}: alias „{alias}" użyty, a niezadeklarowany')

    if missing:
        print('\n'.join(missing))
        return 1

    print('OK — aliasy przestrzeni nazw zadeklarowane.')
    return 0


if __name__ == '__main__':
    sys.exit(main())

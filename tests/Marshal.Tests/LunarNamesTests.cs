using FluentAssertions;
using Marshal.Domain.Lunar;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Nazwy miesięcy księżycowych: warstwa podpisu, która nie ma prawa zmienić rachunku.
/// </summary>
/// <remarks>
/// Testy pilnują jednej rzeczy: że brak nazwy jest zwyczajnym stanem, a nie usterką.
/// Nazwy są wyłączone domyślnie, można je wyczyścić pojedynczo, a zapis ustawień może
/// pochodzić z wersji, która miała ich inną liczbę. W każdym z tych przypadków ma zostać
/// numer — bo numer jest zawsze.
/// </remarks>
public sealed class LunarNamesTests
{
    [Fact]
    public void Propozycja_ma_trzynascie_nazw_i_zadna_nie_jest_pusta()
    {
        LunarNames.Preset.Should().HaveCount(LunarNames.Count);
        LunarNames.Preset.Should().OnlyContain(z => !string.IsNullOrWhiteSpace(z));
        LunarNames.Preset.Should().OnlyHaveUniqueItems();

        // Dziesiąty miesiąc wypada w okolicach listopada — stąd ta nazwa i stąd dopełniacz.
        LunarNames.Preset[9].Should().Be("Luna Listopada");
        LunarNames.Preset[12].Should().Be("Luna Progu");
    }

    [Fact]
    public void Brak_listy_nie_jest_usterka_tylko_brakiem_nazwy()
    {
        LunarNames.Of(null, 1).Should().BeNull();
        LunarNames.Of(LunarNames.Preset, 1).Should().Be("Luna Mrozu");
        LunarNames.Of(LunarNames.Preset, 13).Should().Be("Luna Progu");
    }

    [Fact]
    public void Nazwa_poza_lista_i_nazwa_wyczyszczona_daja_to_samo()
    {
        // Numer miesiąca poza zakresem listy: tak wygląda rok trzynastomiesięczny przy
        // liście skróconej do dwunastu pozycji przez starszy zapis ustawień.
        LunarNames.Of(LunarNames.Preset, 14).Should().BeNull();
        LunarNames.Of(LunarNames.Preset, 0).Should().BeNull();
        LunarNames.Of(LunarNames.Preset, -1).Should().BeNull();

        // Pole wyczyszczone ręcznie. Spacje to nadal puste pole.
        IReadOnlyList<string> wiped = ["", "   ", "Luna Trzecia"];

        LunarNames.Of(wiped, 1).Should().BeNull();
        LunarNames.Of(wiped, 2).Should().BeNull();
        LunarNames.Of(wiped, 3).Should().Be("Luna Trzecia");
    }

    [Fact]
    public void Nazwa_traci_otaczajace_spacje()
    {
        IReadOnlyList<string> spaced = ["  Luna Mrozu  "];

        LunarNames.Of(spaced, 1).Should().Be("Luna Mrozu");
    }

    [Fact]
    public void Lista_wyrownuje_sie_do_trzynastu_pozycji()
    {
        // Krótsza: dociągnięta pustymi polami, bez gubienia tego, co było.
        var padded = LunarNames.Padded(["Luna Mrozu", "Luna Śniegu"]);

        padded.Should().HaveCount(LunarNames.Count);
        padded[0].Should().Be("Luna Mrozu");
        padded[1].Should().Be("Luna Śniegu");
        padded[2].Should().BeEmpty();
        padded[12].Should().BeEmpty();

        // Dłuższa: przycięta, nie odrzucona.
        LunarNames.Padded(Enumerable.Repeat("Luna", 20).ToList())
            .Should().HaveCount(LunarNames.Count);

        // Brak zapisu: trzynaście pustych pól, czyli gotowy formularz ustawień.
        LunarNames.Padded(null).Should().HaveCount(LunarNames.Count);
        LunarNames.Padded(null).Should().OnlyContain(z => z.Length == 0);

        // Propozycja przechodzi przez wyrównanie bez zmian — przycisk „wstaw propozycję”
        // i zapis ustawień mają dawać to samo.
        LunarNames.Padded(LunarNames.Preset).Should().Equal(LunarNames.Preset);
    }
}

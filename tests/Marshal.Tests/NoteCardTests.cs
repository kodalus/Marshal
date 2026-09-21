using FluentAssertions;
using Marshal.UI.ViewModels;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Początek tekstu notatki — to, co widać na kafelku pod tytułem.
/// </summary>
/// <remarks>
/// Tytuł notatki bywa jednym słowem („Zakupy", „Pomysły") i nie mówi, co w środku.
/// Kafelek odpowiada na to pytanie bez otwierania, więc te kilkadziesiąt znaków jest
/// tu całą treścią — a składa je zwykła pętla po znakach, którą łatwo popsuć
/// w sposób niewidoczny na oko.
/// </remarks>
public sealed class NoteCardTests
{
    [Fact]
    public void Poczatek_tekstu_zdejmuje_znaki_zapisu_z_poczatkow_slow()
    {
        // Notatka pisana markdownem zaczyna się od kratki albo myślnika. Kafelek,
        // który je pokazuje, mówi o zapisie, a nie o treści.
        NotesViewModel.Beginning("# Zakupy\n- mleko\n- chleb")
            .Should().Be("Zakupy mleko chleb");
    }

    [Fact]
    public void Znak_w_srodku_slowa_zostaje()
    {
        // Gwiazdka w „2*3" i kreska w „biało-czerwony" są treścią, nie zapisem.
        // Zdejmowanie ich wszędzie robiłoby z notatki bełkot.
        NotesViewModel.Beginning("biało-czerwony 2*3").Should().Be("biało-czerwony 2*3");
    }

    [Fact]
    public void Puste_linie_i_wciecia_schodza_do_jednego_odstepu()
    {
        // Notatka ma akapity, a kafelek cztery linijki. Bez zbicia odstępów pierwsze
        // zdanie spychała pusta linia spod tytułu.
        NotesViewModel.Beginning("Pierwsze\n\n\n   Drugie\tTrzecie")
            .Should().Be("Pierwsze Drugie Trzecie");
    }

    [Fact]
    public void Dlugi_tekst_konczy_sie_wielokropkiem()
    {
        var beginning = NotesViewModel.Beginning(new string('a', 400));

        beginning.Should().EndWith("…");
        beginning.Length.Should().BeLessThan(200, "kafelek ma kilka linijek, a nie całą notatkę");
    }

    [Fact]
    public void Notatka_bez_tresci_nie_ma_poczatku()
    {
        NotesViewModel.Beginning(null).Should().BeEmpty();
        NotesViewModel.Beginning("   \n\t ").Should().BeEmpty();
    }
}

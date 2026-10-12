using FluentAssertions;
using Marshal.Application.Calendar;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Tożsamość wydarzenia cyklicznego w Google — wyliczana z serii, nie nadawana.
/// </summary>
/// <remarks>
/// Ta sama lekcja, co przy tożsamości wystąpienia, raz już przeoczona o warstwę wyżej:
/// zakładając wydarzenie bez podanego identyfikatora, każde urządzenie dostawało od
/// Google własny — więc pulpit i telefon, dociągając serie niezależnie, zakładały dwa
/// wydarzenia cykliczne na jedną serię.
/// </remarks>
public sealed class SeriesEventIdTests
{
    [Fact]
    public void Ta_sama_seria_daje_ten_sam_identyfikator()
    {
        var series = Guid.CreateVersion7();

        SeriesEventId.For(series).Should().Be(SeriesEventId.For(series));
    }

    [Fact]
    public void Rozne_serie_daja_rozne_identyfikatory()
    {
        SeriesEventId.For(Guid.CreateVersion7())
            .Should().NotBe(SeriesEventId.For(Guid.CreateVersion7()));
    }

    [Fact]
    public void Postac_jest_taka_jakiej_wymaga_Google()
    {
        // Dozwolone są wyłącznie znaki kodowania base32hex — cyfry i małe litery od „a"
        // do „v" — a długość mieści się w 5–1024. Znak spoza tego zbioru kończy się
        // odmową przy zakładaniu, czyli serią, której w kalendarzu nie ma.
        for (var i = 0; i < 200; i++)
        {
            var id = SeriesEventId.For(Guid.CreateVersion7());

            id.Should().HaveLength(26);
            id.Should().MatchRegex("^[0-9a-v]+$");
        }
    }

    [Fact]
    public void Caly_identyfikator_serii_wchodzi_do_zapisu()
    {
        // Dwie serie różniące się **ostatnim** bitem muszą dać różne zapisy. Bez
        // dopełnienia ostatniej, niepełnej piątki bitów ostatni znak przepadał i obie
        // trafiałyby na to samo wydarzenie w Google.
        var first = new Guid("00000000-0000-0000-0000-000000000000");
        var second = new Guid("00000000-0000-0000-0000-000000000001");

        SeriesEventId.For(first).Should().NotBe(SeriesEventId.For(second));
    }
}

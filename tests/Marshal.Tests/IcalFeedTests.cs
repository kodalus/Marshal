using FluentAssertions;
using Marshal.Infrastructure.Calendar;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Rozbiór treści iCal. Wszystkie pułapki tego formatu siedzą tutaj, a żadnej
/// z nich nie widać bez prawdziwego pliku.
/// </summary>
public sealed class IcalFeedTests
{
    private static readonly DateTime Teraz = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    private static string Kalendarz(string wnetrze) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//PL\r\n" + wnetrze + "END:VCALENDAR\r\n";

    [Fact]
    public void Pusty_kalendarz_nie_ma_wydarzen()
    {
        IcalFeed.Parse(Kalendarz(string.Empty), Teraz).Should().BeEmpty();
    }

    [Fact]
    public void Zwykle_wydarzenie_wraca_z_tytulem_i_godzinami()
    {
        var plik = Kalendarz(
            "BEGIN:VEVENT\r\nUID:a1\r\nSUMMARY:Wizyta u pediatry\r\n" +
            "DTSTART:20260917T090000Z\r\nDTEND:20260917T100000Z\r\n" +
            "LOCATION:Przychodnia\r\nEND:VEVENT\r\n");

        var wydarzenia = IcalFeed.Parse(plik, Teraz);

        wydarzenia.Should().ContainSingle();
        wydarzenia[0].Title.Should().Be("Wizyta u pediatry");
        wydarzenia[0].Location.Should().Be("Przychodnia");
        wydarzenia[0].StartsAt.UtcDateTime.Should().Be(new DateTime(2026, 9, 17, 9, 0, 0));
        wydarzenia[0].EndsAt.UtcDateTime.Should().Be(new DateTime(2026, 9, 17, 10, 0, 0));
        wydarzenia[0].IsAllDay.Should().BeFalse();
    }

    [Fact]
    public void Wydarzenie_calodniowe_jest_rozpoznane()
    {
        // W iCal poznaje się je po dacie bez pory dnia, nie po osobnym polu.
        var plik = Kalendarz(
            "BEGIN:VEVENT\r\nUID:a2\r\nSUMMARY:Urlop\r\n" +
            "DTSTART;VALUE=DATE:20260920\r\nDTEND;VALUE=DATE:20260922\r\nEND:VEVENT\r\n");

        IcalFeed.Parse(plik, Teraz).Single().IsAllDay.Should().BeTrue();
    }

    [Fact]
    public void Wydarzenie_bez_tytulu_dostaje_zastepczy()
    {
        // Pusty wiersz na siatce nie daje się w nic kliknąć ani niczego nie mówi.
        var plik = Kalendarz(
            "BEGIN:VEVENT\r\nUID:a3\r\nDTSTART:20260917T090000Z\r\nDTEND:20260917T100000Z\r\nEND:VEVENT\r\n");

        IcalFeed.Parse(plik, Teraz).Single().Title.Should().Be("(bez tytułu)");
    }

    [Fact]
    public void Wydarzenie_powtarzalne_rozwija_sie_na_wystapienia()
    {
        var plik = Kalendarz(
            "BEGIN:VEVENT\r\nUID:a4\r\nSUMMARY:Krav maga\r\n" +
            "DTSTART:20260917T170000Z\r\nDTEND:20260917T180000Z\r\n" +
            "RRULE:FREQ=WEEKLY;COUNT=4\r\nEND:VEVENT\r\n");

        IcalFeed.Parse(plik, Teraz).Should().HaveCount(4);
    }

    [Fact]
    public void Wystapienia_serii_maja_rozne_identyfikatory()
    {
        // Wszystkie mają ten sam UID, więc bez daty w kluczu cotygodniowe zajęcia
        // zapisałyby się do bazy raz i siatka pokazałaby jedno.
        var plik = Kalendarz(
            "BEGIN:VEVENT\r\nUID:a5\r\nSUMMARY:Krav maga\r\n" +
            "DTSTART:20260917T170000Z\r\nDTEND:20260917T180000Z\r\n" +
            "RRULE:FREQ=WEEKLY;COUNT=4\r\nEND:VEVENT\r\n");

        var wydarzenia = IcalFeed.Parse(plik, Teraz);

        wydarzenia.Select(e => e.ExternalId).Distinct().Should().HaveCount(4);
        wydarzenia.Should().AllSatisfy(e => e.ExternalId.Should().StartWith("a5|"));
    }

    [Fact]
    public void Powtarzanie_bez_konca_jest_ograniczone_oknem()
    {
        // Kanał z cotygodniowym wydarzeniem bez daty końca rozwinąłby się
        // w nieskończoność, a pamięć skończyłaby się wcześniej.
        var plik = Kalendarz(
            "BEGIN:VEVENT\r\nUID:a6\r\nSUMMARY:Bez końca\r\n" +
            "DTSTART:20260917T170000Z\r\nDTEND:20260917T180000Z\r\n" +
            "RRULE:FREQ=WEEKLY\r\nEND:VEVENT\r\n");

        var wydarzenia = IcalFeed.Parse(plik, Teraz);

        wydarzenia.Should().NotBeEmpty();
        wydarzenia.Should().HaveCountLessThan(40);
    }

    [Fact]
    public void Wydarzenie_spoza_okna_nie_wchodzi()
    {
        var plik = Kalendarz(
            "BEGIN:VEVENT\r\nUID:a7\r\nSUMMARY:Za rok\r\n" +
            "DTSTART:20270917T090000Z\r\nDTEND:20270917T100000Z\r\nEND:VEVENT\r\n");

        IcalFeed.Parse(plik, Teraz).Should().BeEmpty();
    }
}

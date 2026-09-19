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
    private static readonly DateTime Now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    private static string NewCalendar(string wnetrze) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//PL\r\n" + wnetrze + "END:VCALENDAR\r\n";

    [Fact]
    public void Pusty_kalendarz_nie_ma_wydarzen()
    {
        IcalFeed.Parse(NewCalendar(string.Empty), Now).Should().BeEmpty();
    }

    [Fact]
    public void Zwykle_wydarzenie_wraca_z_tytulem_i_godzinami()
    {
        var file = NewCalendar(
            "BEGIN:VEVENT\r\nUID:a1\r\nSUMMARY:Wizyta u pediatry\r\n" +
            "DTSTART:20260917T090000Z\r\nDTEND:20260917T100000Z\r\n" +
            "LOCATION:Przychodnia\r\nEND:VEVENT\r\n");

        var events = IcalFeed.Parse(file, Now);

        events.Should().ContainSingle();
        events[0].Title.Should().Be("Wizyta u pediatry");
        events[0].Location.Should().Be("Przychodnia");
        events[0].StartsAt.UtcDateTime.Should().Be(new DateTime(2026, 9, 17, 9, 0, 0));
        events[0].EndsAt.UtcDateTime.Should().Be(new DateTime(2026, 9, 17, 10, 0, 0));
        events[0].IsAllDay.Should().BeFalse();
    }

    [Fact]
    public void Wydarzenie_calodniowe_jest_rozpoznane()
    {
        // W iCal poznaje się je po dacie bez pory dnia, nie po osobnym polu.
        var file = NewCalendar(
            "BEGIN:VEVENT\r\nUID:a2\r\nSUMMARY:Urlop\r\n" +
            "DTSTART;VALUE=DATE:20260920\r\nDTEND;VALUE=DATE:20260922\r\nEND:VEVENT\r\n");

        IcalFeed.Parse(file, Now).Single().IsAllDay.Should().BeTrue();
    }

    [Fact]
    public void Wydarzenie_bez_tytulu_dostaje_zastepczy()
    {
        // Pusty wiersz na siatce nie daje się w nic kliknąć ani niczego nie mówi.
        var file = NewCalendar(
            "BEGIN:VEVENT\r\nUID:a3\r\nDTSTART:20260917T090000Z\r\nDTEND:20260917T100000Z\r\nEND:VEVENT\r\n");

        IcalFeed.Parse(file, Now).Single().Title.Should().Be("(bez tytułu)");
    }

    [Fact]
    public void Wydarzenie_powtarzalne_rozwija_sie_na_wystapienia()
    {
        var file = NewCalendar(
            "BEGIN:VEVENT\r\nUID:a4\r\nSUMMARY:Krav maga\r\n" +
            "DTSTART:20260917T170000Z\r\nDTEND:20260917T180000Z\r\n" +
            "RRULE:FREQ=WEEKLY;COUNT=4\r\nEND:VEVENT\r\n");

        IcalFeed.Parse(file, Now).Should().HaveCount(4);
    }

    [Fact]
    public void Wystapienia_serii_maja_rozne_identyfikatory()
    {
        // Wszystkie mają ten sam UID, więc bez daty w kluczu cotygodniowe zajęcia
        // zapisałyby się do bazy raz i siatka pokazałaby jedno.
        var file = NewCalendar(
            "BEGIN:VEVENT\r\nUID:a5\r\nSUMMARY:Krav maga\r\n" +
            "DTSTART:20260917T170000Z\r\nDTEND:20260917T180000Z\r\n" +
            "RRULE:FREQ=WEEKLY;COUNT=4\r\nEND:VEVENT\r\n");

        var events = IcalFeed.Parse(file, Now);

        events.Select(e => e.ExternalId).Distinct().Should().HaveCount(4);
        events.Should().AllSatisfy(e => e.ExternalId.Should().StartWith("a5|"));
    }

    [Fact]
    public void Powtarzanie_bez_konca_jest_ograniczone_oknem()
    {
        // Kanał z cotygodniowym wydarzeniem bez daty końca rozwinąłby się
        // w nieskończoność, a pamięć skończyłaby się wcześniej.
        var file = NewCalendar(
            "BEGIN:VEVENT\r\nUID:a6\r\nSUMMARY:Bez końca\r\n" +
            "DTSTART:20260917T170000Z\r\nDTEND:20260917T180000Z\r\n" +
            "RRULE:FREQ=WEEKLY\r\nEND:VEVENT\r\n");

        var events = IcalFeed.Parse(file, Now);

        events.Should().NotBeEmpty();
        events.Should().HaveCountLessThan(40);
    }

    [Fact]
    public void Wydarzenie_spoza_okna_nie_wchodzi()
    {
        var file = NewCalendar(
            "BEGIN:VEVENT\r\nUID:a7\r\nSUMMARY:Za rok\r\n" +
            "DTSTART:20270917T090000Z\r\nDTEND:20270917T100000Z\r\nEND:VEVENT\r\n");

        IcalFeed.Parse(file, Now).Should().BeEmpty();
    }

    [Fact]
    public void Barwa_kanalu_czyta_sie_z_pola_X()
    {
        // Pole spoza normy, ale wystawia je wszystko, co w ogóle podaje kolor.
        // Biblioteka do rozbioru nie wpuszcza własnych pól na X, stąd szukanie
        // w tekście — i stąd ten test, bo to jedyne miejsce, gdzie widać literówkę.
        var file = NewCalendar("X-APPLE-CALENDAR-COLOR:#34AADC\r\n");

        IcalFeed.ParseColor(file).Should().Be("#34AADC");
    }

    [Fact]
    public void Kanal_bez_barwy_nie_zmysla_koloru()
    {
        IcalFeed.ParseColor(NewCalendar(string.Empty)).Should().BeNull();
    }
}

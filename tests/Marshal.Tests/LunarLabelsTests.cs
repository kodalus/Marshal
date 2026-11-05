using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.Calendar;
using Marshal.Domain.Lunar;
using Marshal.UI.ViewModels;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Podpisy księżycowe: jedno miejsce, w którym rachunek spotyka się z ustawieniami.
/// </summary>
/// <remarks>
/// <para>
/// Tutaj nie sprawdza się astronomii — to robi <see cref="LunarCalendarTests"/>. Tutaj
/// sprawdza się trzy rzeczy, które mogą być złe przy dobrym rachunku: że wyłączony
/// kalendarz nie oddaje niczego (a nie pustego napisu, który ekran i tak narysuje),
/// że nazwa pojawia się dopiero po włączeniu nazw, i że po wyłączeniu nazw zostaje
/// numer — bo numer jest zawsze.
/// </para>
/// <para>
/// Czwarta rzecz, osobno: <b>długość podpisu zależy od miejsca</b>. Nagłówek kolumny ma
/// na telefonie jedną siódmą szerokości ekranu, karta zadania ma całą szerokość okna.
/// Jeden podpis na oba znaczyłby albo obcięty nagłówek, albo pustą kartę.
/// </para>
/// </remarks>
public sealed class LunarLabelsTests
{
    /// <summary>5 listopada 2026: Luna 10, dzień 27 z 30. Sprawdzone w testach kalendarza.</summary>
    private static readonly DateOnly Day = new(2026, 11, 5);

    /// <summary>9 listopada 2026: nów, czyli pierwszy dzień Luny 11 — nie dziesiątej.</summary>
    private static readonly DateOnly NewMoon = new(2026, 11, 9);

    /// <summary>24 listopada 2026: pełnia, szesnasty dzień tej samej Luny 11.</summary>
    private static readonly DateOnly FullMoon = new(2026, 11, 24);

    [Fact]
    public void Wylaczony_kalendarz_nie_oddaje_niczego()
    {
        // Nie pusty napis: pusty narysowałby się jako pusty wiersz pod liczbą dnia
        // i siatka urosłaby o tyle, ile zajmuje nic.
        var labels = new LunarLabels(new Settings());

        labels.Shown.Should().BeFalse();
        labels.Of(Day).Should().BeNull();
        labels.Full(Day).Should().BeNull();
    }

    [Fact]
    public void Bez_nazw_zostaje_numer()
    {
        var labels = new LunarLabels(new Settings { LunarShown = true });

        var mark = labels.Of(Day);

        mark.Should().NotBeNull();
        mark!.Month.Should().Be(10);
        mark.Day.Should().Be(27);
        mark.Length.Should().Be(30);
        mark.MonthName.Should().Be("Luna 10");
        mark.IsThirteenth.Should().BeFalse();
    }

    [Fact]
    public void Nazwa_dopiero_po_wlaczeniu_nazw()
    {
        var settings = new Settings { LunarShown = true, Names = [.. LunarNames.Preset] };

        // Nazwy wpisane, ale przełącznik wyłączony — i to nie jest stan przejściowy:
        // tak wygląda urządzenie, na którym ktoś nazwy wpisał i potem je wyłączył.
        new LunarLabels(settings).Of(Day)!.MonthName.Should().Be("Luna 10");

        settings.LunarNamesShown = true;

        new LunarLabels(settings).Of(Day)!.MonthName.Should().Be("Luna Listopada");
    }

    [Fact]
    public void Wyczyszczona_nazwa_wraca_do_numeru_a_nie_do_pustki()
    {
        var names = LunarNames.Padded(LunarNames.Preset).ToList();
        names[9] = string.Empty;

        var labels = new LunarLabels(new Settings
        {
            LunarShown = true,
            LunarNamesShown = true,
            Names = names,
        });

        labels.Of(Day)!.MonthName.Should().Be("Luna 10");
    }

    [Fact]
    public void Now_i_pelnia_dostaja_znak_a_pozostale_dni_nie()
    {
        var labels = new LunarLabels(new Settings { LunarShown = true });

        var opening = labels.Of(NewMoon)!;
        var middle = labels.Of(FullMoon)!;
        var ordinary = labels.Of(Day)!;

        // Nów wypełniony, pełnia pusta — i to celowo odwrotnie niż na niebie: znaki
        // stoją na jasnym tle, więc wypełniony jest tym, który widać mocniej, a nów
        // jest początkiem miesiąca.
        opening.IsNewMoon.Should().BeTrue();
        opening.Glyph.Should().Be("●");
        opening.HasGlyph.Should().BeTrue();
        opening.Opens.Should().BeTrue();
        opening.Day.Should().Be(1);

        middle.IsFullMoon.Should().BeTrue();
        middle.Day.Should().Be(16);
        middle.Glyph.Should().Be("○");
        middle.Opens.Should().BeFalse("pełnia jest w środku miesiąca, nie na jego początku");

        ordinary.HasGlyph.Should().BeFalse();
        ordinary.Glyph.Should().BeEmpty();
        ordinary.Opens.Should().BeFalse();
    }

    [Fact]
    public void Podpis_do_naglowka_jest_krotki_a_do_karty_pelny()
    {
        var labels = new LunarLabels(new Settings
        {
            LunarShown = true,
            LunarNamesShown = true,
            Names = [.. LunarNames.Preset],
        });

        // Nagłówek: dzień i najwyżej znak fazy. Bez nazwy, bo kolumna ma jedną siódmą
        // szerokości ekranu telefonu.
        labels.Of(Day)!.Mark.Should().Be("27");
        labels.Of(NewMoon)!.Mark.Should().Be("● 1");
        labels.Of(Day)!.Mark.Length.Should().BeLessThanOrEqualTo(4);

        // Karta: cała prawda, bo jest na nią miejsce.
        labels.Full(Day).Should().Be("Luna Listopada, dzień 27 z 30");

        // Nów z 9 listopada otwiera już następny miesiąc — dlatego inna nazwa
        // przy tej samej gregoriańskiej nazwie miesiąca. Na tym polega cała nakładka.
        labels.Full(NewMoon).Should().Be("Luna Mgieł, dzień 1 z 30");
    }

    [Fact]
    public void Komorka_miesiaca_rysuje_kreske_tylko_w_dniu_nowiu()
    {
        var labels = new LunarLabels(new Settings
        {
            LunarShown = true,
            LunarNamesShown = true,
            Names = [.. LunarNames.Preset],
        });

        var opening = Cell(NewMoon, labels);
        var ordinary = Cell(Day, labels);
        var dark = Cell(NewMoon, new LunarLabels(new Settings()));

        opening.OpensLunarMonth.Should().BeTrue();
        opening.HasLunarMonthName.Should().BeTrue();
        opening.LunarMonthName.Should().Be("Luna Mgieł");
        opening.HasLunarGlyph.Should().BeTrue();

        // Nazwa raz na miesiąc, nie w każdej komórce: powtórzona trzydzieści razy
        // przestaje być wiadomością, a zabiera wiersz, na którym mieści się wpis.
        ordinary.OpensLunarMonth.Should().BeFalse();
        ordinary.HasLunarMonthName.Should().BeFalse();
        ordinary.LunarMonthName.Should().BeNull();
        ordinary.HasLunarGlyph.Should().BeFalse();

        // Wyłączony kalendarz: komórka nie wie o niczym i nie rysuje niczego.
        dark.OpensLunarMonth.Should().BeFalse();
        dark.HasLunarMonthName.Should().BeFalse();
        dark.HasLunarGlyph.Should().BeFalse();
        dark.LunarGlyph.Should().BeEmpty();
    }

    [Fact]
    public void Naglowek_kolumny_pyta_tylko_czy_jest_co_pokazac()
    {
        var lit = Column(Day, new LunarLabels(new Settings { LunarShown = true }));
        var dark = Column(Day, new LunarLabels(new Settings()));

        lit.HasLunar.Should().BeTrue();
        lit.Lunar!.Mark.Should().Be("27");

        dark.HasLunar.Should().BeFalse();
        dark.Lunar.Should().BeNull();
    }

    private static MonthCell Cell(DateOnly day, LunarLabels labels) =>
        new(day, $"{day.Day}", false, false, [], 0, labels.Of(day));

    private static CalendarColumn Column(DateOnly day, LunarLabels labels) =>
        new(day, "czw", [], [], false, 0, labels.Of(day));

    /// <summary>Ustawienia, w których liczą się tylko trzy rzeczy księżycowe i strefa.</summary>
    private sealed class Settings : ISettings
    {
        public TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

        public string? ZoneProblem => null;

        public ThemeChoice Theme => ThemeChoice.System;

        public string? GoogleClientId => null;

        public string? GoogleClientSecret => null;

        public bool GoogleCalendarEnabled => false;

        public Guid? MainCalendarId => null;

        public void SetMainCalendar(Guid? calendarId) => throw new NotSupportedException();

        public IReadOnlyList<string> CalendarAccounts => [];

        public void AddCalendarAccount(string email) => throw new NotSupportedException();

        public void RemoveCalendarAccount(string email) => throw new NotSupportedException();

        public void SetGoogleCalendarEnabled(bool enabled) => throw new NotSupportedException();

        public void SetZone(string id) => throw new NotSupportedException();

        public void SetTheme(ThemeChoice theme) => throw new NotSupportedException();

        public void SetGoogle(string? clientId, string? clientSecret) =>
            throw new NotSupportedException();

        public bool DailyBackup => false;

        public void SetDailyBackup(bool on) => throw new NotSupportedException();

        public string? BackupFolder => null;

        public void SetBackupFolder(string? path) => throw new NotSupportedException();

        public bool LunarShown { get; set; }

        public void SetLunarShown(bool on) => LunarShown = on;

        public bool LunarNamesShown { get; set; }

        public void SetLunarNamesShown(bool on) => LunarNamesShown = on;

        public List<string> Names { get; set; } = [.. Enumerable.Repeat(string.Empty, 13)];

        public IReadOnlyList<string> LunarMonthNames => Names;

        public void SetLunarMonthNames(IReadOnlyList<string> names) => Names = [.. names];
    }
}

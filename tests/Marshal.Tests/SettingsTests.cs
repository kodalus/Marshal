using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Sync;
using Marshal.Infrastructure.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Ustawienia urządzenia i strefa, w której liczone są dni (spec 3.4).
/// </summary>
public sealed class SettingsTests : IDisposable
{
    private readonly SqliteConnection _polaczenie = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly LocalSettings _ustawienia;

    public SettingsTests()
    {
        _polaczenie.Open();
        // Z przechwytywaczem dziennika, inaczej sprawdzenie „poświadczenia nie trafiają
        // do dziennika" przechodziłoby dlatego, że dziennika nie ma wcale.
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_polaczenie)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.Migrate();
        _ustawienia = new LocalSettings(_db);
    }

    [Fact]
    public void Bez_zapisu_strefa_jest_warszawska_a_motyw_za_systemem()
    {
        // Wpisana wprost, nie brana z systemu — świeżo zainstalowana aplikacja ma
        // liczyć dni tak samo na telefonie kupionym z inną strefą fabryczną.
        _ustawienia.Zone.Should().Be(TimeZoneInfo.FindSystemTimeZoneById(LocalSettings.DefaultZoneId));
        _ustawienia.Theme.Should().Be(ThemeChoice.System);
    }

    [Fact]
    public void Poswiadczenia_dysku_przezywaja_ponowne_otwarcie_i_sa_przycinane()
    {
        // Kopiowanie z konsoli Google wciąga spację albo koniec wiersza, a logowanie
        // odbija się wtedy komunikatem o złym kliencie — po którym nie widać, że
        // chodziło o jeden znak.
        _ustawienia.SetGoogle("  123.apps.googleusercontent.com\n", " GOCSPX-tajne ");

        var znowu = new LocalSettings(_db);

        znowu.GoogleClientId.Should().Be("123.apps.googleusercontent.com");
        znowu.GoogleClientSecret.Should().Be("GOCSPX-tajne");
    }

    [Fact]
    public void Poswiadczenia_dysku_sa_lokalne_i_nie_trafiaja_do_dziennika_zmian()
    {
        // Dziennik zmian jest zwykłym tekstem lądującym na Dysku. Poświadczenia do
        // Dysku nie mają jechać przez Dysk — na drugim urządzeniu wkleja się je jeszcze raz.
        _ustawienia.SetGoogle("123.apps.googleusercontent.com", "GOCSPX-tajne");

        _db.Changes.Should().BeEmpty();
    }

    [Fact]
    public void Ustawienia_przezywaja_ponowne_otwarcie_bazy()
    {
        _ustawienia.SetTheme(ThemeChoice.Dark);
        _ustawienia.SetZone("UTC");

        var znowu = new LocalSettings(_db);

        znowu.Theme.Should().Be(ThemeChoice.Dark);
        znowu.Zone.Should().Be(TimeZoneInfo.Utc);
    }

    [Fact]
    public void Nieznana_strefa_cofa_sie_do_domyslnej_zamiast_wywracac_start()
    {
        // Baza przeniesiona między systemami może nieść nazwę zapisaną inaczej.
        // Brak czasu jest gorszy niż czas w niewłaściwej strefie.
        _db.LocalSettings.Add(
            new Marshal.Domain.Sync.LocalSetting(LocalSettings.ZoneKey, "Mars/Olympus_Mons"));
        _db.SaveChanges();

        var znowu = new LocalSettings(_db);

        znowu.Zone.Should().NotBeNull();

        // I nie po cichu. Zastępstwo przesuwa **wszystkie** godziny naraz, a przesunięte
        // wszystko wygląda dokładnie tak samo jak źle pobrane dane — dwie zupełnie różne
        // rzeczy do zrobienia. Ślad zostaje i widać go na ekranie oraz w dzienniku.
        znowu.ZoneProblem.Should().NotBeNullOrEmpty();
        znowu.ZoneProblem.Should().Contain("Mars/Olympus_Mons");
    }

    [Fact]
    public void Dzialajaca_strefa_nie_zglasza_klopotu()
    {
        // Ostrzeżenie, które świeci zawsze, przestaje być ostrzeżeniem.
        _ustawienia.ZoneProblem.Should().BeNull();
    }

    /// <summary>Ustawienia o jednej wartości — na potrzeby sprawdzenia samego zegara.</summary>
    private sealed class Strefa(string id) : ISettings
    {
        public TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById(id);

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
    }

    private sealed class StalyMoment(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now { get; } = now;
    }

    [Fact]
    public void Dzien_liczy_sie_w_strefie_z_ustawien_a_nie_systemu()
    {
        // Sedno spec 3.4. Ten sam moment fizyczny ma w dwóch strefach różne
        // przesunięcie — i to jest dokładnie ta różnica, przez którą po wyjeździe
        // zadanie jutrzejsze robi się dzisiejszym.
        var wUtc = new SystemClock(new Strefa("UTC"));
        var wWarszawie = new SystemClock(new Strefa("Europe/Warsaw"));

        (wWarszawie.Now.Offset - wUtc.Now.Offset).Should().BeGreaterThan(TimeSpan.Zero);
        wUtc.Now.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Dzisiaj_bierze_dzien_ze_sciany_a_nie_ze_strefy_maszyny()
    {
        // Pierwsza minuta wtorku w strefie z ustawień. Gdyby „dzisiaj" liczyło się
        // przez LocalDateTime, maszyna w UTC pokazałaby jeszcze poniedziałek —
        // i raz na dobę cała aplikacja pracowałaby na wczorajszym dniu.
        // Typ interfejsu, nie klasy: Today jest domyślną implementacją składową
        // interfejsu, a takich nie widać przez typ implementujący.
        IClock clock = new StalyMoment(
            new DateTimeOffset(2026, 9, 15, 0, 30, 0, TimeSpan.FromHours(2)));

        clock.Today.Should().Be(new DateOnly(2026, 9, 15));
    }

    public void Dispose()
    {
        _db.Dispose();
        _polaczenie.Dispose();
    }
}

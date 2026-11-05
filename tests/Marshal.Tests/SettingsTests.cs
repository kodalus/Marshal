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
    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly LocalSettings _settings;

    public SettingsTests()
    {
        _connection.Open();
        // Z przechwytywaczem dziennika, inaczej sprawdzenie „poświadczenia nie trafiają
        // do dziennika" przechodziłoby dlatego, że dziennika nie ma wcale.
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.Migrate();
        _settings = new LocalSettings(_db);
    }

    [Fact]
    public void Bez_zapisu_strefa_jest_warszawska_a_motyw_za_systemem()
    {
        // Wpisana wprost, nie brana z systemu — świeżo zainstalowana aplikacja ma
        // liczyć dni tak samo na telefonie kupionym z inną strefą fabryczną.
        _settings.Zone.Should().Be(TimeZoneInfo.FindSystemTimeZoneById(LocalSettings.DefaultZoneId));
        _settings.Theme.Should().Be(ThemeChoice.System);
    }

    [Fact]
    public void Poswiadczenia_dysku_przezywaja_ponowne_otwarcie_i_sa_przycinane()
    {
        // Kopiowanie z konsoli Google wciąga spację albo koniec wiersza, a logowanie
        // odbija się wtedy komunikatem o złym kliencie — po którym nie widać, że
        // chodziło o jeden znak.
        _settings.SetGoogle("  123.apps.googleusercontent.com\n", " GOCSPX-tajne ");

        var znowu = new LocalSettings(_db);

        znowu.GoogleClientId.Should().Be("123.apps.googleusercontent.com");
        znowu.GoogleClientSecret.Should().Be("GOCSPX-tajne");
    }

    [Fact]
    public void Poswiadczenia_dysku_sa_lokalne_i_nie_trafiaja_do_dziennika_zmian()
    {
        // Dziennik zmian jest zwykłym tekstem lądującym na Dysku. Poświadczenia do
        // Dysku nie mają jechać przez Dysk — na drugim urządzeniu wkleja się je jeszcze raz.
        _settings.SetGoogle("123.apps.googleusercontent.com", "GOCSPX-tajne");

        _db.Changes.Should().BeEmpty();
    }

    [Fact]
    public void Ustawienia_przezywaja_ponowne_otwarcie_bazy()
    {
        _settings.SetTheme(ThemeChoice.Dark);
        _settings.SetZone("UTC");

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
        _settings.ZoneProblem.Should().BeNull();
    }

    /// <summary>Ustawienia o jednej wartości — na potrzeby sprawdzenia samego zegara.</summary>
    [Fact]
    public void Kalendarz_ksiezycowy_i_nazwy_sa_domyslnie_wylaczone()
    {
        // Odwrotnie niż codzienna kopia i z odwrotnego powodu: tam zaniechanie kosztuje
        // dane, więc brak decyzji znaczy „chroń mnie”. Tutaj zaniechanie nie kosztuje
        // nic, więc brak decyzji znaczy „nie dodawaj mi niczego na ekran”.
        _settings.LunarShown.Should().BeFalse();
        _settings.LunarNamesShown.Should().BeFalse();
    }

    [Fact]
    public void Bez_zapisu_jest_trzynascie_pustych_nazw_a_nie_pusta_lista()
    {
        // Pusta lista kazałaby każdemu, kto po nią sięga, liczyć pozycje samemu.
        // Trzynaście pustych pól to gotowy formularz i gotowe wejście do podpisu.
        _settings.LunarMonthNames.Should().HaveCount(13);
        _settings.LunarMonthNames.Should().OnlyContain(z => z.Length == 0);
    }

    [Fact]
    public void Nazwy_przezywaja_ponowne_otwarcie()
    {
        _settings.SetLunarMonthNames(["Luna Mrozu", "Luna Śniegu"]);

        var znowu = new LocalSettings(_db);

        znowu.LunarMonthNames.Should().HaveCount(13);
        znowu.LunarMonthNames[0].Should().Be("Luna Mrozu");
        znowu.LunarMonthNames[1].Should().Be("Luna Śniegu");
        znowu.LunarMonthNames[2].Should().BeEmpty();
    }

    [Fact]
    public void Wyczyszczona_nazwa_nie_przesuwa_pozostalych()
    {
        // Tu pozycja znaczy numer miesiąca, więc pustych wierszy nie wolno pominąć
        // przy czytaniu. Pominięte przesunęłyby czwartą nazwę na trzecie miejsce —
        // czyli wyczyszczenie jednego pola przestawiłoby wszystkie następne.
        _settings.SetLunarMonthNames(
            ["Pierwsza", "Druga", "", "Czwarta", "", "", "", "", "", "", "", "", "Trzynasta"]);

        var znowu = new LocalSettings(_db);

        znowu.LunarMonthNames[2].Should().BeEmpty();
        znowu.LunarMonthNames[3].Should().Be("Czwarta");
        znowu.LunarMonthNames[12].Should().Be("Trzynasta");
    }

    [Fact]
    public void Wylaczenie_kalendarza_nie_kasuje_nazw()
    {
        // Wyłączenie na pół dnia nie ma kosztować trzynastu pól do wpisania na nowo.
        _settings.SetLunarShown(true);
        _settings.SetLunarNamesShown(true);
        _settings.SetLunarMonthNames(["Luna Mrozu"]);

        _settings.SetLunarShown(false);

        _settings.LunarMonthNames[0].Should().Be("Luna Mrozu");

        // I przełącznik nazw też zostaje włączony: to osobna decyzja, więc gaszenie go
        // za użytkownika kazałoby mu ją podejmować dwa razy.
        _settings.LunarNamesShown.Should().BeTrue();

        _settings.SetLunarShown(true);

        _settings.LunarNamesShown.Should().BeTrue();
        _settings.LunarMonthNames[0].Should().Be("Luna Mrozu");
    }

    [Fact]
    public void Nazwa_dluzsza_niz_lista_jest_przycinana_a_nie_odrzucana()
    {
        _settings.SetLunarMonthNames([.. Enumerable.Range(1, 20).Select(z => $"Luna {z}")]);

        _settings.LunarMonthNames.Should().HaveCount(13);
        _settings.LunarMonthNames[12].Should().Be("Luna 13");
    }

    [Fact]
    public void Znak_nowej_linii_w_nazwie_nie_rozsypuje_listy()
    {
        // Nowa linia jest tu separatorem. Z pola jednowierszowego nie przyjdzie, ale
        // z wklejenia albo z ręcznej poprawki bazy — owszem, i wtedy przesunęłaby numery.
        _settings.SetLunarMonthNames(["Luna\nMrozu", "Luna Śniegu"]);

        var znowu = new LocalSettings(_db);

        znowu.LunarMonthNames.Should().HaveCount(13);
        znowu.LunarMonthNames[0].Should().Be("Luna Mrozu");
        znowu.LunarMonthNames[1].Should().Be("Luna Śniegu");
    }

    [Fact]
    public void Ustawienia_ksiezycowe_nie_trafiaja_do_dziennika_zmian()
    {
        // Tabela ustawień jest urządzeniowa i celowo niesynchronizowana — tak samo jak
        // motyw i strefa. Nazwy wpisane na pulpicie nie mają jechać na telefon, bo to
        // decyzja o tym, co pokazuje ten ekran.
        _settings.SetLunarShown(true);
        _settings.SetLunarNamesShown(true);
        _settings.SetLunarMonthNames(["Luna Mrozu"]);

        _db.Changes.Should().BeEmpty();
    }

    private sealed class NewZone(string id) : ISettings
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

        public bool DailyBackup => false;

        public void SetDailyBackup(bool on) => throw new NotSupportedException();

        public string? BackupFolder => null;

        public void SetBackupFolder(string? path) => throw new NotSupportedException();

        public bool LunarShown { get; set; }

        public void SetLunarShown(bool on) => LunarShown = on;

        public bool LunarNamesShown { get; set; }

        public void SetLunarNamesShown(bool on) => LunarNamesShown = on;

        public List<string> MonthNames { get; private set; } = [.. Enumerable.Repeat(string.Empty, 13)];

        public IReadOnlyList<string> LunarMonthNames => MonthNames;

        public void SetLunarMonthNames(IReadOnlyList<string> names) => MonthNames = [.. names];
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
        var wUtc = new SystemClock(new NewZone("UTC"));
        var wWarszawie = new SystemClock(new NewZone("Europe/Warsaw"));

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
        _connection.Dispose();
    }
}

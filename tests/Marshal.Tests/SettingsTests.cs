using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Infrastructure.Data;
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
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_polaczenie)
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
    }

    private sealed class Strefa(string id) : ISettings
    {
        public TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById(id);

        public ThemeChoice Theme => ThemeChoice.System;

        public void SetZone(string id) => throw new NotSupportedException();

        public void SetTheme(ThemeChoice theme) => throw new NotSupportedException();
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
        var zegar = new StalyMoment(
            new DateTimeOffset(2026, 9, 15, 0, 30, 0, TimeSpan.FromHours(2)));

        zegar.Today.Should().Be(new DateOnly(2026, 9, 15));
    }

    public void Dispose()
    {
        _db.Dispose();
        _polaczenie.Dispose();
    }
}

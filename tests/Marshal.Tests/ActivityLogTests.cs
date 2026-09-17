using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Domain.Diagnostics;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Dziennik tego, co aplikacja zrobiła (spec 12).
/// </summary>
public sealed class ActivityLogTests : IDisposable
{
    private sealed class Zegar : IClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 9, 17, 9, 0, 0, TimeSpan.FromHours(2));
    }

    private readonly SqliteConnection _polaczenie = new("Filename=:memory:");
    private readonly DbContextOptions<MarshalDbContext> _opcje;
    private readonly Zegar _zegar = new();
    private readonly ActivityLog _dziennik;

    public ActivityLogTests()
    {
        _polaczenie.Open();
        _opcje = new DbContextOptionsBuilder<MarshalDbContext>()
            .UseSqlite(_polaczenie)
            .Options;

        using (var db = new MarshalDbContext(_opcje))
        {
            db.Database.Migrate();
        }

        _dziennik = new ActivityLog(_opcje, _zegar);
    }

    [Fact]
    public async Task Wpis_zapisuje_sie_i_wraca()
    {
        await _dziennik.RecordAsync("Kalendarz: pobranie", "11 kalendarzy, 52 wydarzenia");

        var wpis = (await _dziennik.RecentAsync()).Single();

        wpis.Operation.Should().Be("Kalendarz: pobranie");
        wpis.Outcome.Should().Be("11 kalendarzy, 52 wydarzenia");
        wpis.Level.Should().Be(ActivityLevel.Ok);
        wpis.At.Should().Be(_zegar.Now);
    }

    [Fact]
    public async Task Najnowsze_ida_na_gore()
    {
        await _dziennik.RecordAsync("Pierwsza", "-");
        _zegar.Now = _zegar.Now.AddMinutes(1);
        await _dziennik.RecordAsync("Druga", "-");

        (await _dziennik.RecentAsync()).Select(w => w.Operation)
            .Should().ContainInOrder("Druga", "Pierwsza");
    }

    [Fact]
    public async Task Zapis_dziennika_nie_przerywa_operacji()
    {
        // Awaria dziennika nie może być gorsza od braku dziennika: opisanie operacji
        // nie ma prawa jej wywrócić. Ale nie znika bez śladu — licznik widać na ekranie.
        var zepsuty = new ActivityLog(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite("Data Source=/nie-ma-takiego-katalogu/marshal.db")
                .Options,
            _zegar);

        var zapis = async () => await zepsuty.RecordAsync("Cokolwiek", "-");

        await zapis.Should().NotThrowAsync();
        zepsuty.Dropped.Should().Be(1);
    }

    [Fact]
    public async Task Dziennik_nie_rosnie_bez_konca()
    {
        // Wszystkie wpisy z jedną datą: tak właśnie wygląda pętla, która coś raportuje
        // przy każdym obiegu. Kasowanie „od daty granicznej w dół" zabrałoby tu
        // cały dziennik naraz.
        for (var i = 0; i < 610; i++)
        {
            await _dziennik.RecordAsync($"Wpis {i}", "-");
        }

        await using var db = new MarshalDbContext(_opcje);

        // Nie równo 500: sprzątanie rusza dopiero po przekroczeniu limitu o zapas,
        // żeby kasowanie szło raz na sto wpisów, a nie przy każdym. Umowa brzmi
        // „ograniczony i gubi najstarsze", a nie „zawsze dokładnie 500".
        db.ActivityEntries.Count().Should().BeInRange(500, 600);
        db.ActivityEntries.Any(w => w.Operation == "Wpis 0").Should().BeFalse();
        db.ActivityEntries.Any(w => w.Operation == "Wpis 609").Should().BeTrue();
    }

    [Fact]
    public async Task Czyszczenie_oproznia_dziennik()
    {
        await _dziennik.RecordAsync("Coś", "-");
        await _dziennik.ClearAsync();

        (await _dziennik.RecentAsync()).Should().BeEmpty();
    }

    public void Dispose() => _polaczenie.Dispose();
}

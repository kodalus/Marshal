using FluentAssertions;
using Marshal.Domain.Primitives;
using Marshal.Domain.Sync;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Sync;
using Marshal.Infrastructure.Time;
using Marshal.Application.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Tożsamość urządzenia i ciągłość zegara logicznego między uruchomieniami.
/// Jedno i drugie zawodzi bez wyjątku — po prostu część zmian nie dociera.
/// </summary>
public sealed class DeviceIdentityTests : IDisposable
{
    private sealed class Zegar : IClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(2));
    }

    private readonly SqliteConnection _polaczenie = new("Filename=:memory:");

    public DeviceIdentityTests()
    {
        _polaczenie.Open();
        using var db = Baza();
        db.Database.Migrate();
    }

    private MarshalDbContext Baza() => new(
        new DbContextOptionsBuilder<MarshalDbContext>()
            .UseSqlite(_polaczenie)
            .AddInterceptors(new ChangeJournalInterceptor())
            .Options);

    [Fact]
    public void Identyfikator_nadany_raz_wraca_ten_sam_po_ponownym_uruchomieniu()
    {
        using var pierwsze = Baza();
        var id = new DeviceIdentity(pierwsze).Id;

        using var drugie = Baza();
        new DeviceIdentity(drugie).Id.Should().Be(id);
    }

    [Fact]
    public void Identyfikator_nadaje_sie_na_nazwe_pliku()
    {
        // Trafia do nazw porcji w składnicy, więc same małe litery, cyfry i myślnik.
        using var db = Baza();

        new DeviceIdentity(db).Id.Should()
            .MatchRegex("^[a-z0-9][a-z0-9-]*$").And.NotBeEmpty();
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("")]
    [InlineData("Ala-ma-kota 2!")]
    public void Dwa_urzadzenia_o_tej_samej_nazwie_dostaja_rozne_identyfikatory(string name)
    {
        // Na Androidzie MachineName zwraca „localhost" na każdym urządzeniu. Gdyby
        // identyfikator brał się z niej wprost, dwa telefony pisałyby do tego samego
        // dziennika i każdy uznawałby dziennik drugiego za własny.
        DeviceIdentity.Generate(name).Should().NotBe(DeviceIdentity.Generate(name));
    }

    [Fact]
    public void Zegar_wznawia_sie_ponad_ostatni_wydany_znacznik()
    {
        // Zegar ścienny cofnięty między uruchomieniami — poprawka z serwera czasu
        // albo rozładowana bateria podtrzymania. Bez wznowienia nowa zmiana dostałaby
        // znacznik wcześniejszy od już wysłanej i przepadłaby przy scalaniu.
        var zegar = new Zegar();
        Hlc last;

        using (var pierwsze = Baza())
        {
            var id = new DeviceIdentity(pierwsze).Id;
            var hlc = new HlcSource(zegar, id, LastHlcStore.Read(pierwsze, id));
            pierwsze.Tasks.Add(TaskItem.Capture("kupić mleko", zegar.Now, hlc.Next()));
            pierwsze.SaveChanges();
            last = hlc.Last;
        }

        zegar.Now = zegar.Now.AddHours(-5);

        using var drugie = Baza();
        var id2 = new DeviceIdentity(drugie).Id;
        var resumed = new HlcSource(zegar, id2, LastHlcStore.Read(drugie, id2));

        resumed.Next().Should().BeGreaterThan(last);
    }

    [Fact]
    public void Znacznik_obcego_urzadzenia_nie_jest_wznawiany()
    {
        // Baza przeniesiona z innego urządzenia. Zegar jest zegarem tego urządzenia
        // i tylko jego — wznowienie z cudzego znacznika dałoby znacznik z cudzym
        // identyfikatorem, czyli rozstrzyganie remisów przestałoby działać.
        using var db = Baza();
        db.LocalSettings.Add(new LocalSetting(
            LocalSetting.LastHlcKey, Hlc.Zero("obce").ToString()));
        db.SaveChanges();

        LastHlcStore.Read(db, "moje").Should().BeNull();
    }

    public void Dispose() => _polaczenie.Dispose();
}

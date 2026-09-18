using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Domain.Areas;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Brama na bazę i znak zapisu — dwie rzeczy, bez których automatyczna synchronizacja
/// byłaby drugą ręką sięgającą do tego samego kontekstu.
/// </summary>
public sealed class KolejkaBazyTests : IDisposable
{
    private sealed class Zegar : IClock
    {
        public DateTimeOffset Now => new(2026, 9, 18, 9, 0, 0, TimeSpan.FromHours(2));
    }

    private readonly SqliteConnection _polaczenie = new("Filename=:memory:");
    private readonly MarshalDbContext _db;

    public KolejkaBazyTests()
    {
        _polaczenie.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_polaczenie)
                .Options);
        _db.Database.Migrate();
    }

    public void Dispose()
    {
        _db.Dispose();
        _polaczenie.Dispose();
    }

    [Fact]
    public async Task Brama_nie_wpuszcza_drugiej_pracy_w_czasie_oczekiwania()
    {
        // Sedno: praca czeka w środku (await), a brama ma być dalej trzymana. Gdyby
        // puszczała na czas oczekiwania, wpuszczałaby drugą pracę dokładnie w tę
        // szczelinę, którą ma zamykać — a to jest ta szczelina, w którą wchodzi
        // synchronizacja ruszająca sama.
        var kolejka = new KolejkaBazy();
        var wewnatrz = 0;
        var najwiecejNaraz = 0;

        async Task Praca()
        {
            var teraz = Interlocked.Increment(ref wewnatrz);
            InterlockedMax(ref najwiecejNaraz, teraz);

            await Task.Delay(20);

            Interlocked.Decrement(ref wewnatrz);
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => kolejka.WykonajAsync(Praca)));

        najwiecejNaraz.Should().Be(1, "brama przepuszcza jedną pracę naraz");
    }

    [Fact]
    public async Task Brama_puszcza_po_wywrotce()
    {
        // Bez tego jedna awaria zamykałaby bazę do końca działania aplikacji.
        var kolejka = new KolejkaBazy();

        // Jawny typ, bo samo „rzuć" pasuje do obu przeciążeń bramy naraz.
        Func<Task> wywrotka = () => Task.FromException(new InvalidOperationException("celowo"));

        var wybuch = async () => await kolejka.WykonajAsync(wywrotka);

        await wybuch.Should().ThrowAsync<InvalidOperationException>();

        var doszlo = false;
        await kolejka.WykonajAsync(() =>
        {
            doszlo = true;
            return Task.CompletedTask;
        });

        doszlo.Should().BeTrue("semafor ma być oddany także wtedy, gdy praca się wywróciła");
    }

    [Fact]
    public async Task Zapis_z_okna_podnosi_znak()
    {
        var sygnal = new SygnalZapisu();
        var podniesiony = 0;
        sygnal.Zapisano += () => podniesiony++;

        var praca = new UnitOfWork(_db, new KolejkaBazy(), sygnal);

        var hlc = new HlcSource(new Zegar(), "testy");
        _db.Areas.Add(new Area(Guid.CreateVersion7(), new Zegar().Now, hlc.Next(), "Dom", 0));
        await praca.SaveChangesAsync();

        podniesiony.Should().Be(1, "to jedyny znak, po którym synchronizacja wie, że jest co wysyłać");
    }

    [Fact]
    public async Task Zapis_bez_zmian_znaku_nie_podnosi()
    {
        // Odświeżenia i zapisy „na wszelki wypadek" zdarzają się często. Gdyby każdy
        // z nich prosił o przebieg, aplikacja chodziłaby po Dysku bez treści.
        var sygnal = new SygnalZapisu();
        var podniesiony = 0;
        sygnal.Zapisano += () => podniesiony++;

        var praca = new UnitOfWork(_db, new KolejkaBazy(), sygnal);

        await praca.SaveChangesAsync();

        podniesiony.Should().Be(0);
    }

    private static void InterlockedMax(ref int cel, int wartosc)
    {
        int stary;

        do
        {
            stary = Volatile.Read(ref cel);

            if (stary >= wartosc)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref cel, wartosc, stary) != stary);
    }
}

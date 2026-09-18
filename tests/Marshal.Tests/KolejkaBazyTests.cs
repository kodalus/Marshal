using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Domain.Areas;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Repositories;
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
    public async Task Brama_wpuszcza_ponownie_tego_kto_jest_w_srodku()
    {
        // Odkąd przez bramę idą też odczyty, zagnieżdżenie zdarza się na każdej
        // drodze: synchronizacja bierze bramę na całą porcję i woła w środku
        // repozytoria, a te wołają bramę. Bez wznawiania czekałaby na zwolnienie
        // przez samą siebie — czyli zawisłaby, i to cicho.
        var kolejka = new KolejkaBazy();
        var doszlo = false;

        await kolejka.WykonajAsync(async () =>
        {
            await kolejka.WykonajAsync(() =>
            {
                doszlo = true;
                return Task.CompletedTask;
            });
        });

        doszlo.Should().BeTrue("brama ma wpuszczać ponownie ten sam przepływ wywołania");

        // A po wyjściu ma być znowu wolna — inaczej pierwsze zagnieżdżenie
        // zamykałoby ją na dobre.
        var pozniej = false;
        await kolejka.WykonajAsync(() =>
        {
            pozniej = true;
            return Task.CompletedTask;
        });

        pozniej.Should().BeTrue();
    }

    [Fact]
    public async Task Odczyt_z_repozytorium_czeka_na_brame()
    {
        // Brama pilnująca samych zapisów przepuszczała odczyty wprost na kontekst
        // i stąd brał się błąd o drugiej operacji zaczętej przed końcem pierwszej.
        // Sprawdzenie wprost: praca trzyma bramę, odczyt z repozytorium ma stać.
        var kolejka = new KolejkaBazy();
        var zadania = new TaskRepository(_db, kolejka);

        var puscic = new TaskCompletionSource();

        var trzymana = kolejka.WykonajAsync(() => puscic.Task);

        // Odpalony po zajęciu bramy, więc jeśli kiedykolwiek się skończy przed
        // zwolnieniem, znaczy, że bramę ominął.
        var odczyt = Task.Run(() => zadania.InboxCountAsync());

        var ktoPierwszy = await Task.WhenAny(odczyt, Task.Delay(200));

        ktoPierwszy.Should().NotBe(odczyt, "odczyt ma czekać na swoją kolej tak samo jak zapis");

        puscic.SetResult();
        await trzymana;

        (await odczyt).Should().Be(0, "po zwolnieniu bramy odczyt ma dojść do skutku");
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

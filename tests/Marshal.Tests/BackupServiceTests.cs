using System.Text;
using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Domain.Areas;
using Marshal.Domain.Primitives;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Backup;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Sync;
using Marshal.Infrastructure.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Kopia zapasowa: wszystko do jednego pliku JSON i z powrotem (spec 12).
/// </summary>
public sealed class BackupServiceTests : IDisposable
{
    private sealed class Zegar : IClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 9, 16, 9, 0, 0, TimeSpan.FromHours(2));
    }

    private sealed class Baza(string urzadzenie, Zegar zegar) : IDisposable
    {
        private readonly SqliteConnection _polaczenie = new("Filename=:memory:");

        public MarshalDbContext Db { get; private set; } = null!;

        public HlcSource Hlc { get; private set; } = null!;

        public BackupService Kopia { get; private set; } = null!;

        public Baza Otworz()
        {
            _polaczenie.Open();
            Db = new MarshalDbContext(
                new DbContextOptionsBuilder<MarshalDbContext>()
                    .UseSqlite(_polaczenie)
                    .AddInterceptors(new ChangeJournalInterceptor())
                    .Options);
            Db.Database.Migrate();
            Hlc = new HlcSource(zegar, urzadzenie);
            Kopia = new BackupService(Db, Hlc, zegar, new StaleId(urzadzenie));

            return this;
        }

        public void Dispose()
        {
            Db.Dispose();
            _polaczenie.Dispose();
        }
    }

    private sealed class StaleId(string id) : IDeviceIdentity
    {
        public string Id { get; } = id;
    }

    private readonly Zegar _zegar = new();
    private readonly Baza _biurko;

    public BackupServiceTests()
    {
        _biurko = new Baza("biurko", _zegar).Otworz();
    }

    private static MemoryStream Plik(byte[] bajty) => new(bajty);

    private static async Task<byte[]> EksportAsync(BackupService kopia)
    {
        using var strumien = new MemoryStream();
        await kopia.ExportAsync(strumien);
        return strumien.ToArray();
    }

    private TaskItem Zadanie(Baza baza, string tytul, Guid obszar)
    {
        var zadanie = TaskItem.Capture(tytul, _zegar.Now, baza.Hlc.Next());
        zadanie.MakeNext(obszar, baza.Hlc.Next());
        baza.Db.Tasks.Add(zadanie);
        baza.Db.SaveChanges();

        return zadanie;
    }

    private Guid Obszar(Baza baza, string nazwa)
    {
        var obszar = new Area(Guid.CreateVersion7(), _zegar.Now, baza.Hlc.Next(), nazwa, sortOrder: 0);
        baza.Db.Areas.Add(obszar);
        baza.Db.SaveChanges();

        return obszar.Id;
    }

    [Fact]
    public async Task Kopia_odtwarza_zadania_na_pustej_bazie()
    {
        var obszar = Obszar(_biurko, "Dom");
        Zadanie(_biurko, "kupić mleko", obszar);
        Zadanie(_biurko, "zadzwonić do przychodni", obszar);

        var plik = await EksportAsync(_biurko.Kopia);

        using var telefon = new Baza("telefon", _zegar).Otworz();
        var raport = await telefon.Kopia.ImportAsync(Plik(plik), ImportMode.Merge);

        raport.Applied.Should().BeGreaterThan(0);
        telefon.Db.Tasks.Select(z => z.Title)
            .Should().BeEquivalentTo(["kupić mleko", "zadzwonić do przychodni"]);
        telefon.Db.Areas.Should().Contain(o => o.Name == "Dom");
    }

    [Fact]
    public async Task Kopia_jest_czytelnym_tekstem_a_nie_zlepkiem_bajtow()
    {
        // Kopia, której nie da się obejrzeć notatnikiem, jest obietnicą,
        // a nie zabezpieczeniem.
        Zadanie(_biurko, "kupić mleko", Obszar(_biurko, "Dom"));

        var tekst = Encoding.UTF8.GetString(await EksportAsync(_biurko.Kopia));

        tekst.Should().Contain("kupić mleko");
        tekst.Should().Contain("\"Tasks\"");
        tekst.Should().Contain("\"wersja\"");
    }

    [Fact]
    public async Task Do_kopii_nie_wycieka_nic_lokalnego()
    {
        // Że nic nie zostało pominięte, wynika z budowy: usługa przechodzi po modelu,
        // a nie po wyliczonej liście zbiorów. Testu wymaga druga strona tej samej
        // reguły — że po modelu nie przeszło **za dużo**. Kursory, pokazane
        // przypomnienia i pobrane wydarzenia należą do urządzenia, nie do danych,
        // a dziennik zmian w kopii oznaczałby wysłanie wszystkiego na nowo.
        Zadanie(_biurko, "kupić mleko", Obszar(_biurko, "Dom"));

        var tekst = Encoding.UTF8.GetString(await EksportAsync(_biurko.Kopia));

        foreach (var lokalna in new[]
                 {
                     "Changes", "FieldStamps", "SyncCursors", "LocalSettings",
                     "ReminderShown", "CalendarEvents", "CalendarCursors",
                 })
        {
            tekst.Should().NotContain($"\"{lokalna}\"", $"tabela {lokalna} jest lokalna");
        }
    }

    [Fact]
    public async Task Nowsza_zmiana_lokalna_wygrywa_ze_starszym_wpisem_z_pliku()
    {
        var obszar = Obszar(_biurko, "Dom");
        var zadanie = Zadanie(_biurko, "stary tytuł", obszar);

        var plik = await EksportAsync(_biurko.Kopia);

        // Zmiana po zrobieniu kopii. Wgranie kopii nie może jej cofnąć — inaczej
        // odtworzenie byłoby cichą utratą wszystkiego, co powstało po eksporcie.
        _zegar.Now = _zegar.Now.AddHours(1);
        zadanie.Rename("nowy tytuł", _biurko.Hlc.Next());
        await _biurko.Db.SaveChangesAsync();

        await _biurko.Kopia.ImportAsync(Plik(plik), ImportMode.Merge);

        _biurko.Db.Tasks.Single().Title.Should().Be("nowy tytuł");
    }

    [Fact]
    public async Task Scalanie_idzie_pole_po_polu_a_nie_calym_rekordem()
    {
        var obszar = Obszar(_biurko, "Dom");
        var zadanie = Zadanie(_biurko, "kupić mleko", obszar);

        // Waga ustawiona przed kopią, tytuł zmieniony po niej. Gdyby cały rekord
        // jechał pod jednym znacznikiem, wgranie kopii albo cofnęłoby tytuł,
        // albo odrzuciło wagę.
        zadanie.SetPriority(Priority.High, _biurko.Hlc.Next());
        await _biurko.Db.SaveChangesAsync();

        var plik = await EksportAsync(_biurko.Kopia);

        _zegar.Now = _zegar.Now.AddHours(1);
        zadanie.Rename("kupić mleko i chleb", _biurko.Hlc.Next());
        await _biurko.Db.SaveChangesAsync();

        await _biurko.Kopia.ImportAsync(Plik(plik), ImportMode.Merge);

        var po = _biurko.Db.Tasks.Single();
        po.Title.Should().Be("kupić mleko i chleb");
        po.Priority.Should().Be(Priority.High);
    }

    [Fact]
    public async Task Podmiana_calosci_czysci_to_co_bylo()
    {
        var obszar = Obszar(_biurko, "Dom");
        Zadanie(_biurko, "było przed kopią", obszar);

        var plik = await EksportAsync(_biurko.Kopia);

        _zegar.Now = _zegar.Now.AddHours(1);
        Zadanie(_biurko, "powstało po kopii", obszar);

        await _biurko.Kopia.ImportAsync(Plik(plik), ImportMode.Replace);

        // Przy scalaniu drugie zadanie by zostało — jest nowsze niż cokolwiek
        // w pliku. Podmiana znaczy podmianę.
        _biurko.Db.Tasks.Select(z => z.Title).Should().BeEquivalentTo(["było przed kopią"]);
    }

    [Fact]
    public async Task Wgranie_kopii_nie_trafia_do_dziennika_zmian()
    {
        // Inaczej każdy odtworzony rekord poleciałby na Dysk jako świeża zmiana
        // i wskrzesił na drugim urządzeniu rzeczy skasowane po zrobieniu kopii.
        Zadanie(_biurko, "kupić mleko", Obszar(_biurko, "Dom"));
        var plik = await EksportAsync(_biurko.Kopia);

        using var telefon = new Baza("telefon", _zegar).Otworz();
        await telefon.Kopia.ImportAsync(Plik(plik), ImportMode.Merge);

        telefon.Db.Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task Wgranie_kopii_podnosi_zegar_logiczny()
    {
        // Bez tego kolejna zmiana na tym urządzeniu byłaby wcześniejsza niż to,
        // co przed chwilą przyszło z pliku, i przegrałaby przy scalaniu (spec 9.6).
        _zegar.Now = _zegar.Now.AddDays(3);
        Zadanie(_biurko, "kupić mleko", Obszar(_biurko, "Dom"));
        var plik = await EksportAsync(_biurko.Kopia);

        var wczesniej = new Zegar { Now = _zegar.Now.AddDays(-3) };
        using var telefon = new Baza("telefon", wczesniej).Otworz();

        await telefon.Kopia.ImportAsync(Plik(plik), ImportMode.Merge);

        telefon.Hlc.Last.WallMs.Should().BeGreaterThan(wczesniej.Now.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Nagrobek_z_kopii_kasuje_rekord_wskrzeszony_gdzie_indziej()
    {
        var obszar = Obszar(_biurko, "Dom");
        var zadanie = Zadanie(_biurko, "do skasowania", obszar);

        _zegar.Now = _zegar.Now.AddHours(1);
        zadanie.MarkDeleted(_biurko.Hlc.Next());
        await _biurko.Db.SaveChangesAsync();

        var plik = await EksportAsync(_biurko.Kopia);

        using var telefon = new Baza("telefon", _zegar).Otworz();
        await telefon.Kopia.ImportAsync(Plik(plik), ImportMode.Merge);

        // Usunięcie jest zawsze logiczne (spec 5.1), więc rekord jest w kopii —
        // z nagrobkiem, a nie przez nieobecność.
        telefon.Db.Tasks.Single().Deleted.Should().BeTrue();
    }

    [Fact]
    public async Task Plik_nie_bedacy_kopia_konczy_sie_bledem_a_nie_polowa_wgranej_bazy()
    {
        var wgranie = async () => await _biurko.Kopia.ImportAsync(
            Plik(Encoding.UTF8.GetBytes("{to nie jest kopia")), ImportMode.Merge);

        await wgranie.Should().ThrowAsync<Exception>();
    }

    public void Dispose() => _biurko.Dispose();
}

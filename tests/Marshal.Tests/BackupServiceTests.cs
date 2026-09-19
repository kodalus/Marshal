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

    private sealed class Baza(string device, Zegar zegar) : IDisposable
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
            Hlc = new HlcSource(zegar, device);
            Kopia = new BackupService(Db, Hlc, zegar, new StaleId(device));

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

    private static async Task<byte[]> EksportAsync(BackupService copy)
    {
        using var strumien = new MemoryStream();
        await copy.ExportAsync(strumien);
        return strumien.ToArray();
    }

    private TaskItem TaskId(Baza baza, string title, Guid area)
    {
        var task = TaskItem.Capture(title, _zegar.Now, baza.Hlc.Next());
        task.MakeNext(area, baza.Hlc.Next());
        baza.Db.Tasks.Add(task);
        baza.Db.SaveChanges();

        return task;
    }

    private Guid Obszar(Baza baza, string name)
    {
        var area = new Area(Guid.CreateVersion7(), _zegar.Now, baza.Hlc.Next(), name, sortOrder: 0);
        baza.Db.Areas.Add(area);
        baza.Db.SaveChanges();

        return area.Id;
    }

    [Fact]
    public async Task Kopia_odtwarza_zadania_na_pustej_bazie()
    {
        var area = Obszar(_biurko, "Dom");
        TaskId(_biurko, "kupić mleko", area);
        TaskId(_biurko, "zadzwonić do przychodni", area);

        var file = await EksportAsync(_biurko.Kopia);

        using var telefon = new Baza("telefon", _zegar).Otworz();
        var report = await telefon.Kopia.ImportAsync(Plik(file), ImportMode.Merge);

        report.Applied.Should().BeGreaterThan(0);
        telefon.Db.Tasks.Select(z => z.Title)
            .Should().BeEquivalentTo(["kupić mleko", "zadzwonić do przychodni"]);
        telefon.Db.Areas.Should().Contain(o => o.Name == "Dom");
    }

    [Fact]
    public async Task Kopia_jest_czytelnym_tekstem_a_nie_zlepkiem_bajtow()
    {
        // Kopia, której nie da się obejrzeć notatnikiem, jest obietnicą,
        // a nie zabezpieczeniem.
        TaskId(_biurko, "kupić mleko", Obszar(_biurko, "Dom"));

        var text = Encoding.UTF8.GetString(await EksportAsync(_biurko.Kopia));

        text.Should().Contain("kupić mleko");
        text.Should().Contain("\"Tasks\"");
        text.Should().Contain("\"wersja\"");
    }

    [Fact]
    public async Task Do_kopii_nie_wycieka_nic_lokalnego()
    {
        // Że nic nie zostało pominięte, wynika z budowy: usługa przechodzi po modelu,
        // a nie po wyliczonej liście zbiorów. Testu wymaga druga strona tej samej
        // reguły — że po modelu nie przeszło **za dużo**. Kursory, pokazane
        // przypomnienia i pobrane wydarzenia należą do urządzenia, nie do danych,
        // a dziennik zmian w kopii oznaczałby wysłanie wszystkiego na nowo.
        TaskId(_biurko, "kupić mleko", Obszar(_biurko, "Dom"));

        var text = Encoding.UTF8.GetString(await EksportAsync(_biurko.Kopia));

        foreach (var local in new[]
                 {
                     "Changes", "FieldStamps", "SyncCursors", "LocalSettings",
                     "ReminderShown", "CalendarEvents", "CalendarCursors",
                 })
        {
            text.Should().NotContain($"\"{local}\"", $"tabela {local} jest lokalna");
        }
    }

    [Fact]
    public async Task Nowsza_zmiana_lokalna_wygrywa_ze_starszym_wpisem_z_pliku()
    {
        var area = Obszar(_biurko, "Dom");
        var task = TaskId(_biurko, "stary tytuł", area);

        var file = await EksportAsync(_biurko.Kopia);

        // Zmiana po zrobieniu kopii. Wgranie kopii nie może jej cofnąć — inaczej
        // odtworzenie byłoby cichą utratą wszystkiego, co powstało po eksporcie.
        _zegar.Now = _zegar.Now.AddHours(1);
        task.Rename("nowy tytuł", _biurko.Hlc.Next());
        await _biurko.Db.SaveChangesAsync();

        await _biurko.Kopia.ImportAsync(Plik(file), ImportMode.Merge);

        _biurko.Db.Tasks.Single().Title.Should().Be("nowy tytuł");
    }

    [Fact]
    public async Task Scalanie_idzie_pole_po_polu_a_nie_calym_rekordem()
    {
        var area = Obszar(_biurko, "Dom");
        var task = TaskId(_biurko, "kupić mleko", area);

        // Waga ustawiona przed kopią, tytuł zmieniony po niej. Gdyby cały rekord
        // jechał pod jednym znacznikiem, wgranie kopii albo cofnęłoby tytuł,
        // albo odrzuciło wagę.
        task.SetPriority(Priority.High, _biurko.Hlc.Next());
        await _biurko.Db.SaveChangesAsync();

        var file = await EksportAsync(_biurko.Kopia);

        _zegar.Now = _zegar.Now.AddHours(1);
        task.Rename("kupić mleko i chleb", _biurko.Hlc.Next());
        await _biurko.Db.SaveChangesAsync();

        await _biurko.Kopia.ImportAsync(Plik(file), ImportMode.Merge);

        var po = _biurko.Db.Tasks.Single();
        po.Title.Should().Be("kupić mleko i chleb");
        po.Priority.Should().Be(Priority.High);
    }

    [Fact]
    public async Task Podmiana_calosci_czysci_to_co_bylo()
    {
        var area = Obszar(_biurko, "Dom");
        TaskId(_biurko, "było przed kopią", area);

        var file = await EksportAsync(_biurko.Kopia);

        _zegar.Now = _zegar.Now.AddHours(1);
        TaskId(_biurko, "powstało po kopii", area);

        await _biurko.Kopia.ImportAsync(Plik(file), ImportMode.Replace);

        // Przy scalaniu drugie zadanie by zostało — jest nowsze niż cokolwiek
        // w pliku. Podmiana znaczy podmianę.
        _biurko.Db.Tasks.Select(z => z.Title).Should().BeEquivalentTo(["było przed kopią"]);
    }

    [Fact]
    public async Task Wgranie_kopii_nie_trafia_do_dziennika_zmian()
    {
        // Inaczej każdy odtworzony rekord poleciałby na Dysk jako świeża zmiana
        // i wskrzesił na drugim urządzeniu rzeczy skasowane po zrobieniu kopii.
        TaskId(_biurko, "kupić mleko", Obszar(_biurko, "Dom"));
        var file = await EksportAsync(_biurko.Kopia);

        using var telefon = new Baza("telefon", _zegar).Otworz();
        await telefon.Kopia.ImportAsync(Plik(file), ImportMode.Merge);

        telefon.Db.Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task Wgranie_kopii_podnosi_zegar_logiczny()
    {
        // Bez tego kolejna zmiana na tym urządzeniu byłaby wcześniejsza niż to,
        // co przed chwilą przyszło z pliku, i przegrałaby przy scalaniu (spec 9.6).
        _zegar.Now = _zegar.Now.AddDays(3);
        TaskId(_biurko, "kupić mleko", Obszar(_biurko, "Dom"));
        var file = await EksportAsync(_biurko.Kopia);

        var wczesniej = new Zegar { Now = _zegar.Now.AddDays(-3) };
        using var telefon = new Baza("telefon", wczesniej).Otworz();

        await telefon.Kopia.ImportAsync(Plik(file), ImportMode.Merge);

        telefon.Hlc.Last.WallMs.Should().BeGreaterThan(wczesniej.Now.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Nagrobek_z_kopii_kasuje_rekord_wskrzeszony_gdzie_indziej()
    {
        var area = Obszar(_biurko, "Dom");
        var task = TaskId(_biurko, "do skasowania", area);

        _zegar.Now = _zegar.Now.AddHours(1);
        task.MarkDeleted(_biurko.Hlc.Next());
        await _biurko.Db.SaveChangesAsync();

        var file = await EksportAsync(_biurko.Kopia);

        using var telefon = new Baza("telefon", _zegar).Otworz();
        await telefon.Kopia.ImportAsync(Plik(file), ImportMode.Merge);

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

    [Fact]
    public async Task Pole_nigdy_nieustawione_nie_jedzie_w_kopii_jako_wyczyszczenie()
    {
        // Dziennik pomija puste pola przy zakładaniu rekordu, więc nie mają znacznika.
        // Gdyby kopia wypisywała je mimo to, byłyby jawnym „wyczyść to" ze znacznikiem
        // całej encji — i skasowałyby wartość nadaną w międzyczasie gdzie indziej.
        var area = Obszar(_biurko, "Dom");
        var task = TaskId(_biurko, "kupić mleko", area);

        var file = await EksportAsync(_biurko.Kopia);

        // Drugie urządzenie nadaje termin — czyli coś, czego oryginał nigdy nie miał.
        using var telefon = new Baza("telefon", _zegar).Otworz();
        await telefon.Kopia.ImportAsync(Plik(file), ImportMode.Merge);

        _zegar.Now = _zegar.Now.AddHours(1);
        var uNich = telefon.Db.Tasks.Single(z => z.Id == task.Id);
        uNich.SetDeadline(new DateOnly(2026, 10, 1), telefon.Hlc.Next());
        await telefon.Db.SaveChangesAsync();

        // Ta sama, stara kopia wgrana jeszcze raz nie ma prawa go zdjąć.
        await telefon.Kopia.ImportAsync(Plik(file), ImportMode.Merge);

        telefon.Db.Tasks.Single(z => z.Id == task.Id)
            .Deadline.Should().Be(new DateOnly(2026, 10, 1));
    }

    [Fact]
    public async Task Uszkodzona_wartosc_pomija_pole_a_nie_wywraca_wgrywania()
    {
        var area = Obszar(_biurko, "Dom");
        TaskId(_biurko, "kupić mleko", area);

        var text = Encoding.UTF8.GetString(await EksportAsync(_biurko.Kopia));

        // Liczba przesunięć przestawiona na tekst, którego nie da się wczytać w liczbę.
        var zepsuty = text.Replace("\"RollCount\": 0", "\"RollCount\": \"nie liczba\"");
        zepsuty.Should().NotBe(text, "podmiana miała trafić w plik");

        using var telefon = new Baza("telefon", _zegar).Otworz();
        var report = await telefon.Kopia.ImportAsync(
            Plik(Encoding.UTF8.GetBytes(zepsuty)), ImportMode.Merge);

        // Reszta rekordu wchodzi. Jedna zła wartość nie może blokować wszystkiego,
        // co przyszło po niej.
        report.Applied.Should().BeGreaterThan(0);
        telefon.Db.Tasks.Single().Title.Should().Be("kupić mleko");
    }

    [Fact]
    public async Task Nieudane_wgranie_z_podmiana_zostawia_baze_taka_jak_byla()
    {
        // Podmiana czyści tabele przed wgraniem. Bez transakcji błąd w połowie
        // zostawiłby bazę pustą i nieodtworzoną.
        TaskId(_biurko, "to ma zostać", Obszar(_biurko, "Dom"));

        var file = await EksportAsync(_biurko.Kopia);

        // Tytuł wyzerowany: plik jest poprawnym JSON-em i wgrywa się bez szemrania,
        // a wywraca się dopiero na zapisie, bo kolumna jest wymagana.
        var text = Encoding.UTF8.GetString(file).Replace("\"to ma zostać\"", "null");
        text.Should().NotBe(Encoding.UTF8.GetString(file), "podmiana miała trafić w plik");

        var wgranie = async () => await _biurko.Kopia.ImportAsync(
            Plik(Encoding.UTF8.GetBytes(text)), ImportMode.Replace);

        await wgranie.Should().ThrowAsync<Exception>();

        _biurko.Db.Tasks.Should().ContainSingle().Which.Title.Should().Be("to ma zostać");
    }

    [Fact]
    public async Task Podmiana_z_pliku_bez_wpisow_jest_odrzucana()
    {
        TaskId(_biurko, "to ma zostać", Obszar(_biurko, "Dom"));

        var pusty = Encoding.UTF8.GetBytes(
            """{"wersja":1,"utworzono":"2026-09-17T09:00:00+02:00","urzadzenie":"skads","wiersze":[]}""");

        var wgranie = async () => await _biurko.Kopia.ImportAsync(Plik(pusty), ImportMode.Replace);
        await wgranie.Should().ThrowAsync<InvalidDataException>();

        _biurko.Db.Tasks.Should().ContainSingle();
    }

    [Fact]
    public async Task Podmiana_z_pliku_o_nieznanych_tabelach_jest_odrzucana()
    {
        // Pominięcie nieznanej tabeli jest przy scalaniu słuszne, ale przy podmianie
        // znaczyłoby bazę wyczyszczoną i nieodtworzoną — po cichu.
        TaskId(_biurko, "to ma zostać", Obszar(_biurko, "Dom"));

        var text = Encoding.UTF8.GetString(await EksportAsync(_biurko.Kopia))
            .Replace("\"e\": \"", "\"e\": \"Obce");

        var wgranie = async () => await _biurko.Kopia.ImportAsync(
            Plik(Encoding.UTF8.GetBytes(text)), ImportMode.Replace);

        await wgranie.Should().ThrowAsync<InvalidDataException>();

        _biurko.Db.ChangeTracker.Clear();
        _biurko.Db.Tasks.Should().ContainSingle().Which.Title.Should().Be("to ma zostać");
    }

    public void Dispose() => _biurko.Dispose();
}

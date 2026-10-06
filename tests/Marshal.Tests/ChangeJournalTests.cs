using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Domain.Primitives;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Series;
using Marshal.Domain.Sync;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

public sealed class ChangeJournalTests : IDisposable
{
    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; } = new(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(2));
    }

    private readonly SqliteConnection _connection;
    private readonly MarshalDbContext _db;
    private readonly Guid _area = Guid.CreateVersion7();
    private long _stamp = 1000;

    public ChangeJournalTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.Migrate();
    }

    private Hlc Stamp() => new(_stamp += 10, 0, "biurko");

    private TaskItem Save(string title = "Zadzwonić do przychodni")
    {
        var task = TaskItem.Capture(title, new Clock().Now, Stamp());
        _db.Tasks.Add(task);
        _db.SaveChanges();
        return task;
    }

    [Fact]
    public void Nowa_encja_zapisuje_wpis_dla_kazdego_wypelnionego_pola()
    {
        Save();

        var entries = _db.Changes.ToList();
        entries.Should().NotBeEmpty();
        entries.Should().OnlyContain(w => w.EntityType == "Tasks");
        entries.Select(w => w.Field).Should().Contain(["Title", "State", "CreatedAt", "UpdatedAt"]);
    }

    /// <summary>
    /// Znacznik dopisany w tym samym kontekście, a jeszcze niezapisany, nie ma
    /// prowadzić do dopisania drugiego z tym samym kluczem.
    /// </summary>
    /// <remarks>
    /// Tak robi nakładanie zmian z synchronizacji: dokłada znaczniki i zostawia je
    /// do wspólnego zapisu. Zapytanie o znaczniki szło wtedy wyłącznie do bazy,
    /// w której ich jeszcze nie było — i zapis wywracał się na dwóch instancjach
    /// tego samego wiersza. Widać to było jako błąd przy zapisie zadania, choć
    /// z zadaniem nie miało nic wspólnego.
    /// </remarks>
    [Fact]
    public void Znacznik_dopisany_i_niezapisany_nie_powiela_sie_przy_zapisie()
    {
        var task = Save();

        // Notatka jest pusta przy zakładaniu, więc **nie ma jeszcze swojego znacznika** —
        // ani w bazie, ani w śledzeniu. To jest dokładnie ten stan, w którym nakładanie
        // zmian z synchronizacji dokłada znacznik i zostawia go do wspólnego zapisu.
        _db.Add(new FieldStamp("Tasks", task.Id, "Note", new Hlc(9999, 0, "telefon").ToString()));

        task.SetNote("z drugiego urządzenia", Stamp());

        // Bez poprawki leci tu wyjątek o instancji, której nie da się śledzić:
        // zapytanie o znaczniki szło wyłącznie do bazy i tego dopisanego nie widziało.
        _db.Invoking(db => db.SaveChanges()).Should().NotThrow();

        _db.FieldStamps.Count(z => z.EntityId == task.Id && z.Field == "Note")
            .Should().Be(1, "jeden znacznik na pole, niezależnie od tego, kto go dopisał");
    }

    [Fact]
    public void Klucz_glowny_nie_trafia_do_dziennika_jako_pole()
    {
        Save();

        _db.Changes.Select(w => w.Field).Should().NotContain("Id");
    }

    [Fact]
    public void Puste_pola_nowej_encji_nie_zasmiecaja_dziennika()
    {
        Save();

        _db.Changes.Select(w => w.Field).Should().NotContain(["Deadline", "WaitingForWho", "ProjectId"]);
    }

    [Fact]
    public void Zmiana_zapisuje_wylacznie_pola_faktycznie_zmienione()
    {
        var task = Save();
        var poDodaniu = _db.Changes.Count();

        task.Rename("Zadzwonić do przychodni po skierowanie", Stamp());
        _db.SaveChanges();

        var fresh = _db.Changes.Skip(poDodaniu).ToList();
        fresh.Select(w => w.Field).Should().BeEquivalentTo("Title", "UpdatedAt");
    }

    [Fact]
    public void Wpis_niesie_wartosc_w_postaci_bazodanowej()
    {
        Save("kupić mleko");

        _db.Changes.Single(w => w.Field == "Title").Value.Should().Be("\"kupić mleko\"");

        // Stan jest enumem konwertowanym na liczbę — dziennik niesie liczbę,
        // nie nazwę, więc nie zależy od nazw w kodzie.
        _db.Changes.Single(w => w.Field == "State").Value.Should().Be("0");
    }

    [Fact]
    public void Polskie_znaki_nie_sa_uciekane()
    {
        // Format tekstowy ma sens tylko wtedy, gdy da się go czytać. Domyślny
        // serializator zamieniłby każdą polską literę na sekwencję \uXXXX.
        Save("zażółć gęślą jaźń");

        var value = _db.Changes.Single(w => w.Field == "Title").Value;

        value.Should().NotContain("\\u");
        value.Should().Contain("zażółć gęślą jaźń");
    }

    [Fact]
    public void Znacznik_wpisu_jest_znacznikiem_encji()
    {
        var task = Save();

        _db.Changes.Should().OnlyContain(w => w.Hlc == task.UpdatedAt.ToString());
    }

    [Fact]
    public void Znaczniki_pol_powstaja_i_sa_podnoszone_przy_zmianie()
    {
        var task = Save();
        var titleBefore = _db.FieldStamps.Single(f => f.Field == "Title").Hlc;

        task.Rename("inny tytuł", Stamp());
        _db.SaveChanges();

        var titleAfter = _db.FieldStamps.Single(f => f.Field == "Title").Hlc;
        titleAfter.Should().NotBe(titleBefore);
        Hlc.Parse(titleAfter).Should().BeGreaterThan(Hlc.Parse(titleBefore));
    }

    [Fact]
    public void Pole_nietkniete_zachowuje_swoj_wczesniejszy_znacznik()
    {
        // To jest cały sens tabeli: bez niej po zmianie tytułu nie dałoby się
        // stwierdzić, że lokalna waga pochodzi sprzed tej zmiany.
        var task = Save();
        var stateBefore = _db.FieldStamps.Single(f => f.Field == "State").Hlc;

        task.Rename("inny tytuł", Stamp());
        _db.SaveChanges();

        _db.FieldStamps.Single(f => f.Field == "State").Hlc.Should().Be(stateBefore);
    }

    [Fact]
    public void Kazde_pole_ma_dokladnie_jeden_znacznik()
    {
        var task = Save();
        task.Rename("raz", Stamp());
        _db.SaveChanges();
        task.Rename("dwa", Stamp());
        _db.SaveChanges();

        _db.FieldStamps.Count(f => f.Field == "Title").Should().Be(1);
    }

    [Fact]
    public void Dziennik_nie_zapisuje_samego_siebie()
    {
        Save();

        _db.Changes.Select(w => w.EntityType).Should().NotContain(["Changes", "FieldStamps"]);
    }

    [Fact]
    public void Wpisy_zaczynaja_jako_niewyslane()
    {
        Save();

        _db.Changes.Should().OnlyContain(w => !w.Sent);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    /// <summary>Wystąpienie postawione z okna serii — dokładnie takie, jak wylicza seria.</summary>
    private (TaskSeries Series, TaskItem Day) Occurrence()
    {
        var starts = new DateOnly(2026, 10, 6);

        var series = TaskSeries.Create(
            starts,
            new RecurrenceRule(RecurrenceKind.Daily),
            new SeriesTemplate("Śmieci", AreaId: _area),
            DateTimeOffset.UtcNow,
            Stamp());

        var slot = new SeriesSlot(OccurrenceId.For(series.Id, starts), starts, null, null);
        var day = TaskItem.InSeries(series.Id, slot, series.Template, DateTimeOffset.UtcNow, Stamp());

        _db.TaskSeries.Add(series);
        _db.Tasks.Add(day);
        _db.SaveChanges();

        return (series, day);
    }

    [Fact]
    public void Wystapienie_wyliczalne_z_serii_nie_wchodzi_do_dziennika()
    {
        // Zdjęcie przyczyny, a nie łata na skutki. Dziennik zapisuje osobno każde pole
        // i osobno jego znacznik — około sześćdziesięciu wierszy na jedno wystąpienie.
        // Przy kilkunastu seriach po kilkadziesiąt dni to dziesiątki tysięcy wierszy
        // do wysłania i do nałożenia po drugiej stronie: stąd scalanie trwające minuty
        // i zatkana brama na bazę.
        //
        // A wysyłać tego nie trzeba: tożsamość wystąpienia liczy się z serii i dnia,
        // więc drugie urządzenie dojdzie do tych samych wierszy samo.
        var (series, day) = Occurrence();

        _db.Changes.Should().NotContain(
            w => w.EntityId == day.Id, "dzień wyliczalny z serii nie jedzie synchronizacją");

        _db.Changes.Should().Contain(
            w => w.EntityId == series.Id, "ale sama seria jedzie — z niej liczy się reszta");
    }

    [Fact]
    public void Odhaczone_wystapienie_jedzie_w_calosci()
    {
        // W chwili, w której dzień przestaje być tym, co okno wylicza, druga strona
        // musi dostać go **w całości**. Same zmienione pola opisywałyby wiersz, którego
        // tamta strona może jeszcze nie mieć — jej okno bywa krótsze — a wtedy
        // z „zrobione" bez nazwy i bez dnia powstałoby zadanie-widmo.
        var (_, day) = Occurrence();

        day.Complete(DateTimeOffset.UtcNow, Stamp());
        _db.SaveChanges();

        var pola = _db.Changes.Where(w => w.EntityId == day.Id).Select(w => w.Field).ToList();

        pola.Should().Contain("State");
        pola.Should().Contain("CompletedAt");
        pola.Should().Contain(nameof(TaskItem.Title), "bez nazwy powstałoby zadanie-widmo");
        pola.Should().Contain(nameof(TaskItem.DoDate), "i bez dnia też");
        pola.Should().Contain(nameof(TaskItem.SeriesId), "razem z przynależnością do serii");
    }

    [Fact]
    public void Dalsze_zmiany_odhaczonego_jada_juz_zwyklymi_polami()
    {
        // Całość tylko przy wyjściu z wyliczalności. Potem wiersz jest po obu stronach
        // i powtarzanie go w komplecie przy każdej zmianie byłoby tym samym marnotrawstwem,
        // przed którym cała ta zmiana ma chronić.
        var (_, day) = Occurrence();

        day.Complete(DateTimeOffset.UtcNow, Stamp());
        _db.SaveChanges();
        _db.Changes.RemoveRange(_db.Changes.Where(w => w.EntityId == day.Id));
        _db.SaveChanges();

        day.Rename("Śmieci — szkło", Stamp());
        _db.SaveChanges();

        _db.Changes.Where(w => w.EntityId == day.Id).Select(w => w.Field)
            .Should().BeEquivalentTo([nameof(TaskItem.Title), nameof(Entity.UpdatedAt)]);
    }

    [Fact]
    public void Zmiana_wystapienia_z_reki_jedzie_w_calosci()
    {
        // Objaw, który to wymusił: skrócenie jednego dnia pracy rozciągnięciem bloku
        // na siatce. Wiersz zostawał zaplanowany i nietknięty, więc wypadał z dziennika
        // jako „wyliczalny" — a wyliczalny znaczy „druga strona policzy go sama",
        // czego o zmianie powiedzieć nie można. Na telefonie dzień stał dalej
        // pełnowymiarowy, a skrócenie poszło przy tym do kalendarza Google: ta sama
        // rzecz widoczna dwa razy, przy czym krótsza jako wydarzenie cudze.
        var (_, day) = Occurrence();

        day.SetEstimate(90, day.Energy, Stamp());
        _db.SaveChanges();

        var pola = _db.Changes.Where(w => w.EntityId == day.Id).Select(w => w.Field).ToList();

        pola.Should().Contain(nameof(TaskItem.EstimatedMinutes), "zmiana jedzie");
        pola.Should().Contain(
            nameof(TaskItem.Title), "i w całości, bo tamta strona może nie mieć wiersza");
        pola.Should().Contain(nameof(TaskItem.DoDate), "i bez dnia też");
    }

    [Fact]
    public void Wskazanie_na_odbicie_w_kalendarzu_jedzie_razem_z_wierszem()
    {
        // To jest ta część objawu, która zostawała na ekranie: bez wskazania druga
        // strona nie ma po czym poznać, że wydarzenie w Google jest odbiciem jej
        // własnego zadania — więc rysuje je obok jako wydarzenie cudze.
        var (_, day) = Occurrence();

        day.Share(Guid.CreateVersion7(), "wydarzenie-google", Stamp());
        _db.SaveChanges();

        _db.Changes.Where(w => w.EntityId == day.Id).Select(w => w.Field)
            .Should().Contain(nameof(TaskItem.SharedEventId));
    }

    [Fact]
    public void Przepisanie_z_szablonu_serii_nie_jedzie_dziennikiem()
    {
        // Jedyny wyjątek od „wiersz zmieniany jedzie": przepisanie wystąpień po zmianie
        // serii, która nie przestawia dni. Zmianę niesie wtedy wiersz serii, a druga
        // strona przepisze z niego swoje dni sama.
        var (_, day) = Occurrence();

        _db.Changes.RemoveRange(_db.Changes.ToList());
        _db.SaveChanges();

        using (SeriesScope.Begin())
        {
            day.Restamp(new SeriesTemplate("Śmieci — szkło", AreaId: _area), null, null, Stamp);
            _db.SaveChanges();
        }

        _db.Changes.Should().NotContain(w => w.EntityId == day.Id);
    }
}

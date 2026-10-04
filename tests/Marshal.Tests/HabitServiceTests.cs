using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.UseCases;
using Marshal.Domain.Habits;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Repositories;
using Marshal.Infrastructure.Sync;
using Marshal.Infrastructure.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Nawyki: siatka dni, seria i dopisywanie dzisiejszego.
/// </summary>
/// <remarks>
/// Cała wartość nawyku siedzi w historii, więc testy pilnują przede wszystkim liczb:
/// seria liczona od dziś wstecz, rekord i licznik od początku, a nie z okna siatki.
/// </remarks>
public sealed class HabitServiceTests : IDisposable
{
    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 10, 4, 9, 0, 0, TimeSpan.FromHours(2));
    }

    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly Clock _clock = new();
    private readonly HlcSource _hlc;
    private readonly HabitRepository _store;
    private readonly HabitService _habits;

    public HabitServiceTests()
    {
        _connection.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.Migrate();

        _hlc = new HlcSource(_clock, "biurko");
        _store = new HabitRepository(_db);
        _habits = new HabitService(_store, new UnitOfWork(_db), _clock, _hlc);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // Przez interfejs, bo „dzisiaj" jest jego domyślną metodą, a nie polem atrapy.
    private DateOnly Today => ((IClock)_clock).Today;

    private void Mark(Guid habitId, DateOnly day, int amount = 1)
    {
        var mark = HabitMark.On(habitId, day, _clock.Now, _hlc.Next());
        mark.Set(amount, _hlc.Next());
        _db.HabitMarks.Add(mark);
        _db.SaveChanges();
    }

    [Fact]
    public async Task Tabela_nawykow_powstaje_razem_z_reszta_bazy()
    {
        // Migracja pisana ręcznie — ten test jest jedynym miejscem, w którym wyjdzie,
        // że kolumna w niej nie zgadza się z modelem.
        var habit = await _habits.CreateAsync("Medytacja");

        (await _store.FindAsync(habit.Id)).Should().NotBeNull();
        (await _store.ListAsync()).Should().ContainSingle().Which.Title.Should().Be("Medytacja");
    }

    [Fact]
    public async Task Dotkniecie_nawyku_na_ptaszek_przestawia_a_nie_dolicza()
    {
        // Nawyk bez progu ma dwa stany. Drugie dotknięcie ma zdejmować ptaszek,
        // a nie robić „dwa razy medytacja".
        var habit = await _habits.CreateAsync("Medytacja");

        (await _habits.BumpAsync(habit.Id)).Should().Be(1);
        (await _habits.BumpAsync(habit.Id)).Should().Be(0);
        (await _habits.BumpAsync(habit.Id)).Should().Be(1);

        _db.HabitMarks.Count(m => m.HabitId == habit.Id)
            .Should().Be(1, "jeden wiersz na dzień, nie jeden na dotknięcie");
    }

    [Fact]
    public async Task Nawyk_na_ilosc_dolicza_i_liczy_sie_dopiero_od_progu()
    {
        var habit = await _habits.CreateAsync("Czytanie", target: 20, unit: "stron");

        await _habits.BumpAsync(habit.Id, by: 12);

        var halfway = (await _habits.BoardAsync()).Single();

        halfway.Today.Should().Be(12);
        halfway.DoneToday.Should().BeFalse();
        halfway.Streak.Should().Be(0, "dzień zaczęty to nie dzień zrobiony");
        halfway.Score.Should().Be("12/20 stron");

        await _habits.BumpAsync(habit.Id, by: 8);

        var done = (await _habits.BoardAsync()).Single();

        done.DoneToday.Should().BeTrue();
        done.Streak.Should().Be(1);
    }

    [Fact]
    public async Task Dzisiejszy_jeszcze_nieodhaczony_nie_zrywa_serii()
    {
        // Seria pokazana jako zerwana o poranku byłaby karą za to, że jest rano —
        // a to jest dokładnie ta chwila, w której na serię się patrzy.
        var habit = await _habits.CreateAsync("Ćwiczenia");

        foreach (var back in Enumerable.Range(1, 5))
        {
            Mark(habit.Id, Today.AddDays(-back));
        }

        var card = (await _habits.BoardAsync()).Single();

        card.Today.Should().Be(0);
        card.Streak.Should().Be(5, "wczorajszy jest, więc seria trwa");

        await _habits.BumpAsync(habit.Id);

        (await _habits.BoardAsync()).Single().Streak.Should().Be(6);
    }

    [Fact]
    public async Task Dziura_wczoraj_zrywa_serie_ale_nie_rekord()
    {
        var habit = await _habits.CreateAsync("Ćwiczenia");

        // Dziesięć dni z rzędu dawno temu, przerwa, i dwa dni domknięte przedwczoraj.
        foreach (var back in Enumerable.Range(20, 10))
        {
            Mark(habit.Id, Today.AddDays(-back));
        }

        Mark(habit.Id, Today.AddDays(-3));
        Mark(habit.Id, Today.AddDays(-2));

        var card = (await _habits.BoardAsync()).Single();

        card.Streak.Should().Be(0, "wczorajszego nie ma, więc seria jest zerwana");
        card.Best.Should().Be(10);
        card.Total.Should().Be(12);
    }

    [Fact]
    public async Task Rekord_i_licznik_licza_sie_od_poczatku_a_nie_z_okna_siatki()
    {
        // Sedno: liczby z okna kłamałyby u kogoś, kto ciągnie coś drugi rok.
        var habit = await _habits.CreateAsync("Ćwiczenia");

        var faraway = HabitService.Window + 200;

        foreach (var back in Enumerable.Range(faraway, 30))
        {
            Mark(habit.Id, Today.AddDays(-back));
        }

        var card = (await _habits.BoardAsync()).Single();

        card.Best.Should().Be(30);
        card.Total.Should().Be(30);
        card.Days.Should().NotContain(d => d.Counts, "to było dawno przed oknem siatki");
    }

    [Fact]
    public async Task Siatka_zaczyna_sie_od_poniedzialku_i_konczy_dzisiaj()
    {
        // Bez wyrównania kolumna nie jest tygodniem i „zawsze wypada mi w weekendy"
        // przestaje dać się zobaczyć.
        var habit = await _habits.CreateAsync("Ćwiczenia");

        var days = (await _habits.BoardAsync()).Single().Days;

        days[0].Day.DayOfWeek.Should().Be(DayOfWeek.Monday);
        days[^1].Day.Should().Be(Today);
        days.Count.Should().BeGreaterThanOrEqualTo(HabitService.Window);
    }

    [Fact]
    public async Task Dwa_urzadzenia_odhaczajace_ten_sam_dzien_daja_jeden_wpis()
    {
        // Ta sama odpowiedź, co przy rytmach: tożsamość wpisu liczona z nawyku i dnia,
        // więc scalanie składa dwa zapisy w jeden zamiast liczyć serię podwójnie.
        var habit = await _habits.CreateAsync("Medytacja");

        HabitMark.On(habit.Id, Today, _clock.Now, _hlc.Next()).Id
            .Should().Be(HabitMark.On(habit.Id, Today, _clock.Now, _hlc.Next()).Id);

        HabitMark.On(habit.Id, Today, _clock.Now, _hlc.Next()).Id
            .Should().NotBe(HabitMark.On(habit.Id, Today.AddDays(-1), _clock.Now, _hlc.Next()).Id);
    }

    [Fact]
    public async Task Poprawiony_dzien_wstecz_wchodzi_do_serii()
    {
        // Po to się wchodzi w szczegół: ktoś odhaczył po północy, ktoś zapomniał
        // telefonu. Siatka ma mówić prawdę o tym, jak było, a nie o tym, kiedy
        // zdążył kliknąć.
        var habit = await _habits.CreateAsync("Ćwiczenia");

        Mark(habit.Id, Today.AddDays(-1));
        Mark(habit.Id, Today.AddDays(-3));

        (await _habits.BoardAsync()).Single().Streak.Should().Be(1, "brakuje przedwczoraj");

        await _habits.SetAsync(habit.Id, Today.AddDays(-2), 1);

        var card = (await _habits.BoardAsync()).Single();

        card.Streak.Should().Be(3);
        card.Total.Should().Be(3);
        card.Missed.Should().Be(0, "dzisiejszy jeszcze trwa, więc nie jest przepadnięty");
    }

    [Fact]
    public async Task Zdjety_dzien_wstecz_wypada_z_serii_i_z_licznika()
    {
        var habit = await _habits.CreateAsync("Ćwiczenia");

        foreach (var back in Enumerable.Range(1, 4))
        {
            Mark(habit.Id, Today.AddDays(-back));
        }

        await _habits.SetAsync(habit.Id, Today.AddDays(-2), 0);

        var card = (await _habits.BoardAsync()).Single();

        card.Streak.Should().Be(1, "seria kończy się na dziurze");
        card.Total.Should().Be(3);
    }

    [Fact]
    public async Task Podniesiony_prog_odbiera_dni_ktore_do_niego_nie_siegaja()
    {
        // Próg jest własnością nawyku, nie dnia, więc jego zmiana przelicza całą
        // historię. To jest zamierzone: „dwadzieścia stron" znaczy to samo wstecz
        // i w przód, a inaczej siatka pokazywałaby dwie różne miary naraz.
        var habit = await _habits.CreateAsync("Czytanie", target: 10, unit: "stron");

        Mark(habit.Id, Today.AddDays(-1), amount: 10);
        Mark(habit.Id, Today.AddDays(-2), amount: 12);
        Mark(habit.Id, Today.AddDays(-3), amount: 30);

        (await _habits.BoardAsync()).Single().Total.Should().Be(3);

        await _habits.SetTargetAsync(habit.Id, 20, "stron");

        var card = (await _habits.BoardAsync()).Single();

        card.Total.Should().Be(1, "tylko trzydzieści stron sięga nowego progu");
        card.Streak.Should().Be(0);
        card.Score.Should().Be("0/20 stron");
    }

    [Fact]
    public async Task Prog_i_barwa_dajace_sie_zmienic_wracaja_z_bazy()
    {
        var habit = await _habits.CreateAsync("Czytanie");

        await _habits.RenameAsync(habit.Id, "Czytanie przed snem");
        await _habits.SetTargetAsync(habit.Id, 20, "stron");
        await _habits.SetColorAsync(habit.Id, "#3FA36B");

        var saved = (await _store.ListAsync()).Single();

        saved.Title.Should().Be("Czytanie przed snem");
        saved.Target.Should().Be(20);
        saved.Unit.Should().Be("stron");
        saved.Color.Should().Be("#3FA36B");

        // Zdjęcie progu zdejmuje też jednostkę: „stron" bez liczby nie znaczy nic.
        await _habits.SetTargetAsync(habit.Id, null, "stron");

        var plain = (await _store.ListAsync()).Single();

        plain.Target.Should().BeNull();
        plain.Unit.Should().BeNull();
    }

    [Fact]
    public async Task Skasowany_nawyk_znika_razem_z_liczeniem()
    {
        var habit = await _habits.CreateAsync("Bieganie");
        Mark(habit.Id, Today.AddDays(-1));

        await _habits.DeleteAsync(habit.Id);

        (await _habits.BoardAsync()).Should().BeEmpty();
        (await _store.FindAsync(habit.Id)).Should().BeNull("nagrobek znaczy „tego nie ma”");
    }

    [Fact]
    public async Task Liczby_szczegolu_mowia_o_calej_historii()
    {
        var habit = await _habits.CreateAsync("Ćwiczenia");

        // Start dziesięć dni temu, w środku dwie dziury.
        foreach (var back in new[] { 10, 9, 8, 6, 5, 3, 2, 1 })
        {
            Mark(habit.Id, Today.AddDays(-back));
        }

        var card = (await _habits.BoardAsync()).Single();

        card.Started.Should().Be(Today.AddDays(-10));
        card.Since.Should().Be(11, "dziesięć dni wstecz plus dzisiejszy");
        card.Total.Should().Be(8);
        card.Missed.Should().Be(2, "dwie dziury w środku; dzisiejszy jeszcze trwa");
        card.Best.Should().Be(3);
        card.Rate.Should().Be(73);

        // „Razem" i „pominięte" nie sumują się do „dni od startu" i tak ma być:
        // brakującą jedynką jest dzisiejszy, wciąż otwarty.
        (card.Total + card.Missed).Should().Be(card.Since - 1);
    }

    [Fact]
    public async Task Odlozony_nawyk_schodzi_z_listy_ale_historia_zostaje()
    {
        var habit = await _habits.CreateAsync("Bieganie");
        Mark(habit.Id, Today.AddDays(-1));

        await _habits.ArchiveAsync(habit.Id);

        (await _habits.BoardAsync()).Should().BeEmpty();
        _db.HabitMarks.Count(m => m.HabitId == habit.Id)
            .Should().Be(1, "odłożenie to nie skasowanie");

        await _habits.ReviveAsync(habit.Id);

        (await _habits.BoardAsync()).Single().Total.Should().Be(1);
    }
}

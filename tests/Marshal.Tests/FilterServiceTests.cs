using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.UseCases;
using Marshal.Domain.Filters;
using Marshal.Domain.Tags;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Repositories;
using Marshal.Infrastructure.Sync;
using Marshal.Infrastructure.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Zapisane widoki i uruchamianie filtrów na prawdziwej bazie (spec 11.5).
/// </summary>
public sealed class FilterServiceTests : IDisposable
{
    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 9, 16, 9, 0, 0, TimeSpan.FromHours(2));
    }

    private readonly SqliteConnection _polaczenie = new("Filename=:memory:");
    private readonly MarshalDbContext _db;
    private readonly Clock _zegar = new();
    private readonly HlcSource _hlc;
    private readonly FilterService _usluga;
    private readonly Guid _obszar = Guid.CreateVersion7();

    public FilterServiceTests()
    {
        _polaczenie.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>()
                .UseSqlite(_polaczenie)
                .AddInterceptors(new ChangeJournalInterceptor())
                .Options);
        _db.Database.Migrate();
        _hlc = new HlcSource(_zegar, "biurko");

        _usluga = new FilterService(
            new SavedFilterRepository(_db),
            new TaskRepository(_db),
            new TagRepository(_db),
            new UnitOfWork(_db),
            _zegar,
            _hlc);
    }

    private TaskItem TaskId(string title, Action<TaskItem>? set = null)
    {
        var task = TaskItem.Capture(title, _zegar.Now, _hlc.Next());
        task.MakeNext(_obszar, _hlc.Next());
        set?.Invoke(task);

        _db.Tasks.Add(task);
        _db.SaveChanges();

        return task;
    }

    private Guid Otaguj(TaskItem task, string name)
    {
        var tag = new Tag(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), name, sortOrder: 0);
        _db.Tags.Add(tag);
        _db.TaskTags.Add(new TaskTag(Guid.CreateVersion7(), _zegar.Now, _hlc.Next(), task.Id, tag.Id));
        _db.SaveChanges();

        return tag.Id;
    }

    [Fact]
    public async Task Filtr_oddaje_tylko_pasujace_zadania()
    {
        TaskId("zadzwonić do przychodni", z => z.SetEstimate(10, Energy.Low, _hlc.Next()));
        TaskId("przeczytać umowę", z => z.SetEstimate(90, Energy.High, _hlc.Next()));
        TaskId("nieoszacowane");

        var result = await _usluga.RunAsync(new FilterQuery([FilterCondition.Estimate(15)]));

        result.Should().ContainSingle().Which.Title.Should().Be("zadzwonić do przychodni");
    }

    [Fact]
    public async Task Filtr_po_tagu_siega_do_powiazan()
    {
        var otagowane = TaskId("kupić mleko");
        TaskId("bez tagu");
        var tag = Otaguj(otagowane, "zakupy");

        var result = await _usluga.RunAsync(new FilterQuery([FilterCondition.Tags(tag)]));

        result.Should().ContainSingle().Which.Id.Should().Be(otagowane.Id);
    }

    [Fact]
    public async Task Zdjety_tag_przestaje_pasowac()
    {
        // Powiązanie ma własny nagrobek właśnie po to (zob. TaskTag) — bez tego
        // zdjęty tag wracałby przy każdym scaleniu i filtr kłamałby na obu urządzeniach.
        var task = TaskId("kupić mleko");
        var tag = Otaguj(task, "zakupy");

        var powiazanie = _db.TaskTags.Single();
        powiazanie.MarkDeleted(_hlc.Next());
        await _db.SaveChangesAsync();

        (await _usluga.RunAsync(new FilterQuery([FilterCondition.Tags(tag)]))).Should().BeEmpty();
    }

    [Fact]
    public async Task Pusty_filtr_nie_wysypuje_bazy_na_ekran()
    {
        TaskId("cokolwiek");

        (await _usluga.RunAsync(FilterQuery.Empty)).Should().BeEmpty();
        (await _usluga.RunAsync(null)).Should().BeEmpty();
    }

    [Fact]
    public async Task Zapisany_widok_wraca_z_warunkami()
    {
        var filter = new FilterQuery([
            FilterCondition.States(TaskState.Next),
            FilterCondition.Estimate(15),
        ]);

        var saved = await _usluga.SaveAsync("Kwadrans", filter);

        var ulubione = await _usluga.FavouritesAsync();
        ulubione.Should().ContainSingle();
        ulubione[0].Name.Should().Be("Kwadrans");
        ulubione[0].Id.Should().Be(saved.Id);
        ulubione[0].Query.Should().NotBeNull();
        ulubione[0].Query!.Conditions.Should().HaveCount(2);
    }

    [Fact]
    public async Task Ulubione_trzymaja_kolejnosc_zapisywania()
    {
        await _usluga.SaveAsync("Pierwszy", new FilterQuery([FilterCondition.States(TaskState.Next)]));
        await _usluga.SaveAsync("Drugi", new FilterQuery([FilterCondition.States(TaskState.Waiting)]));
        await _usluga.SaveAsync("Trzeci", new FilterQuery([FilterCondition.States(TaskState.Someday)]));

        (await _usluga.FavouritesAsync()).Select(f => f.Name)
            .Should().ContainInOrder("Pierwszy", "Drugi", "Trzeci");
    }

    [Fact]
    public async Task Widoku_bez_warunkow_nie_da_sie_zapisac()
    {
        var patch = async () => await _usluga.SaveAsync("Pusty", FilterQuery.Empty);
        await patch.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Skasowany_widok_znika_z_ulubionych_ale_zostaje_nagrobek()
    {
        var saved = await _usluga.SaveAsync(
            "Do skasowania", new FilterQuery([FilterCondition.States(TaskState.Next)]));

        await _usluga.DeleteAsync(saved.Id);

        (await _usluga.FavouritesAsync()).Should().BeEmpty();
        (await _usluga.FindAsync(saved.Id)).Should().BeNull();

        // Usunięcia fizycznego nie ma (spec 5.1) — inaczej drugie urządzenie
        // wskrzesiłoby widok przy najbliższym scaleniu.
        _db.SavedFilters.Count().Should().Be(1);
    }

    [Fact]
    public async Task Zmiana_widoku_zapisuje_sie_bez_tworzenia_drugiego()
    {
        var saved = await _usluga.SaveAsync(
            "Kwadrans", new FilterQuery([FilterCondition.Estimate(15)]));

        await _usluga.UpdateAsync(
            saved.Id, "Pół godziny", new FilterQuery([FilterCondition.Estimate(30)]));

        var ulubione = await _usluga.FavouritesAsync();
        ulubione.Should().ContainSingle();
        ulubione[0].Name.Should().Be("Pół godziny");
        ulubione[0].Query!.Conditions[0].MaxMinutes.Should().Be(30);
    }

    [Fact]
    public async Task Zmiana_widoku_trafia_do_dziennika_jako_jedno_pole()
    {
        // Cały filtr w jednej kolumnie znaczy jeden wpis w dzienniku — i to jest
        // powód, dla którego kolumna jest jedna: scalanie per pole nie ma jak złożyć
        // widoku z połówek dwóch różnych decyzji.
        var saved = await _usluga.SaveAsync(
            "Kwadrans", new FilterQuery([FilterCondition.Estimate(15)]));

        await _usluga.UpdateAsync(
            saved.Id, "Kwadrans", new FilterQuery([
                FilterCondition.Estimate(30),
                FilterCondition.States(TaskState.Next),
            ]));

        // Dwa warunki więcej, a w dzienniku jedno pole zmienione — cały filtr jedzie
        // razem albo wcale.
        _db.Changes
            .Count(z => z.EntityId == saved.Id && z.Field == "DefinitionJson")
            .Should().Be(2);
    }

    public void Dispose()
    {
        _db.Dispose();
        _polaczenie.Dispose();
    }
}

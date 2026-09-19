using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.UseCases;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Repositories;
using Marshal.Infrastructure.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Marshal.Tests;

public sealed class TagServiceTests : IDisposable
{
    private sealed class Clock : IClock
    {
        public DateTimeOffset Now => new(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(2));
    }

    private readonly SqliteConnection _connection;
    private readonly MarshalDbContext _db;
    private readonly TagService _tagi;
    private readonly Guid _zadanie = Guid.CreateVersion7();

    public TagServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new MarshalDbContext(
            new DbContextOptionsBuilder<MarshalDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();

        var clock = new Clock();
        _tagi = new TagService(
            new TagRepository(_db), new UnitOfWork(_db), clock, new HlcSource(clock, "testy"));
    }

    [Fact]
    public async Task Przypiecie_tworzy_tag_gdy_jeszcze_nie_istnieje()
    {
        await _tagi.AttachAsync(_zadanie, "dom");

        _db.Tags.Should().HaveCount(1);
        (await _tagi.ForTaskAsync(_zadanie)).Single().Name.Should().Be("dom");
    }

    [Fact]
    public async Task Ten_sam_tag_dwoma_pisowniami_to_jeden_tag()
    {
        await _tagi.AttachAsync(_zadanie, "Dom");
        await _tagi.AttachAsync(Guid.CreateVersion7(), "dom");

        _db.Tags.Should().HaveCount(1);
    }

    [Fact]
    public async Task Krzyzyk_przed_nazwa_jest_obcinany()
    {
        await _tagi.AttachAsync(_zadanie, "#pilne");

        _db.Tags.Single().Name.Should().Be("pilne");
    }

    [Fact]
    public async Task Powtorne_przypiecie_nie_dubluje_powiazania()
    {
        await _tagi.AttachAsync(_zadanie, "dom");
        await _tagi.AttachAsync(_zadanie, "dom");

        _db.TaskTags.Should().HaveCount(1);
    }

    [Fact]
    public async Task Zdjecie_zostawia_nagrobek_zamiast_kasowac_rekord()
    {
        var tag = await _tagi.AttachAsync(_zadanie, "dom");

        await _tagi.DetachAsync(_zadanie, tag);

        _db.TaskTags.Should().HaveCount(1);
        _db.TaskTags.Single().Deleted.Should().BeTrue();
        (await _tagi.ForTaskAsync(_zadanie)).Should().BeEmpty();
    }

    [Fact]
    public async Task Ponowne_przypiecie_przywraca_zdjete_powiazanie_zamiast_tworzyc_drugie()
    {
        // Drugi rekord obok nagrobka dałby po scaleniu dwa powiązania tego samego
        // zadania z tym samym tagiem, a wynik zależałby od kolejności odczytu.
        var tag = await _tagi.AttachAsync(_zadanie, "dom");
        await _tagi.DetachAsync(_zadanie, tag);

        await _tagi.AttachAsync(_zadanie, "dom");

        _db.TaskTags.Should().HaveCount(1);
        _db.TaskTags.Single().Deleted.Should().BeFalse();
    }

    [Fact]
    public async Task Zdjecie_nieistniejacego_powiazania_nic_nie_robi()
    {
        var zdejmij = async () => await _tagi.DetachAsync(_zadanie, Guid.CreateVersion7());

        await zdejmij.Should().NotThrowAsync();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}

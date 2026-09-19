using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Application.Calendar;
using Marshal.Domain.Diagnostics;
using Marshal.Domain.Tasks;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Odbicie w kalendarzu robione po kliknięciu, a nie w jego trakcie.
/// </summary>
/// <remarks>
/// Dopóki wyrównanie odbicia szło wprost, odhaczenie zadania na kalendarzu trwało
/// sekundę albo dwie: zapis lokalny szedł w milisekundach, a ptaszek pojawiał się
/// dopiero po powrocie z Google. Ten test opisuje rzecz, której z ekranu nie widać
/// inaczej niż stoperem — że wołający nie czeka.
/// </remarks>
public sealed class DeferredMirrorTests
{
    private sealed class NewMirror : ITaskMirror
    {
        public TaskCompletionSource Released { get; } = new();

        public TaskCompletionSource Weszlo { get; } = new();

        public Exception? Wywrotka { get; set; }

        public async Task PushAsync(TaskItem task, CancellationToken ct = default)
        {
            Weszlo.TrySetResult();
            await Released.Task;

            if (Wywrotka is not null)
            {
                throw Wywrotka;
            }
        }

        public Task RemoveAsync(TaskItem task, CancellationToken ct = default) =>
            PushAsync(task, ct);
    }

    private sealed class NewJournal : IActivityLog
    {
        public List<(string Co, ActivityLevel Level)> Entries { get; } = [];

        public TaskCompletionSource Saved { get; } = new();

        public int Dropped => 0;

        public Task RecordAsync(
            string operation,
            string outcome,
            ActivityLevel level = ActivityLevel.Ok,
            string? detail = null,
            CancellationToken ct = default)
        {
            lock (Entries)
            {
                Entries.Add((operation, level));
            }

            Saved.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ActivityEntry>> RecentAsync(
            int count = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ActivityEntry>>([]);

        public Task ClearAsync(CancellationToken ct = default)
        {
            lock (Entries)
            {
                Entries.Clear();
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Czekanie na warunek zamiast na stoper. Zwleka najwyżej pięć sekund.</summary>
    private static async Task Doczekaj(Func<bool> condition)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (DateTime.UtcNow < end)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        condition().Should().BeTrue("warunek miał zajść w ciągu pięciu sekund");
    }

    private static TaskItem TaskId() =>
        TaskItem.Capture("Zebranie", DateTimeOffset.UnixEpoch, new Marshal.Domain.Primitives.Hlc(1, 0, "testy"));

    [Fact]
    public async Task Wolajacy_nie_czeka_na_siec()
    {
        var mirror = new NewMirror();
        var deferred = new DeferredMirror(mirror, new NewJournal());

        // Sedno: odbicie stoi i nie ruszy, dopóki go nie puścimy — a mimo to wołanie
        // wraca. Gdyby czekało, ten await nie skończyłby się nigdy.
        await deferred.PushAsync(TaskId());

        await mirror.Weszlo.Task.WaitAsync(TimeSpan.FromSeconds(5));

        mirror.Released.SetResult();
    }

    /// <summary>Odbicie zapisujące kolejność i zwlekające z pierwszą pracą.</summary>
    private sealed class Order : ITaskMirror
    {
        public List<string> Wykonane { get; } = [];

        /// <summary>Wysłanie stoi, dopóki się go nie puści. Kasowanie idzie od razu.</summary>
        public TaskCompletionSource Released { get; } = new();

        public TaskCompletionSource Weszlo { get; } = new();

        public TaskCompletionSource Skonczone { get; } = new();

        public async Task PushAsync(TaskItem task, CancellationToken ct = default)
        {
            Weszlo.TrySetResult();
            await Released.Task;

            lock (Wykonane)
            {
                Wykonane.Add("wysłanie");
            }
        }

        public Task RemoveAsync(TaskItem task, CancellationToken ct = default)
        {
            lock (Wykonane)
            {
                Wykonane.Add("kasowanie");
            }

            Skonczone.TrySetResult();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Odbicia_ida_w_kolejnosci_zgloszen()
    {
        // Zadanie założone i zaraz skasowane. Kasowanie puszczone przed wysłaniem nie
        // zastaje jeszcze żadnego wydarzenia i nie robi nic, a wysłanie zakłada je
        // chwilę później — w kalendarzu zostaje wpis po zadaniu, którego już nie ma.
        // Poprzednia wersja pilnowała „jednego naraz" semaforem, ale o kolejności
        // decydowało wtedy to, która praca pierwsza po niego sięgnęła, czyli pula
        // wątków. Wysłanie stoi tu na uwięzi właśnie po to, żeby dać kasowaniu
        // wszelką sposobność wyprzedzenia go.
        var mirror = new Order();
        var deferred = new DeferredMirror(mirror, new NewJournal());

        await deferred.PushAsync(TaskId());
        await mirror.Weszlo.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await deferred.RemoveAsync(TaskId());

        // Chwila na to, żeby kasowanie zdążyło się wepchnąć, gdyby miało jak.
        await Task.Delay(50);

        lock (mirror.Wykonane)
        {
            mirror.Wykonane.Should().BeEmpty("kasowanie ma czekać na swoją poprzedniczkę");
        }

        mirror.Released.SetResult();
        await mirror.Skonczone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (mirror.Wykonane)
        {
            mirror.Wykonane.Should().Equal("wysłanie", "kasowanie");
        }
    }

    [Fact]
    public async Task Nieudane_odbicie_nie_zrywa_ogona_kolejki()
    {
        // Jedno nieudane wysłanie nie może zabrać ze sobą wszystkiego, co za nim stoi:
        // odbicia doczepiają się jedno do drugiego, więc wyjątek puszczony dalej
        // unieruchomiłby kolejkę do końca działania aplikacji.
        var mirror = new NewMirror { Wywrotka = new InvalidOperationException("sieć padła") };
        var journal = new NewJournal();
        var deferred = new DeferredMirror(mirror, journal);

        await deferred.PushAsync(TaskId());
        await mirror.Weszlo.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await deferred.RemoveAsync(TaskId());
        mirror.Released.SetResult();

        // Dwa wpisy: obie prace się wywróciły, ale obie doszły do skutku. Czekanie
        // na warunek, a nie na stoper — wolna maszyna nie ma prawa psuć wyniku.
        await Doczekaj(() =>
        {
            lock (journal.Entries)
            {
                return journal.Entries.Count == 2;
            }
        });
    }

    [Fact]
    public async Task Nieudane_odbicie_zostawia_slad_w_dzienniku()
    {
        // Odbicie robione po fakcie nie ma komu oddać wyjątku: ekran dawno odpowiedział.
        // Bez wpisu nieudane wysłanie byłoby nieodróżnialne od wysłanego.
        var mirror = new NewMirror { Wywrotka = new InvalidOperationException("sieć padła") };
        var journal = new NewJournal();
        var deferred = new DeferredMirror(mirror, journal);

        await deferred.PushAsync(TaskId());

        await mirror.Weszlo.Task.WaitAsync(TimeSpan.FromSeconds(5));
        mirror.Released.SetResult();

        await journal.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (journal.Entries)
        {
            journal.Entries.Should().ContainSingle()
                .Which.Level.Should().Be(ActivityLevel.Problem);
        }
    }
}

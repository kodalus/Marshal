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
public sealed class OdlozoneOdbicieTests
{
    private sealed class Odbicie : ITaskMirror
    {
        public TaskCompletionSource Puszczone { get; } = new();

        public TaskCompletionSource Weszlo { get; } = new();

        public Exception? Wywrotka { get; set; }

        public async Task PushAsync(TaskItem task, CancellationToken ct = default)
        {
            Weszlo.TrySetResult();
            await Puszczone.Task;

            if (Wywrotka is not null)
            {
                throw Wywrotka;
            }
        }

        public Task RemoveAsync(TaskItem task, CancellationToken ct = default) =>
            PushAsync(task, ct);
    }

    private sealed class Dziennik : IActivityLog
    {
        public List<(string Co, ActivityLevel Poziom)> Wpisy { get; } = [];

        public TaskCompletionSource Zapisano { get; } = new();

        public int Dropped => 0;

        public Task RecordAsync(
            string operation,
            string outcome,
            ActivityLevel level = ActivityLevel.Ok,
            string? detail = null,
            CancellationToken ct = default)
        {
            lock (Wpisy)
            {
                Wpisy.Add((operation, level));
            }

            Zapisano.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ActivityEntry>> RecentAsync(
            int count = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ActivityEntry>>([]);

        public Task ClearAsync(CancellationToken ct = default)
        {
            lock (Wpisy)
            {
                Wpisy.Clear();
            }

            return Task.CompletedTask;
        }
    }

    private static TaskItem Zadanie() =>
        TaskItem.Capture("Zebranie", DateTimeOffset.UnixEpoch, new Marshal.Domain.Primitives.Hlc(1, 0, "testy"));

    [Fact]
    public async Task Wolajacy_nie_czeka_na_siec()
    {
        var odbicie = new Odbicie();
        var odlozone = new OdlozoneOdbicie(odbicie, new Dziennik());

        // Sedno: odbicie stoi i nie ruszy, dopóki go nie puścimy — a mimo to wołanie
        // wraca. Gdyby czekało, ten await nie skończyłby się nigdy.
        await odlozone.PushAsync(Zadanie());

        await odbicie.Weszlo.Task.WaitAsync(TimeSpan.FromSeconds(5));

        odbicie.Puszczone.SetResult();
    }

    [Fact]
    public async Task Nieudane_odbicie_zostawia_slad_w_dzienniku()
    {
        // Odbicie robione po fakcie nie ma komu oddać wyjątku: ekran dawno odpowiedział.
        // Bez wpisu nieudane wysłanie byłoby nieodróżnialne od wysłanego.
        var odbicie = new Odbicie { Wywrotka = new InvalidOperationException("sieć padła") };
        var dziennik = new Dziennik();
        var odlozone = new OdlozoneOdbicie(odbicie, dziennik);

        await odlozone.PushAsync(Zadanie());

        await odbicie.Weszlo.Task.WaitAsync(TimeSpan.FromSeconds(5));
        odbicie.Puszczone.SetResult();

        await dziennik.Zapisano.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (dziennik.Wpisy)
        {
            dziennik.Wpisy.Should().ContainSingle()
                .Which.Poziom.Should().Be(ActivityLevel.Problem);
        }
    }
}

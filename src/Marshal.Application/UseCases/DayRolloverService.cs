using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Tasks;

namespace Marshal.Application.UseCases;

public sealed record RolloverReport(int Moved, int Spawned);

/// <summary>
/// Przejście dnia: zaległe zaplanowane i pominięte wystąpienia serii (spec 8.4, 8.7).
/// </summary>
/// <remarks>
/// <para>
/// Wołane przy starcie aplikacji, przy powrocie z tła i po każdym scaleniu
/// synchronizacji. Nie ma osobnego wyzwalacza o północy i nie będzie: aplikacja nie
/// chodzi w tle, a „przejście dnia" to nie zdarzenie w czasie, tylko zastana różnica
/// między datą zapisaną a dzisiejszą.
/// </para>
/// <para>
/// Wołanie jest **powtarzalne bez skutków ubocznych** — to nie jest wygoda, tylko
/// warunek: dwa urządzenia robią to samo, niezależnie i bez umawiania się, które ma.
/// </para>
/// </remarks>
public sealed class DayRolloverService(
    ITaskRepository tasks,
    IUnitOfWork unitOfWork,
    IClock clock,
    IHlcSource hlc)
{
    /// <summary>
    /// Ile wystąpień wolno dorobić w jednym przebiegu.
    /// </summary>
    /// <remarks>
    /// Dotyczy wyłącznie <see cref="OnMissed.Accumulate"/> — jedynej ścieżki w modelu,
    /// która potrafi wyprodukować stertę. Rok bez otwarcia aplikacji przy powtarzaniu
    /// codziennym dałby trzysta pozycji naraz, czyli dokładnie to, przed czym ma chronić
    /// zasada 1.2. Reszta zaległości dojdzie przy kolejnym uruchomieniu; nic nie ginie,
    /// tylko schodzi partiami.
    /// </remarks>
    private const int MaxSpawnsPerRun = 120;

    public async Task<RolloverReport> RunAsync(CancellationToken ct = default)
    {
        var now = clock.Now;
        var today = clock.Today;

        var queue = new Queue<TaskItem>(await tasks.OverdueByDoDateAsync(today, ct));
        var moved = 0;
        var fresh = 0;

        while (queue.Count > 0)
        {
            var task = queue.Dequeue();
            var before = (task.DoDate, task.RollCount, task.CarriedSince);

            if (RecurrenceRunner.Rollover(task, now, hlc.Next) is { } next)
            {
                tasks.Add(next);
                fresh++;

                // Świeże wystąpienie samo bywa zaległe: przy Accumulate rytm nadrabia
                // po jednym dniu, więc tydzień nieobecności to tydzień pozycji. Bez
                // domknięcia tu zaległość schodziłaby po jednym dniu na uruchomienie.
                if (next.DoDate < today && fresh < MaxSpawnsPerRun)
                {
                    queue.Enqueue(next);
                }
            }

            if (before != (task.DoDate, task.RollCount, task.CarriedSince))
            {
                moved++;
            }
        }

        if (moved > 0 || fresh > 0)
        {
            await unitOfWork.SaveChangesAsync(ct);
        }

        return new RolloverReport(moved, fresh);
    }
}

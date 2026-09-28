using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Tasks;

namespace Marshal.Application.UseCases;

public sealed record RolloverReport(int Moved, int Spawned, int Merged = 0);

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

        // Najpierw bliźniaki, potem zaległości: dwie kopie tej samej serii przeszłyby
        // przejście dnia każda osobno i wyprodukowały dwa następniki zamiast jednego.
        var merged = await MergeTwinsAsync(ct);

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

        return new RolloverReport(moved, fresh, merged);
    }

    /// <summary>
    /// Zlanie w jedno kopii tej samej serii, które powstały na dwóch urządzeniach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Do dziś następnik dostawał identyfikator losowy, więc telefon i pulpit, które ten
    /// sam dzień przekroczyły osobno, tworzyły dwa różne zadania — a scalanie rozpoznaje
    /// rzeczy po identyfikatorze i przyjmowało oba. Seria podwajała się na siatce
    /// i podwajała każdą swoją zapowiedź. Od teraz tożsamość następnika jest wyliczana
    /// (<see cref="OccurrenceId"/>) i nowe bliźniaki nie powstają; ten krok sprząta te,
    /// które zdążyły powstać wcześniej.
    /// </para>
    /// <para>
    /// <b>Bliźniak to nie „podobne zadanie".</b> Muszą zgadzać się wszystkie cztery
    /// rzeczy, które seria przenosi na następnik — nazwa, dzień, miejsce i sama reguła.
    /// Dwa wystąpienia tej samej serii na ten sam dzień nie są niczym, czego ktoś mógłby
    /// chcieć; dwa podobne zadania — jak najbardziej, i dlatego samo podobieństwo nie
    /// wystarcza.
    /// </para>
    /// <para>
    /// <b>Zostaje najmniejszy identyfikator, nie najstarszy wpis.</b> Wybór musi wypaść
    /// tak samo na obu urządzeniach — inaczej każde zostawiłoby inną kopię i po scaleniu
    /// nie zostałaby żadna. Data utworzenia do tego się nie nadaje: pochodzi z dwóch
    /// różnych zegarów.
    /// </para>
    /// <para>
    /// Do kosza, nie z bazy: kopia dostaje nagrobek i zostaje w archiwum. Gdyby ten krok
    /// kiedykolwiek pomylił się co do bliźniaka, pomyłka ma być do obejrzenia i do
    /// cofnięcia, a nie do odtworzenia z kopii zapasowej.
    /// </para>
    /// </remarks>
    private async Task<int> MergeTwinsAsync(CancellationToken ct)
    {
        var twins = (await tasks.RecurringAsync(ct))
            .GroupBy(t => (t.Title, t.DoDate, t.AreaId, t.ProjectId, t.RecurrenceJson))
            .Where(g => g.Count() > 1)
            .ToList();

        if (twins.Count == 0)
        {
            return 0;
        }

        var gone = 0;

        foreach (var group in twins)
        {
            foreach (var extra in group.OrderBy(t => t.Id).Skip(1))
            {
                extra.Trash(hlc.Next());
                gone++;
            }
        }

        await unitOfWork.SaveChangesAsync(ct);

        return gone;
    }
}

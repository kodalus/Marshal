using Marshal.Domain.Primitives;
using Marshal.Domain.Tasks;

namespace Marshal.Domain.Recurrence;

/// <summary>
/// Rozwiązywanie powtórzeń: odhaczenie wystąpienia i przejście dnia (spec 8.4, 8.7).
/// </summary>
/// <remarks>
/// Czysta domena — dostaje zadania i daty, oddaje nowe zadania. Nie zna bazy ani
/// zegara systemowego, więc „co się dzieje 29 lutego" da się sprawdzić bez czekania
/// do 29 lutego.
/// </remarks>
public static class RecurrenceRunner
{
    /// <summary>
    /// Odhaczenie wystąpienia. Zwraca kolejne wystąpienie albo <c>null</c>, gdy seria
    /// się skończyła lub zadanie nie jest powtarzalne.
    /// </summary>
    public static TaskItem? Complete(TaskItem task, DateTimeOffset now, Func<Hlc> stamp)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(stamp);

        var rule = task.Recurrence;
        task.Complete(now, stamp());

        if (rule is null)
        {
            return null;
        }

        // Zaczepienie decyduje, od czego liczyć: od tego, co obiecane, czy od tego,
        // co się faktycznie stało. „Co poniedziałek śmieci" odhaczone we wtorek nie
        // przesuwa kolejnego poniedziałku; „co 3 dni podlewanie" — przesuwa.
        var today = Today(now);

        var basis = rule.Anchor == RecurrenceAnchor.FromCompletion
            ? today
            : task.DoDate ?? today;

        return Spawn(task, rule, basis, now, stamp);
    }

    /// <summary>
    /// Pominięcie bieżącego wystąpienia: to jedno przepada, a rytm idzie dalej.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bez tego jedyną drogą na „tej środy nie będzie" było wyrzucenie zadania — a razem
    /// z nim przepadał cały rytm, bo regułę niesie właśnie to wystąpienie. Jedna czynność
    /// kasowała więc dwie różne rzeczy, z czego drugiej nikt nie chciał.
    /// </para>
    /// <para>
    /// Wyrzucone, a nie odhaczone: pominięte wystąpienie nie zostało zrobione i nie ma
    /// prawa wchodzić do liczb o tym, co zrobione. Rytm liczony jest od dnia, na który
    /// było umówione — pominięcie nie jest wykonaniem, więc nie przesuwa serii także
    /// wtedy, gdy rytm chodzi od wykonania.
    /// </para>
    /// </remarks>
    public static TaskItem? Skip(TaskItem task, DateTimeOffset now, Func<Hlc> stamp)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(stamp);

        var rule = task.Recurrence;
        var basis = task.DoDate ?? Today(now);

        task.Trash(stamp());

        return rule is null ? null : Spawn(task, rule, basis, now, stamp);
    }

    /// <summary>
    /// Przejście dnia dla jednego niewykonanego zadania z <see cref="TaskItem.DoDate"/>
    /// w przeszłości. Zwraca kolejne wystąpienie, jeśli powstało.
    /// </summary>
    /// <remarks>
    /// Wołanie jest **powtarzalne bez skutków ubocznych**: po przetworzeniu albo data
    /// zadania stoi na dziś, albo reguła zeszła na następnik, więc drugie uruchomienie
    /// tego samego dnia nie ma już czego przetwarzać. To jest warunek, żeby dało się
    /// puszczać przy każdym starcie aplikacji, na dwóch urządzeniach, bez umawiania się,
    /// które ma to zrobić.
    /// </remarks>
    public static TaskItem? Rollover(TaskItem task, DateTimeOffset now, Func<Hlc> stamp)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(stamp);

        var today = Today(now);

        if (task.DoDate is not { } doDate || doDate >= today || !IsOpen(task.State))
        {
            return null;
        }

        var rule = task.Recurrence;

        if (rule is null)
        {
            // Zwykłe zaplanowane zadanie: przesunięcie z licznikiem (spec 8.7).
            // Termin (Deadline) celowo nie jest ruszany — to fakt zewnętrzny, świat się
            // nie przesunął, więc minięcie zostaje przeterminowaniem (N5).
            if (task.State == TaskState.Scheduled)
            {
                task.RollTo(today, stamp());
            }

            return null;
        }

        switch (rule.OnMissed)
        {
            case OnMissed.Skip:
                // Jednym krokiem na dziś albo dalej. Tydzień bez otwierania aplikacji ma
                // dać jedno wystąpienie, a nie siedem utworzonych i wyrzuconych po kolei.
                task.Trash(stamp());
                return SpawnFrom(task, rule, doDate, today, now, stamp);

            case OnMissed.Accumulate:
                // Wystąpienie przestaje być częścią serii i zostaje zwykłą zaległością;
                // rytm idzie dalej na następniku. Trzy nieodhaczone treningi to trzy
                // pozycje do zrobienia plus jedna umówiona na dziś.
                var next = Spawn(task, rule, doDate, now, stamp);
                task.LeaveAsDebt(stamp());
                return next;

            default:
                task.CarryTo(today, stamp());
                return null;
        }
    }

    /// <summary>
    /// Tworzy kolejne wystąpienie i **zabiera regułę poprzedniemu**.
    /// </summary>
    /// <remarks>
    /// Regułę nosi zawsze najnowsze wystąpienie. Bez tego przejście dnia puszczone
    /// dwa razy zrobiłoby dwie kopie, a <see cref="OnMissed.Accumulate"/> po jednej
    /// na każde uruchomienie.
    /// </remarks>
    private static TaskItem? Spawn(
        TaskItem task, RecurrenceRule rule, DateOnly basis, DateTimeOffset now, Func<Hlc> stamp)
    {
        task.SetRecurrence(null, stamp());

        if (RecurrenceSchedule.NextSlot(rule, basis) is not { } slot)
        {
            return null;
        }

        return task.SpawnNextOccurrence(slot.Date, slot.Rule, now, stamp(), slot.Time);
    }

    private static TaskItem? SpawnFrom(
        TaskItem task,
        RecurrenceRule rule,
        DateOnly basis,
        DateOnly floor,
        DateTimeOffset now,
        Func<Hlc> stamp)
    {
        task.SetRecurrence(null, stamp());

        if (RecurrenceSchedule.NextFrom(rule, basis, floor) is not { } slot)
        {
            return null;
        }

        return task.SpawnNextOccurrence(slot.Date, slot.Rule, now, stamp(), slot.Time);
    }

    /// <summary>
    /// Dzień lokalny chwili. Jedno miejsce, w którym chwila zamienia się w datę —
    /// żeby „dziś" znaczyło to samo w odhaczaniu i w przejściu dnia.
    /// </summary>
    private static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(now.DateTime);

    private static bool IsOpen(TaskState state) =>
        state is TaskState.Next or TaskState.Scheduled or TaskState.Waiting;
}

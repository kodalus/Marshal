using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Tasks;

namespace Marshal.Application.Calendar;

/// <summary>
/// Zadanie udostępnione jako wydarzenie w podłączonym kalendarzu (spec 10.2).
/// </summary>
/// <remarks>
/// <para>
/// Po to, żeby ktoś, kto nie ma Marshala, widział u siebie to, co go dotyczy.
/// Udostępnia się pojedyncze zadania, nie całe obszary — obszar rodzinny mieści
/// i „odebrać dziecko", i „kupić prezent", a widzieć je mają różne osoby.
/// </para>
/// <para>
/// <b>Wysyłamy tylko to, co udostępnione.</b> Zadanie bez powiązania nie dotyka
/// kalendarza w żaden sposób, także wtedy, gdy zmienia się co sekundę. To jedyne
/// miejsce w aplikacji, w którym błąd psuje dane poza nią.
/// </para>
/// <para>
/// <b>Nie udaje, że się udało.</b> Gdy zapis do kalendarza padnie, powiązanie zostaje
/// takie, jakie było, a wyjątek idzie w górę. Ciche odpięcie zostawiłoby w cudzym
/// kalendarzu wydarzenie, o którym nikt już nie pamięta.
/// </para>
/// </remarks>
public sealed class TaskMirror(
    CalendarSyncService calendar,
    ITaskRepository tasks,
    IUnitOfWork unitOfWork,
    IHlcSource hlc,
    ISettings settings) : ITaskMirror
{
    /// <summary>Ile trwa udostępnione zadanie bez podanej długości.</summary>
    private const int DomyslneMinuty = 30;

    public async Task PushAsync(TaskItem task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.SharedCalendarId is not { } kalendarz)
        {
            return;
        }

        // Zadanie, które przestało mieć dzień albo godzinę, przestaje być wydarzeniem.
        // Kalendarz nie ma jak pokazać „kiedyś w tym tygodniu", a zostawione w nim
        // wydarzenie kłamałoby o porze, której nikt nie wybrał.
        if (task.State == TaskState.Trashed || task.DoDate is null || task.DoTime is null)
        {
            await RemoveAsync(task, ct);
            return;
        }

        var szkic = Szkic(task);

        var identyfikator = await calendar.SaveEventAsync(
            kalendarz, task.SharedEventId, szkic, ct);

        if (identyfikator != task.SharedEventId)
        {
            task.Share(kalendarz, identyfikator, hlc.Next());
            await unitOfWork.SaveChangesAsync(ct);
        }
    }

    public async Task RemoveAsync(TaskItem task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.SharedCalendarId is not { } kalendarz || task.SharedEventId is not { } wydarzenie)
        {
            return;
        }

        // Najpierw u źródła, potem u nas — jak przy każdym zapisie na zewnątrz.
        // Odwrotna kolejność zostawiałaby przy nieudanym kasowaniu wydarzenie,
        // do którego nie mamy już żadnego wskazania.
        await calendar.DeleteEventAsync(kalendarz, wydarzenie, ct);

        task.Unshare(hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);
    }

    /// <summary>Udostępnienie zadania po raz pierwszy. Oddaje powód odmowy albo nic.</summary>
    public async Task<string?> ShareAsync(Guid taskId, Guid calendarId, CancellationToken ct = default)
    {
        if (await tasks.FindAsync(taskId, ct) is not { } zadanie)
        {
            return null;
        }

        if ((await calendar.SourcesAsync(ct)).FirstOrDefault(z => z.Id == calendarId)
            is not { } zrodlo)
        {
            return "Tego kalendarza nie ma już na liście podłączonych.";
        }

        if (!calendar.CanWrite(zrodlo.Kind))
        {
            return $"Do kalendarza „{zrodlo.Name}” umiemy tylko czytać.";
        }

        if (zadanie.DoDate is null || zadanie.DoTime is null)
        {
            return "Najpierw dzień i godzina — kalendarz nie ma jak pokazać zadania bez pory.";
        }

        var identyfikator = await calendar.SaveEventAsync(calendarId, null, Szkic(zadanie), ct);

        zadanie.Share(calendarId, identyfikator, hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);

        return null;
    }

    /// <summary>Zdjęcie udostępnienia z jednego zadania.</summary>
    public async Task UnshareAsync(Guid taskId, CancellationToken ct = default)
    {
        if (await tasks.FindAsync(taskId, ct) is { } zadanie)
        {
            await RemoveAsync(zadanie, ct);
        }
    }

    /// <summary>
    /// Zadanie w postaci wydarzenia.
    /// </summary>
    /// <remarks>
    /// Odhaczone niesie ptaszek w nazwie — tak samo jak wydarzenie odhaczone na siatce.
    /// Druga osoba widzi wtedy u siebie, że rzecz jest zrobiona, bez pytania.
    /// </remarks>
    private CalendarDraft Szkic(TaskItem task)
    {
        var strefa = settings.Zone;
        var dzien = task.DoDate!.Value;
        var pora = task.DoTime!.Value;

        var lokalny = dzien.ToDateTime(pora);
        var start = new DateTimeOffset(lokalny, strefa.GetUtcOffset(lokalny));
        var dlugosc = TimeSpan.FromMinutes(task.EstimatedMinutes ?? DomyslneMinuty);

        var nazwa = task.State == TaskState.Done
            ? EventMark.Apply(task.Title)
            : EventMark.Strip(task.Title);

        return new CalendarDraft(nazwa, start, start + dlugosc);
    }
}

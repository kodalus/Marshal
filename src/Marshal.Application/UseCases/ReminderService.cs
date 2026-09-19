using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Tasks;

namespace Marshal.Application.UseCases;

/// <summary>
/// Odzywanie się o czasie (spec 13).
/// </summary>
/// <remarks>
/// <para>
/// Zadanie z godziną może mieć kilka przypomnień, liczonych **od niej**: o czasie,
/// kwadrans wcześniej, dobę wcześniej. Tak się o tym myśli — „przypomnij mi pół
/// godziny przed wizytą", a nie „przypomnij o 14:30" — a przy przesunięciu zadania
/// wyprzedzenia jadą razem z nim i nie trzeba ich poprawiać po jednym.
/// </para>
/// <para>
/// Zadanie bez godziny ma osobną, bezwzględną chwilę: nie ma od czego liczyć
/// wyprzedzenia, a „kiedyś w tym tygodniu, kwadrans wcześniej" nie znaczy nic.
/// </para>
/// </remarks>
public sealed class ReminderService(
    ITaskRepository tasks,
    IReminderLog log,
    INotifier notifier,
    IUnitOfWork unitOfWork,
    ISettings settings,
    IClock clock)
{
    /// <summary>
    /// Jak dawne przypomnienie z wyprzedzenia jeszcze warto pokazać.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dotyczy **wyłącznie** przypomnień liczonych od godziny zadania. Tych bywa kilka
    /// na zadanie, więc aplikacja zamknięta przez tydzień uzbierałaby ich kilkadziesiąt
    /// naraz — a wtedy żadne nie jest przypomnieniem, tylko lawiną do zamknięcia.
    /// </para>
    /// <para>
    /// Przypomnienie z własną chwilą nie ma odcięcia i mieć nie może: jest jedno,
    /// ustawione ręcznie, i to, że aplikacja była zamknięta, nie jest powodem, żeby
    /// o nim nie powiedzieć. Odzywanie się wyłącznie co do minuty znaczyłoby, że
    /// przypomnienia nie działają w ogóle.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan Overdue = TimeSpan.FromDays(1);

    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var now = clock.Now;
        var zone = settings.Zone;
        var shown = 0;

        // Wszystkie chwile zebrane i uporządkowane **razem**, nie zadanie po zadaniu.
        // Gdy uzbierało się kilka zaległych, kolejność ma być taka, w jakiej miały się
        // odezwać — a nie taka, w jakiej zadania wyszły z bazy.
        var toShow = (await tasks.WithRemindersAsync(ct))
            .SelectMany(z => Moments(z, zone).Select(c => (TaskId: z, c.Moment, c.WithLead)))
            .Where(w => w.Moment <= now)
            .Where(w => !w.WithLead || now - w.Moment <= Overdue)
            .OrderBy(w => w.Moment)
            .ToList();

        foreach (var (task, moment, _) in toShow)
        {
            if (await log.WasShownAsync(task.Id, moment, ct))
            {
                continue;
            }

            await notifier.ShowAsync(
                new Notification(task.Id, task.Title, Caption(task, moment, zone)), ct);

            log.Record(task.Id, moment, now);
            shown++;
        }

        if (shown > 0)
        {
            await unitOfWork.SaveChangesAsync(ct);
        }

        return shown;
    }

    /// <summary>
    /// Najbliższa chwila, w której cokolwiek ma się odezwać. Puste, gdy nic nie czeka.
    /// </summary>
    /// <remarks>
    /// Do budzika systemowego na Androidzie: przy zamkniętej aplikacji nie ma minutnika,
    /// który sprawdzałby przypomnienia co minutę, więc system musi dostać jedną
    /// konkretną godzinę. Po każdym odezwaniu liczy się ją od nowa — budzik jest
    /// zawsze na **najbliższą** rzecz, a nie na wszystkie naraz.
    /// </remarks>
    public async Task<DateTimeOffset?> NextUpAsync(CancellationToken ct = default)
    {
        var now = clock.Now;
        var zone = settings.Zone;

        var moments = (await tasks.WithRemindersAsync(ct))
            .SelectMany(z => Moments(z, zone))
            .Select(c => c.Moment)
            .Where(c => c > now)
            .OrderBy(c => c)
            .ToList();

        return moments.Count > 0 ? moments[0] : null;
    }

    /// <summary>Wszystkie chwile, w których to zadanie ma się odezwać. Najdawniejsze pierwsze.</summary>
    private static IEnumerable<(DateTimeOffset Moment, bool WithLead)> Moments(
        TaskItem task, TimeZoneInfo zone)
    {
        var moments = new List<(DateTimeOffset, bool)>();

        if (task.ReminderAt is { } absolute)
        {
            moments.Add((absolute, false));
        }

        if (task is { DoDate: { } day, DoTime: { } time } && task.ReminderLeads.Count > 0)
        {
            // Strefa liczona dla **tej** chwili, nie bieżąca: przypomnienie o zadaniu
            // za trzy tygodnie ma wypaść o właściwej godzinie także wtedy, gdy po drodze
            // zmienia się czas.
            var local = day.ToDateTime(time);
            var start = new DateTimeOffset(local, zone.GetUtcOffset(local));

            moments.AddRange(
                task.ReminderLeads.Select(m => (start - TimeSpan.FromMinutes(m), true)));
        }

        return moments.DistinctBy(c => c.Item1).OrderBy(c => c.Item1);
    }

    /// <summary>Treść pod tytułem: o czym to przypomnienie mówi.</summary>
    private static string Caption(TaskItem task, DateTimeOffset moment, TimeZoneInfo zone)
    {
        if (task is not { DoDate: { } day, DoTime: { } time })
        {
            return task.Note ?? string.Empty;
        }

        var local = day.ToDateTime(time);
        var start = new DateTimeOffset(local, zone.GetUtcOffset(local));
        var before = start - moment;

        return before <= TimeSpan.Zero
            ? $"Teraz — {time:HH}:{time:mm}"
            : $"Za {Count(before)} — {time:HH}:{time:mm}";
    }

    private static string Count(TimeSpan before) => before.TotalMinutes switch
    {
        < 60 => $"{(int)before.TotalMinutes} min",
        < 60 * 24 => $"{(int)before.TotalHours} godz.",
        _ => $"{(int)before.TotalDays} dni",
    };
}

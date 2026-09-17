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
    private static readonly TimeSpan Przeterminowanie = TimeSpan.FromDays(1);

    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var teraz = clock.Now;
        var strefa = settings.Zone;
        var pokazane = 0;

        foreach (var zadanie in await tasks.WithRemindersAsync(ct))
        {
            foreach (var (chwila, zWyprzedzenia) in Chwile(zadanie, strefa))
            {
                if (chwila > teraz
                    || (zWyprzedzenia && teraz - chwila > Przeterminowanie)
                    || await log.WasShownAsync(zadanie.Id, chwila, ct))
                {
                    continue;
                }

                await notifier.ShowAsync(
                    new Notification(zadanie.Id, zadanie.Title, Podpis(zadanie, chwila, strefa)), ct);

                log.Record(zadanie.Id, chwila, teraz);
                pokazane++;
            }
        }

        if (pokazane > 0)
        {
            await unitOfWork.SaveChangesAsync(ct);
        }

        return pokazane;
    }

    /// <summary>Wszystkie chwile, w których to zadanie ma się odezwać. Najdawniejsze pierwsze.</summary>
    private static IEnumerable<(DateTimeOffset Chwila, bool ZWyprzedzenia)> Chwile(
        TaskItem zadanie, TimeZoneInfo strefa)
    {
        var chwile = new List<(DateTimeOffset, bool)>();

        if (zadanie.ReminderAt is { } bezwzgledna)
        {
            chwile.Add((bezwzgledna, false));
        }

        if (zadanie is { DoDate: { } dzien, DoTime: { } pora } && zadanie.ReminderLeads.Count > 0)
        {
            // Strefa liczona dla **tej** chwili, nie bieżąca: przypomnienie o zadaniu
            // za trzy tygodnie ma wypaść o właściwej godzinie także wtedy, gdy po drodze
            // zmienia się czas.
            var lokalna = dzien.ToDateTime(pora);
            var start = new DateTimeOffset(lokalna, strefa.GetUtcOffset(lokalna));

            chwile.AddRange(
                zadanie.ReminderLeads.Select(m => (start - TimeSpan.FromMinutes(m), true)));
        }

        return chwile.DistinctBy(c => c.Item1).OrderBy(c => c.Item1);
    }

    /// <summary>Treść pod tytułem: o czym to przypomnienie mówi.</summary>
    private static string Podpis(TaskItem zadanie, DateTimeOffset chwila, TimeZoneInfo strefa)
    {
        if (zadanie is not { DoDate: { } dzien, DoTime: { } pora })
        {
            return zadanie.Note ?? string.Empty;
        }

        var lokalna = dzien.ToDateTime(pora);
        var start = new DateTimeOffset(lokalna, strefa.GetUtcOffset(lokalna));
        var przed = start - chwila;

        return przed <= TimeSpan.Zero
            ? $"Teraz — {pora:HH}:{pora:mm}"
            : $"Za {Ile(przed)} — {pora:HH}:{pora:mm}";
    }

    private static string Ile(TimeSpan przed) => przed.TotalMinutes switch
    {
        < 60 => $"{(int)przed.TotalMinutes} min",
        < 60 * 24 => $"{(int)przed.TotalHours} godz.",
        _ => $"{(int)przed.TotalDays} dni",
    };
}

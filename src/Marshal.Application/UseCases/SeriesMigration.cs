using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Diagnostics;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Series;
using Marshal.Domain.Tasks;

namespace Marshal.Application.UseCases;

public sealed record MigrationReport(int Converted, int Collapsed, int Spawned);

/// <summary>
/// Przeniesienie rytmów ze starego modelu do serii. Raz, przy pierwszym uruchomieniu.
/// </summary>
/// <remarks>
/// <para>
/// W starym modelu regułę nosiło najnowsze żywe wystąpienie, a serii jako rzeczy nie
/// było. Tutaj każde takie wystąpienie zakłada serię: reguła i szablon schodzą do niej,
/// a samo wystąpienie zostaje jej pierwszym — nietknięte, ze znacznikiem „zmienione
/// z ręki", bo razem z nim mają zostać załączniki, podzadania i wszystko, co ktoś do
/// niego dopisał.
/// </para>
/// <para>
/// <b>Zlanie bliźniaków jest tu dokładne</b>, a nie heurystyczne. Stare kopie
/// rozpoznawało się po nazwie, miejscu i kształcie rytmu — i to było zgadywanie, które
/// raz sklejało za mało, raz za dużo. Tu wystarczy pytanie o tożsamość: po przeniesieniu
/// dni należą do serii i liczą się z niej, więc dwa wiersze na ten sam dzień tej samej
/// serii są jedną rzeczą zapisaną dwa razy. Zostaje wiersz o mniejszym identyfikatorze,
/// żeby wybór wypadł tak samo na obu urządzeniach: data utworzenia do tego się nie
/// nadaje, bo pochodzi z dwóch różnych zegarów.
/// </para>
/// <para>
/// <b>Powtarzalne bez skutków ubocznych.</b> Przeniesione wystąpienie nie ma już reguły,
/// więc drugie uruchomienie nie ma czego przenosić — ten sam warunek, co przy przejściu
/// dnia, i z tego samego powodu: dwa urządzenia robią to samo, nie umawiając się.
/// </para>
/// </remarks>
public sealed class SeriesMigration(
    ITaskRepository tasks,
    ITaskSeriesRepository series,
    IUnitOfWork unitOfWork,
    IClock clock,
    IHlcSource hlc,
    SeriesService rhythms,
    IActivityLog? journal = null)
{
    public async Task<MigrationReport> RunAsync(CancellationToken ct = default)
    {
        var carriers = (await tasks.RecurringAsync(ct))
            .Where(t => t.Recurrence is not null && t.SeriesId is null)
            .ToList();

        if (carriers.Count == 0)
        {
            return new MigrationReport(0, 0, 0);
        }

        // Bliźniaki sklejane **przed** zakładaniem serii. Dwie kopie tej samej serii
        // założyłyby dwie serie, a wtedy nie byłyby już kopiami niczego — każda miałaby
        // własną tożsamość i własne, poprawnie wyliczone okno. Rozjazd zostałby na stałe.
        var collapsed = Collapse(carriers);
        var converted = 0;

        foreach (var carrier in carriers)
        {
            if (carrier.State == TaskState.Trashed || carrier.Recurrence is not { } rule)
            {
                continue;
            }

            var fresh = TaskSeries.Create(
                carrier.DoDate ?? clock.Today,
                Calendared(rule),
                SeriesTemplate.From(carrier),
                clock.Now,
                hlc.Next());

            series.Add(fresh);

            carrier.JoinSeries(fresh.Id, hlc.Next());
            carrier.Override(hlc.Next());
            carrier.SetRecurrence(null, hlc.Next());
            converted++;
        }

        await unitOfWork.SaveChangesAsync(ct);

        var filled = await rhythms.TopUpAsync(ct);

        if (journal is not null)
        {
            await journal.RecordAsync(
                "Rytmy: przeniesienie do serii",
                $"{converted} serii, {collapsed} kopii do kosza, {filled.Added} wystąpień",
                ActivityLevel.Ok,
                null,
                ct);
        }

        return new MigrationReport(converted, collapsed, filled.Added);
    }

    /// <summary>
    /// Rytm codzienny przestawiony na zaczepienie kalendarzowe.
    /// </summary>
    /// <remarks>
    /// Przy odstępie jednego dnia obie odpowiedzi znaczą to samo — następny dzień jest
    /// następnym dniem — a różnią się tym, że rytm zaczepiony na wykonaniu nie ma dat do
    /// wyliczenia i dostaje okno na jedno wystąpienie. Przeniesione bez tej poprawki
    /// „codziennie" straciłoby widok miesiąca, przy którym ma sens największy.
    /// </remarks>
    private static RecurrenceRule Calendared(RecurrenceRule rule) =>
        rule is { Kind: RecurrenceKind.Daily, Anchor: RecurrenceAnchor.FromCompletion }
            ? new RecurrenceRule(
                rule.Kind, rule.Interval, rule.DaysOfWeek, rule.DayOfMonth,
                RecurrenceAnchor.FromScheduled, rule.OnMissed, rule.Until, rule.Count,
                rule.Changes, rule.Minutes, rule.Time, rule.Leads)
            : rule;

    /// <summary>
    /// Kopie tej samej serii z dwóch urządzeń — do kosza, zostaje jedna.
    /// </summary>
    /// <remarks>
    /// Ostatni raz po nazwie, miejscu i kształcie rytmu, bo przed przeniesieniem nie ma
    /// jeszcze czym pytać o tożsamość. Od teraz to pytanie nie będzie już potrzebne:
    /// wystąpienia liczą tożsamość z serii i dnia, więc dwa urządzenia dochodzą do tego
    /// samego wiersza, a nie do dwóch podobnych.
    /// </remarks>
    private int Collapse(List<TaskItem> carriers)
    {
        var gone = 0;

        foreach (var group in carriers
            .GroupBy(t => (t.Title, t.AreaId, t.ProjectId, t.Recurrence!.Shape))
            .Where(g => g.Count() > 1))
        {
            foreach (var extra in group
                .OrderBy(t => t.DoDate ?? DateOnly.MaxValue)
                .ThenBy(t => t.Id)
                .Skip(1))
            {
                extra.Trash(hlc.Next());
                extra.SetRecurrence(null, hlc.Next());
                gone++;
            }
        }

        return gone;
    }
}

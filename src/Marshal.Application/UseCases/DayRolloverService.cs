using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Diagnostics;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Tasks;

namespace Marshal.Application.UseCases;

public sealed record RolloverReport(int Moved, int Spawned, int Dropped = 0);

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
    ITaskSeriesRepository series,
    SeriesService rhythms,
    IUnitOfWork unitOfWork,
    IClock clock,
    IHlcSource hlc,
    IActivityLog? journal = null)
{
    /// <summary>
    /// Co zrobić z zaległym wystąpieniem serii.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Dzień wystąpienia jest nietykalny.</b> W modelu z zapisanym oknem dzień jest
    /// częścią tożsamości wiersza: identyfikator liczy się z serii i z niego. Przeniesienie
    /// zaległego na dziś postawiłoby wiersz na dniu, który seria sama ma obsadzić — i to
    /// jest dokładnie ta droga, którą wracały „po dwa wystąpienia na dzień", tylko wejściem
    /// od innej strony. Zaległe zostaje więc tam, gdzie było, i jest zaległe: po to są
    /// listy zaległości i dlatego widać je na czerwono.
    /// </para>
    /// <para>
    /// <b>Co znaczy „przepadło", zależy od rytmu</b> — i tu trzy odpowiedzi, które
    /// w starym modelu różniły się sposobem produkowania następnika, różnią się wreszcie
    /// czymś, co widać:
    /// </para>
    /// <list type="bullet">
    /// <item><c>Skip</c> — przepadło i nie wraca; nagrobek od razu.</item>
    /// <item><c>Carry</c> — przypomina o sobie, dopóki rytm nie wypadł znowu. „Co
    /// poniedziałek śmieci" ma pytać przez tydzień i przestać, kiedy przychodzi następny
    /// poniedziałek: dwa wystąpienia tej samej rzeczy nie są dwiema rzeczami.</item>
    /// <item><c>Accumulate</c> — zostaje na zawsze jako zaległość. Trzy opuszczone
    /// treningi to trzy treningi, których nie było, i tak ma być zapisane.</item>
    /// </list>
    /// </remarks>
    private int Judge(TaskItem task, List<TaskItem> siblings, DateOnly today)
    {
        var rule = OnMissed.Carry;

        if (task.SeriesId is { } id && _rules.TryGetValue(id, out var found))
        {
            rule = found;
        }

        if (rule == OnMissed.Accumulate)
        {
            return 0;
        }

        if (rule == OnMissed.Carry && !Superseded(task, siblings, today))
        {
            return 0;
        }

        task.Trash(hlc.Next());
        return 1;
    }

    /// <summary>Czy ten rytm wypadł od tamtego dnia jeszcze raz — i ten dzień już minął.</summary>
    private static bool Superseded(TaskItem task, List<TaskItem> siblings, DateOnly today) =>
        siblings.Any(z => z.Id != task.Id
                       && z.DoDate > task.DoDate
                       && z.DoDate <= today);

    private Dictionary<Guid, OnMissed> _rules = [];

    public async Task<RolloverReport> RunAsync(CancellationToken ct = default)
    {
        var now = clock.Now;
        var today = clock.Today;

        // Odpowiedź na minięcie leży w serii, a wystąpienie zna tylko swoją
        // przynależność. Wczytane raz na przebieg, bo serii jest kilkanaście,
        // a zaległych wystąpień bywa kilkaset.
        _rules = (await series.ListAsync(ct)).ToDictionary(z => z.Id, z => z.Rule.OnMissed);

        var overdue = await tasks.OverdueByDoDateAsync(today, ct);
        var moved = 0;
        var gone = 0;

        // Zaległe wystąpienia serii **po seriach**, bo odpowiedź na „to już przepadło"
        // zależy u nich od tego, czy ten rytm wypadł od tamtej pory jeszcze raz.
        var standing = overdue
            .Where(t => t.SeriesId is { } id && id != Guid.Empty)
            .GroupBy(t => t.SeriesId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var task in overdue)
        {
            if (task.SeriesId is { } id)
            {
                gone += Judge(task, standing[id], today);
                continue;
            }

            var before = (task.DoDate, task.RollCount, task.CarriedSince);

            RecurrenceRunner.Rollover(task, now, hlc.Next);

            if (before != (task.DoDate, task.RollCount, task.CarriedSince))
            {
                moved++;
            }
        }

        if (moved > 0 || gone > 0)
        {
            await unitOfWork.SaveChangesAsync(ct);
        }

        var filled = await rhythms.TopUpAsync(ct);

        // Do dziennika, bo to jedyna czynność w przejściu dnia, która **zabiera** zadania
        // — i jedyna, o której trzeba móc powiedzieć „zadziałała" albo „nie zadziałała"
        // bez zaglądania do bazy. Brak takiego wpisu kosztował już jedną rundę przy
        // sklejaniu kopii serii: nie dało się odróżnić „nie było czego zabrać" od
        // „w ogóle nie doszło".
        if (gone > 0 && journal is not null)
        {
            await journal.RecordAsync(
                "Przejście dnia: zamknięcie zaległych wystąpień",
                $"{gone} do kosza",
                ActivityLevel.Ok,
                null,
                ct);
        }

        return new RolloverReport(moved, filled.Added, gone);
    }

}

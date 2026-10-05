using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Diagnostics;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Series;
using Marshal.Domain.Tasks;

namespace Marshal.Application.UseCases;

/// <summary>Co zrobiło dopełnianie okna.</summary>
public sealed record TopUpReport(int Added, int Retired = 0);

/// <summary>
/// Serie powtarzalne: zakładanie, dopełnianie okna i kończenie.
/// </summary>
/// <remarks>
/// <para>
/// <b>Okno zapisane, nie rysowane.</b> Do tej pory seria miała naraz jedno żywe
/// wystąpienie, a to, co dalej, było rysowane z reguły przy każdym przerysowaniu
/// kalendarza. Zapowiedź nie była zadaniem, więc nie miała przypomnienia, nie dawała
/// się nikomu pokazać ani opisać notatką — a tożsamość następnika, liczona z poprzednika
/// i z dnia, zależała od tego, kiedy które urządzenie zostało otwarte. Stąd serie
/// rozchodzące się na dwa łańcuchy i wracające po dwa razy na ten sam dzień.
/// </para>
/// <para>
/// Tu wystąpienia są zwykłymi zadaniami, postawionymi z góry, z tożsamością liczoną
/// z serii i dnia. Dopełnianie wolno puszczać ile razy się chce i na ilu urządzeniach
/// się chce: dni już zajęte są pomijane, a dwa urządzenia liczą te same identyfikatory,
/// więc scalanie składa ich pracę w jedno zamiast rozstawiać ją obok siebie.
/// </para>
/// </remarks>
public sealed class SeriesService(
    ITaskSeriesRepository series,
    ITaskRepository tasks,
    IUnitOfWork unitOfWork,
    IClock clock,
    IHlcSource hlc,
    IActivityLog? journal = null)
{
    /// <summary>
    /// Nadanie rytmu zadaniu, które już istnieje.
    /// </summary>
    /// <remarks>
    /// Zadanie zostaje tym, czym jest, i wchodzi do serii jako jej pierwsze wystąpienie —
    /// z własnym, losowym identyfikatorem i ze znacznikiem „zmienione z ręki". Skasowanie
    /// go i postawienie w jego miejsce wiersza z wyliczoną tożsamością byłoby czystsze
    /// w tabeli i gorsze w życiu: razem z zadaniem przepadłyby załączniki, podzadania
    /// i wszystko, co ktoś do niego dopisał, zanim nadał mu rytm.
    /// </remarks>
    public async Task<TaskSeries> StartAsync(
        TaskItem task, RecurrenceRule rule, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(rule);

        var starts = task.DoDate ?? clock.Today;
        var fresh = TaskSeries.Create(
            starts, rule, SeriesTemplate.From(task), clock.Now, hlc.Next());

        series.Add(fresh);

        task.JoinSeries(fresh.Id, hlc.Next());
        task.Override(hlc.Next());

        // Reguła schodzi z zadania: od teraz nosi ją seria. Zostawiona tu znaczyłaby
        // dwa źródła prawdy o tym samym rytmie, a przy rozjeździe wygrywałoby to,
        // które akurat ktoś przeczytał.
        task.SetRecurrence(null, hlc.Next());

        await unitOfWork.SaveChangesAsync(ct);
        await TopUpAsync(fresh, ct);

        return fresh;
    }

    /// <summary>
    /// Dopełnienie okna wszystkich serii.
    /// </summary>
    /// <remarks>
    /// Wołane przy starcie, przy powrocie z tła i po każdym scaleniu — tak samo jak
    /// przejście dnia i z tego samego powodu: jest <b>powtarzalne bez skutków ubocznych</b>,
    /// więc dwa urządzenia robią to samo, niezależnie i bez umawiania się, które ma.
    /// </remarks>
    public async Task<TopUpReport> TopUpAsync(CancellationToken ct = default)
    {
        var added = 0;

        foreach (var one in await series.ListAsync(ct))
        {
            added += await FillAsync(one, ct);
        }

        if (added > 0)
        {
            await unitOfWork.SaveChangesAsync(ct);

            if (journal is not null)
            {
                await journal.RecordAsync(
                    "Serie: dopełnienie okna", $"{added} wystąpień", ActivityLevel.Ok, null, ct);
            }
        }

        return new TopUpReport(added);
    }

    public async Task<TopUpReport> TopUpAsync(TaskSeries one, CancellationToken ct = default)
    {
        var added = await FillAsync(one, ct);

        if (added > 0)
        {
            await unitOfWork.SaveChangesAsync(ct);
        }

        return new TopUpReport(added);
    }

    /// <summary>
    /// Koniec serii na danym dniu: ten zostaje, dalszych nie ma.
    /// </summary>
    /// <remarks>
    /// Data końca w regule **i** nagrobki na tym, co już stoi dalej. Sama data końca
    /// zatrzymałaby dokładanie, ale zostawiłaby na siatce wystąpienia postawione wcześniej,
    /// czyli odpowiedziałaby „już nie będzie" na ekranie, na którym widać, że będzie.
    /// </remarks>
    public async Task<int> EndAsync(
        TaskSeries one, DateOnly last, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(one);

        one.EndOn(last, hlc.Next());

        var gone = 0;

        foreach (var occurrence in await series.OccurrencesAsync(one.Id, ct))
        {
            if (occurrence.DoDate > last && Open(occurrence) && !occurrence.Overridden)
            {
                occurrence.Trash(hlc.Next());
                gone++;
            }
        }

        await unitOfWork.SaveChangesAsync(ct);

        return gone;
    }

    /// <summary>
    /// Skasowanie serii: rytm znika, historia zostaje.
    /// </summary>
    /// <remarks>
    /// Wystąpienia minione i odhaczone zostają tym, czym są — to jest zapis tego, co się
    /// wydarzyło, i nie przestaje być prawdą przez to, że rytmu już nie ma. Nagrobek
    /// dostaje seria i to, co stało jeszcze przed nią w przyszłości.
    /// </remarks>
    public async Task<int> DropAsync(TaskSeries one, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(one);

        var today = clock.Today;
        var gone = 0;

        foreach (var occurrence in await series.OccurrencesAsync(one.Id, ct))
        {
            if (occurrence.DoDate >= today && Open(occurrence))
            {
                occurrence.Trash(hlc.Next());
                gone++;
            }
        }

        one.MarkDeleted(hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);

        return gone;
    }

    /// <summary>
    /// Zmiana serii: nowy szablon, nowy rytm, albo jedno i drugie.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Wystąpienia zmienione z ręki zostają nietknięte.</b> To jest ta jedna decyzja,
    /// której model z zapisanym oknem nie da się ominąć, i rozstrzygnięta jest na korzyść
    /// dnia: ten jeden wtorek, który ktoś świadomie przestawił, był ostatnią świadomą
    /// decyzją o tym dniu, a zmiana nazwy całej serii nie jest decyzją o nim.
    /// </para>
    /// <para>
    /// Reszta przyszłych wystąpień jest stawiana od nowa. Przepisywanie ich w miejscu
    /// wyglądałoby taniej, ale zmiana rytmu przestawia dni, a dzień jest częścią
    /// tożsamości wiersza — wiersz zostawiony na starym dniu z nową treścią nie byłby
    /// ani starym wystąpieniem, ani nowym.
    /// </para>
    /// </remarks>
    public async Task<TopUpReport> ChangeAsync(
        TaskSeries one,
        SeriesTemplate? template = null,
        RecurrenceRule? rule = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(one);

        if (template is not null)
        {
            one.SetTemplate(template, hlc.Next());
        }

        if (rule is not null)
        {
            one.SetRule(rule, hlc.Next());
        }

        var today = clock.Today;
        var retired = 0;

        foreach (var occurrence in await series.OccurrencesAsync(one.Id, ct))
        {
            if (occurrence.DoDate >= today && Open(occurrence) && !occurrence.Overridden)
            {
                occurrence.Trash(hlc.Next());
                retired++;
            }
        }

        await unitOfWork.SaveChangesAsync(ct);

        // Stawiane od nowa **po** zapisaniu nagrobków: dopełnianie pomija dni już zajęte,
        // więc puszczone przed nimi nie postawiłoby ani jednego wiersza.
        var fresh = await FillAsync(one, ct);

        if (fresh > 0)
        {
            await unitOfWork.SaveChangesAsync(ct);
        }

        return new TopUpReport(fresh, retired);
    }

    /// <summary>
    /// Dni, które mają wystąpienie, a go nie mają — postawione.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Dzień zajęty poznaje się po dwóch rzeczach i obie są potrzebne.</b> Po
    /// tożsamości — bo nagrobek z wyliczonym identyfikatorem znaczy „ten dzień został
    /// skasowany świadomie" i dopełnienie nie ma prawa go wskrzesić. I po dacie — bo
    /// pierwsze wystąpienie serii ma identyfikator losowy (było zadaniem, zanim dostało
    /// rytm), a wystąpienia z poprzedniego modelu mają go tym bardziej.
    /// </para>
    /// <para>
    /// Nagrobki liczą się razem z żywymi i to jest sedno różnicy wobec poprzedniego
    /// modelu: „tej środy nie będzie" jest tu zapisem trwałym, a nie odwołaniem
    /// doklejonym do reguły, które przy pierwszym obcięciu minionych zmian przepadało.
    /// </para>
    /// </remarks>
    private async Task<int> FillAsync(TaskSeries one, CancellationToken ct)
    {
        var standing = await series.OccurrencesAsync(one.Id, ct);

        var byId = standing.Select(t => t.Id).ToHashSet();
        var byDay = standing.Where(t => t.DoDate is not null)
            .Select(t => t.DoDate!.Value)
            .ToHashSet();

        var template = one.Template;
        var added = 0;

        foreach (var slot in SeriesWindow.Plan(one, clock.Today))
        {
            if (byId.Contains(slot.Id) || byDay.Contains(slot.Date))
            {
                continue;
            }

            tasks.Add(TaskItem.InSeries(one.Id, slot, template, clock.Now, hlc.Next()));
            added++;
        }

        return added;
    }

    private static bool Open(TaskItem task) =>
        task.State is TaskState.Next or TaskState.Scheduled or TaskState.Waiting;
}

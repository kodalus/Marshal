using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Diagnostics;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Series;
using Marshal.Domain.Tasks;

namespace Marshal.Application.UseCases;

/// <summary>Co zrobiło dopełnianie okna.</summary>
public sealed record TopUpReport(int Added, int Retired = 0, int Pruned = 0);

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
            starts, rule, Blend(SeriesTemplate.From(task), rule), clock.Now, hlc.Next());

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
        var pruned = 0;

        foreach (var one in await series.ListAsync(ct))
        {
            var (fresh, gone) = await ReconcileAsync(one, ct);
            added += fresh;
            pruned += gone;
        }

        if (added > 0 || pruned > 0)
        {
            await unitOfWork.SaveChangesAsync(ct);

            if (journal is not null)
            {
                await journal.RecordAsync(
                    "Serie: uzgodnienie okna",
                    $"{added} dołożonych, {pruned} zabranych",
                    ActivityLevel.Ok,
                    null,
                    ct);
            }
        }

        return new TopUpReport(added, 0, pruned);
    }

    public async Task<TopUpReport> TopUpAsync(TaskSeries one, CancellationToken ct = default)
    {
        var (added, pruned) = await ReconcileAsync(one, ct);

        if (added > 0 || pruned > 0)
        {
            await unitOfWork.SaveChangesAsync(ct);
        }

        return new TopUpReport(added, 0, pruned);
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

        var before = one.Rule;

        if (rule is not null)
        {
            one.SetRule(rule, hlc.Next());
        }

        if (template is not null)
        {
            one.SetTemplate(Blend(template, one.Rule), hlc.Next());
        }

        var today = clock.Today;
        var standing = await series.OccurrencesAsync(one.Id, ct);
        var mine = one.Template;

        // Okno stawiane od nowa **tylko wtedy, gdy zmieniły się dni**. Zmiana nazwy,
        // pory czy długości nie przestawia żadnego dnia, więc wyrzucanie
        // sześćdziesięciu wierszy i stawianie sześćdziesięciu nowych byłoby wyłącznie
        // sześćdziesięcioma wpisami w dzienniku synchronizacji — i nowymi
        // identyfikatorami dla dni, które nigdzie się nie ruszyły.
        if (!Moves(before, one.Rule))
        {
            var touched = 0;

            foreach (var occurrence in standing)
            {
                if (occurrence.DoDate >= today && Open(occurrence) && !occurrence.Overridden)
                {
                    occurrence.Restamp(mine, one.Rule.Time, one.Rule.Minutes, hlc.Next);
                    touched++;
                }
            }

            if (touched > 0)
            {
                await unitOfWork.SaveChangesAsync(ct);
            }

            return new TopUpReport(0, 0);
        }

        var retired = 0;

        foreach (var occurrence in standing)
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
        var (fresh, _) = await ReconcileAsync(one, ct);

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
    private async Task<(int Added, int Pruned)> ReconcileAsync(
        TaskSeries one, CancellationToken ct)
    {
        var today = clock.Today;
        var standing = await series.OccurrencesAsync(one.Id, ct);
        var plan = SeriesWindow.Plan(one, today);

        var byId = standing.Select(t => t.Id).ToHashSet();
        var byDay = standing.Where(t => t.DoDate is not null)
            .Select(t => t.DoDate!.Value)
            .ToHashSet();

        var template = one.Template;
        var added = 0;

        foreach (var slot in plan)
        {
            if (byId.Contains(slot.Id) || byDay.Contains(slot.Date))
            {
                continue;
            }

            tasks.Add(TaskItem.InSeries(one.Id, slot, template, clock.Now, hlc.Next()));
            added++;
        }

        // ——— Zabieranie dni, których okno nie chce ———————————————————————————
        //
        // Bez tego kroku okno **nie było funkcją serii i daty**, tylko sumą wszystkiego,
        // co kiedykolwiek którekolwiek urządzenie policzyło. A policzyć mogły różnie:
        // początek serii jest polem scalanym, więc telefon i pulpit, które przeniosły
        // rytm przed zsynchronizowaniem się, zaczynały ją od różnych dni — każde
        // stawiało wtedy własny zestaw dat. Po scaleniu początek się uzgadniał, a oba
        // zestawy zostawały na siatce obok siebie. Rozjazd, którego nic nie zamykało.
        //
        // Dzień zabiera się tylko wtedy, gdy spełnia **wszystkie** warunki: jest przed
        // nami, jest jeszcze otwarty, nie został zmieniony z ręki i nie ma go w planie.
        // Minione i odhaczone zostają, bo to zapis tego, co było; zmienione z ręki
        // zostają, bo były ostatnią świadomą decyzją o swoim dniu.
        var wanted = plan.Select(z => z.Date).ToHashSet();
        var pruned = 0;

        foreach (var occurrence in standing)
        {
            if (occurrence.DoDate is { } day
                && day > today
                && Open(occurrence)
                && !occurrence.Overridden
                && !wanted.Contains(day))
            {
                occurrence.Trash(hlc.Next());
                pruned++;
            }
        }

        return (added, pruned);
    }

    /// <summary>
    /// Szablon z porą i długością <b>serii</b>, nie tego jednego dnia.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Szablon zdejmuje się z zadania, a zadanie bywa wystąpieniem przestawionym
    /// wyjątkowo: ktoś przeciągnął dzisiejszy blok z dziewiątej na dziesiątą. Wzięta
    /// wprost, taka godzina stawała się godziną wszystkich następnych dni — czyli
    /// wyjątek jednego dnia przepisywał się na całą serię, dokładnie wbrew temu, po co
    /// reguła pamięta porę.
    /// </para>
    /// <para>
    /// Pora i długość zapamiętane w regule <b>są</b> decyzją o serii i dlatego mają
    /// pierwszeństwo. Zadanie odpowiada tylko tam, gdzie reguła milczy — bo wtedy nic
    /// jej jeszcze nie powiedziało.
    /// </para>
    /// </remarks>
    public static SeriesTemplate Blend(SeriesTemplate template, RecurrenceRule rule)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(rule);

        return template with
        {
            DoTime = rule.Time ?? template.DoTime,
            EstimatedMinutes = rule.Minutes ?? template.EstimatedMinutes,
            Leads = rule.Leads.Count > 0 ? rule.Leads : template.Leads,
        };
    }

    /// <summary>
    /// Czy nowa reguła przestawia <b>dni</b>, a nie tylko to, co dzień niesie.
    /// </summary>
    /// <remarks>
    /// Kształt rytmu, data końca i licznik pozostałych wystąpień wyznaczają, które dni
    /// wypadają. Pora, długość i wyprzedzenia nie wyznaczają niczego — zmieniają
    /// wyłącznie treść dni, które i tak wypadłyby tam, gdzie wypadają. Rozróżnienie
    /// jest potrzebne, bo zapis karty niesie regułę zawsze, także wtedy, gdy nikt rytmu
    /// nie dotknął: pora i długość są w niej zapamiętywane, więc samo poprawienie
    /// tytułu wyglądało jak zmiana rytmu i stawiało całe okno od nowa.
    /// </remarks>
    private static bool Moves(RecurrenceRule before, RecurrenceRule after) =>
        before.Shape != after.Shape
        || before.Until != after.Until
        || before.Count != after.Count;

    private static bool Open(TaskItem task) =>
        task.State is TaskState.Next or TaskState.Scheduled or TaskState.Waiting;
}

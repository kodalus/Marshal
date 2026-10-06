using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Diagnostics;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Series;
using Marshal.Domain.Tasks;

namespace Marshal.Application.UseCases;

public sealed record MigrationReport(int Converted, int Collapsed, int Spawned, int Twins = 0);

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
        // Najpierw naprawa, potem przenoszenie. Serie bliźniacze powstały **u tych,
        // którzy przenieśli rytmy, zanim tożsamość serii stała się wyliczana**: każde
        // urządzenie zakładało wtedy własną. Krok jest tani przy kilkunastu seriach
        // i po sprzątnięciu nie ma już czego sprzątać.
        var twins = await CollapseSeriesAsync(ct);

        // Osobna naprawa, przed wyjściem „nie ma czego przenosić": dotyczy serii już
        // przeniesionych, więc po pierwszym uruchomieniu to jest jedyne miejsce,
        // w którym jeszcze cokolwiek się tu dzieje.
        var claimed = await ClaimMirrorsAsync(ct);

        var carriers = (await tasks.RecurringAsync(ct))
            .Where(t => t.Recurrence is not null && t.SeriesId is null)
            .ToList();

        if (carriers.Count == 0)
        {
            if (claimed > 0 && journal is not null)
            {
                await journal.RecordAsync(
                    "Serie: przyznanie odbić bez właściciela",
                    $"{claimed} wystąpień",
                    ActivityLevel.Ok,
                    null,
                    ct);
            }

            return new MigrationReport(0, 0, 0, twins);
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

            // Szablon z porą i długością **serii**, nie tego jednego dnia: zadanie
            // niosące regułę bywa wystąpieniem przestawionym wyjątkowo, a reguła
            // pamięta porę właśnie po to, żeby ten wyjątek nie przepisał się na resztę.
            var moved = Calendared(rule);

            var fresh = TaskSeries.Create(
                Phase(carrier),
                moved,
                SeriesService.Blend(SeriesTemplate.From(carrier), moved),
                clock.Now,
                hlc.Next(),

                // Tożsamość **wyliczona z zadania, które regułę niosło**, a nie losowana.
                // Przeniesienie odbywa się na każdym urządzeniu osobno; przy losowanej
                // telefon i pulpit zakładały dla tego samego starego rytmu dwie różne
                // serie, każda z własnym oknem — czyli ten sam błąd, przed którym miała
                // chronić cała przebudowa, tylko o warstwę wyżej.
                SeriesId.Of(carrier.Id));

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
                $"{converted} serii, {collapsed} kopii do kosza, {filled.Added} wystąpień"
                    + (twins > 0 ? $", {twins} bliźniaczych serii zlanych" : string.Empty)
                    + (claimed > 0 ? $", {claimed} odbić przyznanych" : string.Empty),
                ActivityLevel.Ok,
                null,
                ct);
        }

        return new MigrationReport(converted, collapsed, filled.Added);
    }

    /// <summary>
    /// Przyznanie wystąpieniu odbicia, które w kalendarzu już stoi, a u nikogo nie należy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Naprawa po tym, że wystąpienie zmienione z siatki nie dostawało znacznika
    /// „zmienione z ręki". Wiersz nietknięty nie jedzie synchronizacją — drugie
    /// urządzenie ma go policzyć samo — więc do tamtej strony nie docierała ani zmiana,
    /// ani powstałe przy niej <b>wskazanie na wydarzenie w Google</b>. Wydarzenie jednak
    /// powstawało, bo wysyłka do kalendarza idzie po każdym zapisie. Na drugim urządzeniu
    /// stało więc obok policzonego przez nie wystąpienia jako wydarzenie cudze: ta sama
    /// rzecz dwa razy, raz bez możliwości otwarcia jak własnej.
    /// </para>
    /// <para>
    /// Sam znacznik wystarczy do naprawy: wiersz przestaje być wyliczalny, dziennik
    /// zapisuje go <b>w całości</b> — razem ze wskazaniem — i druga strona rozpoznaje
    /// wydarzenie jako odbicie swojego zadania, więc przestaje je rysować osobno.
    /// Pytane wąsko, o wystąpienia zaplanowane: odhaczone i wyrzucone jechały dziennikiem
    /// od początku, bo z definicji wyliczalnego wypadały same.
    /// </para>
    /// </remarks>
    private async Task<int> ClaimMirrorsAsync(CancellationToken ct)
    {
        var claimed = 0;

        foreach (var occurrence in await series.AllOccurrencesAsync(ct))
        {
            if (occurrence is
                {
                    SeriesId: not null,
                    Overridden: false,
                    State: TaskState.Scheduled,
                    SharedEventId: { Length: > 0 },
                })
            {
                occurrence.Override(hlc.Next());
                claimed++;
            }
        }

        if (claimed > 0)
        {
            await unitOfWork.SaveChangesAsync(ct);
        }

        return claimed;
    }

    /// <summary>
    /// Zlanie serii bliźniaczych — jednorazowa naprawa po losowanej tożsamości serii.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Przeniesienie rytmów chodzi na każdym urządzeniu osobno. Dopóki tożsamość serii
    /// była losowana, telefon i pulpit zakładały dla tego samego starego rytmu dwie
    /// różne serie — każda z własnym oknem, własnymi identyfikatorami dni i własnym
    /// wierszem na każdy dzień. Rytm pojawiał się przez to dwa razy, dokładnie tak jak
    /// przed przebudową, tylko z innego powodu.
    /// </para>
    /// <para>
    /// <b>Zostaje ta, na którą wskazuje zadanie.</b> Nie najmniejszy identyfikator:
    /// przynależność zadania jest polem scalanym, więc po synchronizacji oba urządzenia
    /// czytają w nim to samo — a razem z tym zadaniem zostają jego załączniki, podzadania
    /// i wszystko, co ktoś do niego dopisał. Dopiero przy remisie rozstrzyga identyfikator,
    /// bo wybór musi wypaść tak samo po obu stronach.
    /// </para>
    /// <para>
    /// Wystąpienia serii przegranej są duplikatami dni, które zostają: otwarte idą do
    /// kosza, a minione i odhaczone przestają należeć do czegokolwiek i zostają zwykłymi
    /// zadaniami. Historia ma zostać historią, a nie zniknąć razem z serią, której nigdy
    /// nie miało być.
    /// </para>
    /// </remarks>
    private async Task<int> CollapseSeriesAsync(CancellationToken ct)
    {
        var all = await series.ListAsync(ct);

        if (all.Count < 2)
        {
            return 0;
        }

        var groups = all
            .GroupBy(z => (
                z.Template.Title,
                z.Template.AreaId,
                z.Template.ProjectId,
                z.Rule.Shape))
            .Where(g => g.Count() > 1)
            .ToList();

        if (groups.Count == 0)
        {
            return 0;
        }

        var occurrences = (await series.AllOccurrencesAsync(ct))
            .Where(t => t.SeriesId is not null)
            .GroupBy(t => t.SeriesId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var today = clock.Today;
        var gone = 0;

        foreach (var group in groups)
        {
            // Seria, na którą wskazuje jakiekolwiek zadanie zmienione z ręki — czyli to,
            // które niosło rytm przed przeniesieniem. Przy remisie najmniejszy
            // identyfikator, bo wybór musi wypaść tak samo na obu urządzeniach.
            var keeper = group
                .OrderByDescending(z => occurrences.GetValueOrDefault(z.Id, [])
                    .Any(t => t.Overridden))
                .ThenBy(z => z.Id)
                .First();

            foreach (var loser in group.Where(z => z.Id != keeper.Id))
            {
                foreach (var occurrence in occurrences.GetValueOrDefault(loser.Id, []))
                {
                    if (occurrence.DoDate >= today
                        && occurrence.State is TaskState.Scheduled or TaskState.Next
                            or TaskState.Waiting)
                    {
                        occurrence.Trash(hlc.Next());
                    }
                    else
                    {
                        occurrence.JoinSeries(null, hlc.Next());
                    }
                }

                loser.MarkDeleted(hlc.Next());
                gone++;
            }
        }

        await unitOfWork.SaveChangesAsync(ct);

        return gone;
    }

    /// <summary>
    /// Dzień, od którego liczy się rytm przeniesionej serii.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Z pola zapisanego, a nie z dzisiejszej daty.</b> Stare przejście dnia
    /// przesuwało datę wykonania zaległego zadania na dzisiejszą — na każdym urządzeniu
    /// osobno i w dniu, w którym akurat je otwarto. Wzięta wprost dawała więc telefonowi
    /// i pulpitowi różne początki tej samej serii, a różny początek to inna faza rytmu
    /// i inny zestaw dni. Tożsamość serii była już wyliczana, więc obie wersje były
    /// <b>tą samą serią z dwoma oknami</b> — i oba zostawały na siatce obok siebie.
    /// </para>
    /// <para>
    /// „Zaległe od" jest tu najlepszą odpowiedzią: zapisuje dzień, na który rzecz była
    /// umówiona, <b>zanim</b> przejście dnia zaczęło ją przesuwać. Dalej data wykonania,
    /// a na końcu dzień założenia zadania — wszystkie trzy są polami zapisanymi, więc po
    /// zsynchronizowaniu oba urządzenia czytają w nich to samo.
    /// </para>
    /// </remarks>
    private DateOnly Phase(TaskItem carrier) =>
        carrier.CarriedSince
            ?? carrier.DoDate
            ?? DateOnly.FromDateTime(carrier.CreatedAt.Date);

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

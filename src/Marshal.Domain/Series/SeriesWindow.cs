using Marshal.Domain.Recurrence;

namespace Marshal.Domain.Series;

/// <summary>Jedno miejsce w oknie serii: dzień, tożsamość i to, co dzień niesie.</summary>
public sealed record SeriesSlot(Guid Id, DateOnly Date, TimeOnly? Time, int? Minutes);

/// <summary>
/// Okno serii: które wystąpienia mają w tej chwili istnieć.
/// </summary>
/// <remarks>
/// <para>
/// <b>Liczone, nie pamiętane.</b> Okno jest funkcją serii i dzisiejszej daty, więc
/// dwa urządzenia dochodzą do tej samej listy dni i tych samych tożsamości, bez
/// umawiania się, które ma dokładać. Dopełnianie wolno puszczać ile razy się chce:
/// dni już postawione są pomijane po tożsamości, a tożsamość nie zależy od tego, kiedy
/// urządzenie zostało otwarte.
/// </para>
/// <para>
/// <b>Rytm liczony od początku serii, a stawiane tylko dni od dziś.</b> Faza musi iść
/// od <see cref="TaskSeries.Starts"/>, bo „co drugi dzień od wtorku" nie da się odtworzyć
/// z żadnej późniejszej daty — liczone od dziś wypadałoby raz na wtorki, raz na środy,
/// zależnie od dnia uruchomienia. Dni minione natomiast nie są dokładane: historia ma
/// zostać taka, jaka była, a nie dorosnąć wstecz o wystąpienia, których nigdy nie było.
/// </para>
/// <para>
/// <b>Horyzont liczony zdarzeniami.</b> Cykl bez końca stoi na sześćdziesięciu
/// zdarzeniach do przodu — dwa miesiące przy rytmie codziennym, czternaście przy
/// tygodniowym. Liczba, a nie zakres dni, bo zdarzenie jest tym, co kosztuje: przy
/// horyzoncie liczonym dniami rytm codzienny kosztował trzydzieści razy więcej od
/// tygodniowego przy tym samym napisie w ustawieniach.
/// </para>
/// </remarks>
public static class SeriesWindow
{
    /// <summary>
    /// Ile zdarzeń trzymać do przodu w cyklu bez daty końca.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Liczba podana wprost i jedna dla wszystkich rytmów. Było trzydzieści wystąpień
    /// albo miesiąc dni — późniejsze z dwóch — i to był kompromis podyktowany kosztem
    /// dziennika synchronizacji, bo wystąpienie postawione z okna jechało wtedy na drugie
    /// urządzenie w pięćdziesięciu kilku wierszach. Tamten koszt rozkłada dziś
    /// <c>SeriesService.MaxPerRun</c>, a liczba zdarzeń jest tym, o czym da się
    /// powiedzieć, ile ich jest.
    /// </para>
    /// <para>
    /// Sufit niepotrzebny: licznik zdarzeń jest sufitem. Przy horyzoncie liczonym dniami
    /// rytm godzinowy założony przez pomyłkę stawiał tysiące wierszy i trzeba było go
    /// osobno zatrzymywać — tu stawia sześćdziesiąt, jak każdy inny.
    /// </para>
    /// </remarks>
    public const int Ahead = 60;

    /// <summary>Jak daleko wolno szukać dni. Dalej niż ktokolwiek przewinie kalendarz.</summary>
    private const int Reach = 30;

    /// <summary>
    /// Wystąpienia, które mają istnieć od dziś w przód — w kolejności dni.
    /// </summary>
    /// <remarks>
    /// Dzień dzisiejszy wchodzi do okna. Wystąpienie na dziś jest tym, po które się
    /// sięga najczęściej, a odcięcie okna „od jutra" kazałoby czekać do północy na
    /// zadanie umówione na teraz.
    /// </remarks>
    public static IReadOnlyList<SeriesSlot> Plan(TaskSeries series, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(series);

        // Rytm zaczepiony na wykonaniu **nie ma** dat do wyliczenia. „Co 3 dni od
        // wykonania" mówi, kiedy wypadnie następne, dopiero gdy poprzednie zostanie
        // odhaczone — i to jest cecha rytmu, nie brak w programie. Okno na jedno
        // wystąpienie jest tu jedyną uczciwą odpowiedzią: sześćdziesiąt wierszy
        // postawionych na datach zgadniętych z założenia „wszystko na czas" trzeba by
        // przy każdym spóźnieniu skasować i postawić od nowa, a kalendarz pokazywałby
        // do tej chwili rozkład, którego nikt nie obiecał.
        var reach = series.Rule.Anchor == RecurrenceAnchor.FromCompletion ? 1 : Ahead;
        var slots = new List<SeriesSlot>(reach);

        foreach (var (date, time, minutes) in Walk(series, today.AddYears(Reach)))
        {
            // Minione pomijane, ale **po przejściu przez nie**: licznik pozostałych
            // wystąpień zużywa się po drodze, więc seria „jeszcze pięć razy" liczona
            // od dziś dałaby pięć razy więcej, niż obiecano.
            if (date < today)
            {
                continue;
            }

            if (slots.Count >= reach)
            {
                break;
            }

            slots.Add(new SeriesSlot(OccurrenceId.For(series.Id, date), date, time, minutes));
        }

        return slots;
    }

    /// <summary>
    /// Dni serii od jej początku: najpierw sam początek, potem to, co po nim.
    /// </summary>
    /// <remarks>
    /// <b>Początek jest wystąpieniem, nie zaczepieniem.</b> „Co drugi dzień od
    /// poniedziałku" wypada w ten poniedziałek, a nie dopiero w środę — tak samo jak
    /// w poprzednim modelu, gdzie zadanie niosące regułę <b>było</b> pierwszym
    /// wystąpieniem, a kolejne liczyło się od niego. Liczenie wyłącznie przez „następne
    /// po" gubiło ten jeden dzień, i gubiło go po cichu: przy rytmie codziennym następne
    /// po dniu poprzednim wypada akurat na początku, więc błąd pokazywał się dopiero
    /// przy odstępie większym od jednego.
    ///
    /// Licznik pozostałych wystąpień <b>liczy bieżące</b> — tak mówi reguła i tak liczy
    /// wyznaczanie następnego dnia. Początek nie pomniejsza go więc osobno: seria
    /// „jeszcze pięć razy" to początek i cztery dni po nim, a pomniejszenie z ręki
    /// odbierało jej jeden dzień.
    /// </remarks>
    private static IEnumerable<(DateOnly Date, TimeOnly? Time, int? Minutes)> Walk(
        TaskSeries series, DateOnly bound)
    {
        var rule = series.Rule;
        var first = series.Starts;

        if (rule.Until is null || first <= rule.Until)
        {
            var change = rule.ChangeOn(first);

            if (change is not { Dropped: true })
            {
                yield return (
                    change?.Day ?? first,
                    change?.Time ?? rule.Time,
                    change?.Minutes ?? rule.Minutes);
            }
        }

        foreach (var slot in RecurrenceSchedule.Following(rule, first, bound))
        {
            yield return (slot.Date, slot.Time, slot.Minutes);
        }
    }
}

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
/// <b>Horyzont liczony dniami i wystąpieniami naraz.</b> Sześćdziesiąt wystąpień to
/// dwa miesiące przy rytmie codziennym i czternaście przy tygodniowym — przy samym
/// liczniku przewinięcie kalendarza o kwartał pokazywałoby przy codziennej serii pustkę.
/// Stąd późniejsze z dwóch: sześćdziesiąt wystąpień albo kwartał. Sufit jest po to, żeby
/// rytm godzinowy założony przez pomyłkę nie postawił dziesięciu tysięcy wierszy.
/// </para>
/// </remarks>
public static class SeriesWindow
{
    /// <summary>
    /// Ile wystąpień trzymać do przodu.
    /// </summary>
    /// <remarks>
    /// Było sześćdziesiąt i to była liczba wzięta z wygody oglądania, bez policzenia,
    /// ile kosztuje. Każdy wiersz zapisany to <b>około sześćdziesięciu wierszy dziennika
    /// synchronizacji</b> — po jednym na każde pole, plus tyleż znaczników pól. Okno
    /// kwartalne przy rytmie codziennym znaczyło więc blisko sześć tysięcy wierszy na
    /// jedną serię, a kilkanaście serii zatrzymywało bazę na tyle długo, że aplikacja
    /// nie wstawała ze splash-ekranu.
    /// </remarks>
    public const int Ahead = 30;

    /// <summary>
    /// Ile dni do przodu trzymać, gdy licznik kończy się wcześniej.
    /// </summary>
    /// <remarks>
    /// Miesiąc, nie kwartał. Przy rytmie codziennym to jest ta liczba, która rozstrzyga
    /// o koszcie — bo dzień kosztuje wiersz — a okno przesuwa się przy każdym
    /// uruchomieniu i przy każdym przejściu dnia, więc „miesiąc do przodu" znaczy
    /// miesiąc liczony od dziś, a nie od instalacji.
    /// </remarks>
    public const int Days = 31;

    /// <summary>Sufit na jedną serię — ochrona przed rytmem założonym przez pomyłkę.</summary>
    public const int Ceiling = 400;

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
        var keep = series.Rule.Anchor == RecurrenceAnchor.FromCompletion
            ? today.AddDays(-1)
            : today.AddDays(Days);
        var slots = new List<SeriesSlot>(Ahead);

        foreach (var (date, time, minutes) in Walk(series, today.AddYears(Reach)))
        {
            // Minione pomijane, ale **po przejściu przez nie**: licznik pozostałych
            // wystąpień zużywa się po drodze, więc seria „jeszcze pięć razy" liczona
            // od dziś dałaby pięć razy więcej, niż obiecano.
            if (date < today)
            {
                continue;
            }

            if (slots.Count >= Ceiling || (slots.Count >= reach && date > keep))
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

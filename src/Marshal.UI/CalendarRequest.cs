namespace Marshal.UI;

/// <summary>
/// Co otworzyć razem z kalendarzem po wejściu spoza okna.
/// </summary>
/// <remarks>
/// <para>
/// Osobny typ zamiast kilku parametrów obok siebie, bo prośba przechodzi przez trzy
/// warstwy i po drodze zostaje zapamiętana: widget, okno Androida, model widoku. Cztery
/// wartości niesione luzem znaczyłyby cztery pola do zapamiętania i cztery do wyzerowania
/// po spełnieniu — a pominięcie któregokolwiek zostawia prośbę w połowie spełnioną.
/// </para>
/// <para>
/// Zadanie albo wydarzenie, nigdy oba naraz. Zadanie ma u nas swój identyfikator
/// i własną nakładkę szczegółu; wydarzenie z podłączonego kalendarza identyfikatora
/// u nas nie ma i ma własną kartę — rozpoznaje się je po kalendarzu, z którego pochodzi,
/// i po identyfikatorze u źródła. Puste jedno i drugie znaczy „sam kalendarz".
/// </para>
/// </remarks>
/// <param name="Task">Zadanie Marshala do otwarcia.</param>
/// <param name="Source">Kalendarz, z którego pochodzi wydarzenie.</param>
/// <param name="Event">Identyfikator wydarzenia u źródła.</param>
/// <param name="Day">
/// Dzień, pod którym stało dotknięte wydarzenie. Wydarzenie nie niesie swojej daty przez
/// kafelek, a kafelek pokazuje dowolny dzień — bez tego kalendarz nie wiedziałby, dokąd
/// się przesunąć. Przy zadaniu zbędny: zadanie zna swój dzień wykonania.
/// </param>
public sealed record CalendarRequest(
    Guid? Task = null,
    Guid? Source = null,
    string? Event = null,
    DateOnly? Day = null)
{
    /// <summary>Czy prośba wskazuje wydarzenie z podłączonego kalendarza.</summary>
    public bool IsEvent => Source is not null && Event is { Length: > 0 } && Day is not null;
}

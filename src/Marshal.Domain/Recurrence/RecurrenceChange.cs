namespace Marshal.Domain.Recurrence;

/// <summary>
/// Zmiana pojedynczego wystąpienia rytmu: odwołanie albo przełożenie.
/// </summary>
/// <remarks>
/// <para>
/// Rytm mówi, kiedy coś wypada; życie mówi, że akurat w tę jedną środę nie. Bez tego
/// były dwie odpowiedzi i obie złe: zmienić regułę (czyli wszystkie pozostałe środy)
/// albo nie zrobić nic.
/// </para>
/// <para>
/// <b>Kluczem jest dzień, w którym wystąpienie wypadało z reguły</b>, a nie dzień,
/// na który je przełożono. Rytm biegnie dalej po swojemu: środa przeniesiona na czwartek
/// nie przesuwa kolejnej środy. Gdyby kluczem był dzień docelowy, przełożenie dwa razy
/// z rzędu gubiłoby ślad, po którym wiadomo, czego dotyczy.
/// </para>
/// <para>
/// Zmiany dotyczą wyłącznie wystąpień, które **jeszcze nie powstały**. To, które niesie
/// regułę, jest zwykłym zadaniem i zmienia się jak każde inne — przeciągnięciem po
/// siatce albo w karcie.
/// </para>
/// </remarks>
/// <param name="Date">Dzień z reguły. Tożsamość wystąpienia w serii.</param>
/// <param name="Dropped">Czy wystąpienie odwołano. Wtedy reszta pól nic nie znaczy.</param>
/// <param name="Day">Dokąd przełożone. Puste znaczy „w swoim dniu".</param>
/// <param name="Time">Nowa pora. Pusta znaczy „ta sama, co w rytmie".</param>
/// <param name="Minutes">Długość tego jednego razu. Pusta znaczy „taka, jak w rytmie".</param>
/// <remarks>
/// Odwołanie osobnym polem, a nie brakiem dnia docelowego. Tak było do pierwszego
/// wystąpienia, któremu zmieniono samą długość: zmiana bez przełożenia wyglądała wtedy
/// jak odwołanie i wystąpienie znikało z siatki zamiast stać się dłuższe. Pytanie
/// „czy to się odbędzie" jest innym pytaniem niż „gdzie i jak długo", więc ma własne pole.
/// </remarks>
public sealed record RecurrenceChange(
    DateOnly Date,
    bool Dropped = false,
    DateOnly? Day = null,
    TimeOnly? Time = null,
    int? Minutes = null);

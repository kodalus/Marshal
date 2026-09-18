using Marshal.Domain.Calendar;

namespace Marshal.Application.Calendar;

/// <summary>Wydarzenie w postaci, w jakiej chcemy je zapisać u źródła.</summary>
/// <remarks>
/// Całodniowe świadomie poza zakresem tej wersji: data bez godziny idzie do Google
/// innym polem i w innym formacie, a zgadywanie tego formatu bez kompilatora pod ręką
/// kosztuje pełny przebieg CI za każdą pomyłkę. Zapis czegoś, czego nie umiemy odczytać
/// z powrotem, byłby gorszy niż brak zapisu.
/// </remarks>
public sealed record CalendarDraft(
    string Title, DateTimeOffset Start, DateTimeOffset End, string? Location = null,

    /// <summary>
    /// Wydarzenie całodniowe: data bez godziny.
    /// </summary>
    /// <remarks>
    /// Nie to samo co „od północy do północy". Google trzyma jedno i drugie inaczej —
    /// całodniowe ma samą datę i nie przelicza się na strefę, bo data nie ma strefy.
    /// Wysłane jako chwila stałoby o północy czasu lokalnego i u kogoś na wschód
    /// wypadałoby dzień wcześniej.
    /// </remarks>
    bool AllDay = false);

/// <summary>
/// Zapis do kalendarza zewnętrznego (spec 10.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>To jest jedyne miejsce w projekcie, w którym awaria niszczy dane poza aplikacją.</b>
/// Wszystko inne da się odtworzyć z dziennika zmian albo pobrać jeszcze raz; skasowane
/// wydarzenie w cudzym kalendarzu nie wraca. Stąd trzy zasady, których nie wolno tu
/// obejść:
/// </para>
/// <para>
/// 1. <b>Łatamy, nie nadpisujemy.</b> Zapis całego wydarzenia wyczyściłby wszystko,
/// czego nie znamy — uczestników, przypomnienia, opis, załączniki. Wysyłamy wyłącznie
/// pola, które użytkownik zmienił.
/// </para>
/// <para>
/// 2. <b>Najpierw u źródła, potem u nas.</b> Odwrotna kolejność zostawiałaby przy
/// nieudanym zapisie wydarzenie, które widać w Marshalu, a którego nie ma nigdzie indziej.
/// </para>
/// <para>
/// 3. <b>Kasujemy tylko to, co wskazano wprost.</b> Żadnego sprzątania „przy okazji".
/// </para>
/// </remarks>
public interface ICalendarWriter
{
    CalendarKind Kind { get; }

    /// <summary>Nowe wydarzenie. Oddaje identyfikator nadany przez źródło.</summary>
    Task<string> CreateAsync(
        CalendarSource source, CalendarDraft draft, CancellationToken ct = default);

    /// <summary>Zmiana istniejącego. Łata podane pola, reszty nie dotyka.</summary>
    Task UpdateAsync(
        CalendarSource source, string externalId, CalendarDraft draft,
        CancellationToken ct = default);

    /// <summary>
    /// Zmiana samej nazwy wydarzenia.
    /// </summary>
    /// <remarks>
    /// Osobno od <see cref="UpdateAsync"/>, bo tamta wysyła też godziny — a godziny
    /// wydarzenia całodniowego idą do Google innym polem i w innym formacie. Odhaczenie
    /// całodniowego przez pełną zmianę zrobiłoby z niego wydarzenie o godzinie, i to
    /// w cudzym kalendarzu. Tu leci jedno pole, więc nie ma czego zepsuć.
    /// </remarks>
    Task RenameAsync(
        CalendarSource source, string externalId, string title, CancellationToken ct = default);

    Task DeleteAsync(CalendarSource source, string externalId, CancellationToken ct = default);

    /// <summary>
    /// Dopisanie osoby do gości wydarzenia. Oddaje fałsz, gdy już tam była.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wykonanie <b>musi</b> odczytać obecną listę gości u źródła i wysłać ją w całości
    /// razem z dopisaną osobą. Lista gości jest jednym polem, którego wartością jest
    /// tablica — łatanie scala pola, ale nie zagląda do środka tablicy. Wysłanie samej
    /// dopisywanej osoby nie znaczy „dodaj ją", tylko „od teraz gośćmi są wyłącznie ci
    /// wymienieni" — i reszta dostaje powiadomienie, że została z wydarzenia usunięta.
    /// </para>
    /// <para>
    /// Odczyt z <b>Google</b>, nie z naszej kopii: gości w ogóle nie trzymamy, więc
    /// nasza lista byłaby pusta i zdmuchnęłaby wszystkich.
    /// </para>
    /// <para>
    /// Między odczytem a zapisem jest szczelina, w którą mieści się cudza zmiana.
    /// Wykonanie ma ją zamknąć znacznikiem wersji: zapis tylko wtedy, gdy wydarzenie
    /// jest nadal w tej wersji, którą przeczytaliśmy. Nieudany zapis jest tu właściwym
    /// zachowaniem — lepiej powtórzyć odczyt niż po cichu skasować komuś zaproszenie.
    /// </para>
    /// </remarks>
    Task<bool> InviteAsync(
        CalendarSource source, string externalId, string email, CancellationToken ct = default);
}

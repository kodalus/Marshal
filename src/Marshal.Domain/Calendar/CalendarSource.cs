using Marshal.Domain.Primitives;

namespace Marshal.Domain.Calendar;

public enum CalendarKind
{
    /// <summary>Kalendarz z konta Google, po identyfikatorze.</summary>
    Google = 0,

    /// <summary>Kanał iCal pobierany po adresie.</summary>
    Ical = 1,
}

/// <summary>
/// Kalendarz, który aplikacja pokazuje (spec 10.1).
/// </summary>
/// <remarks>
/// <para>
/// Synchronizowany, bo „które kalendarze pokazuję i w jakim kolorze" jest decyzją,
/// a decyzje wędrują między urządzeniami. Same wydarzenia **nie** — zob.
/// <see cref="CalendarEvent"/>.
/// </para>
/// <para>
/// Identyfikator zewnętrzny jest ten sam na obu urządzeniach: dla Google to identyfikator
/// kalendarza w koncie, dla iCal adres kanału. Dlatego wystarczy zsynchronizować sam
/// wybór, a każde urządzenie pobiera treść samo.
/// </para>
/// </remarks>
public sealed class CalendarSource : Entity
{
    private CalendarSource()
    {
        ExternalId = string.Empty;
        Name = string.Empty;
    }

    public CalendarSource(
        Guid id,
        DateTimeOffset createdAt,
        Hlc updatedAt,
        CalendarKind kind,
        string externalId,
        string name,
        string? color = null,
        string? account = null)
        : base(id, createdAt, updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Kind = kind;
        ExternalId = externalId.Trim();
        Name = name.Trim();
        Color = color;
        Account = string.IsNullOrWhiteSpace(account) ? null : account.Trim();
        IsVisible = true;
    }

    public CalendarKind Kind { get; private set; }

    /// <summary>Identyfikator kalendarza Google albo adres kanału iCal.</summary>
    public string ExternalId { get; private set; }

    public string Name { get; private set; }

    /// <summary>Kolor na kalendarz (spec 10.1). Puste = kolor domyślny okna.</summary>
    public string? Color { get; private set; }

    /// <summary>Ukryty zostaje podłączony, ale nie zaśmieca siatki.</summary>
    public bool IsVisible { get; private set; }

    /// <summary>
    /// Konto Google, z którego pochodzi ten kalendarz. Puste znaczy konto główne.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Adres pocztowy, nie wewnętrzny identyfikator, i to jest rozstrzygnięcie.
    /// Wiersz podłączenia jedzie synchronizacją na drugie urządzenie, a żeton zostaje
    /// tam, gdzie powstał — bo jest tajemnicą tego urządzenia. Drugie urządzenie musi
    /// więc umieć **rozpoznać to samo konto po czymś, co obie strony widzą tak samo**,
    /// a jedyną taką rzeczą jest adres. Identyfikator nadany przy dodawaniu byłby
    /// lokalny i po drugiej stronie nie znaczyłby nic.
    /// </para>
    /// <para>
    /// Puste zostaje dla wszystkiego, co podłączono, zanim konta w ogóle istniały,
    /// i dla kanałów iCal, które konta nie mają. Puste znaczy „konto główne", czyli to,
    /// którym aplikacja synchronizuje przez Dysk — a nie „nie wiadomo".
    /// </para>
    /// </remarks>
    public string? Account { get; private set; }

    /// <summary>
    /// Czy ten kalendarz wolno tylko czytać.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Google podaje to przy każdym kalendarzu jako poziom dostępu: czytelnik, piszący,
    /// właściciel. Kalendarze świąteczne, fazy księżyca i cudze udostępnione bez prawa
    /// zmian są czytelnikami — a wpisuje się je akurat po to, żeby je tylko czytać.
    /// </para>
    /// <para>
    /// Zapisane u nas, nie odpytywane przy każdej czynności: pole „Obszar" musi wiedzieć
    /// <b>zanim</b> ktoś je kliknie, czy ma być czynne. Dowiadywanie się z odmowy znaczy
    /// odmowę po fakcie — a przy przenoszeniu wydarzenia po fakcie znaczy kopię
    /// założoną w nowym kalendarzu, zanim odmowa przyszła ze starego.
    /// </para>
    /// <para>
    /// Odświeżane przy każdym pobraniu, bo dostęp się zmienia: ktoś dopuszcza do
    /// swojego kalendarza albo dostęp odbiera. Nierozpoznane znaczy „wolno pisać" —
    /// zgadywanie w drugą stronę zablokowałoby zapis do kalendarza, do którego wolno.
    /// </para>
    /// </remarks>
    public bool ReadOnly { get; private set; }

    /// <summary>Zmiana poziomu dostępu. Bez znacznika zegara: to jest wiadomość od Google.</summary>
    /// <remarks>
    /// Nie jedzie synchronizacją i nie ma w niej znaczenia. To nie jest decyzja
    /// użytkowniczki o danych, tylko odpowiedź Google na pytanie „czy mi wolno" —
    /// a drugie urządzenie pyta o to samo i dostaje tę samą odpowiedź. Znacznik
    /// zegara logicznego dokładałby tu rozstrzyganie sporu, którego nie ma.
    /// </remarks>
    public void SetReadOnly(bool readOnly) => ReadOnly = readOnly;

    public void Rename(string name, Hlc stamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
        Touch(stamp);
    }

    public void SetColor(string? color, Hlc stamp)
    {
        Color = color;
        Touch(stamp);
    }

    public void SetVisible(bool visible, Hlc stamp)
    {
        IsVisible = visible;
        Touch(stamp);
    }
}

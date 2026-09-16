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
        string? color = null)
        : base(id, createdAt, updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Kind = kind;
        ExternalId = externalId.Trim();
        Name = name.Trim();
        Color = color;
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

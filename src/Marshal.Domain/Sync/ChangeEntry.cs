namespace Marshal.Domain.Sync;

/// <summary>
/// Pojedyncza zmiana jednego pola jednej encji — jeden wiersz dziennika (spec 9.3).
/// </summary>
/// <remarks>
/// <para>
/// Rekord **lokalny**, nie podlega synchronizacji: to on jest tym, co się wysyła.
/// Stąd brak dziedziczenia po <c>Entity</c> i brak własnego nagrobka.
/// </para>
/// <para>
/// Ziarnem jest pole, nie encja, i to jest warunek scalania obiecanego w 9.3.
/// Gdyby wiersz opisywał całą encję, zmiana wagi na telefonie i tytułu na
/// desktopie w trybie offline dałaby po scaleniu jedną z nich, nie obie —
/// wygrałby nowszy zapis całości.
/// </para>
/// </remarks>
public sealed class ChangeEntry
{
    private ChangeEntry()
    {
        EntityType = string.Empty;
        Field = string.Empty;
        Hlc = string.Empty;
    }

    public ChangeEntry(Guid id, string entityType, Guid entityId, string field, string? value, string hlc)
    {
        Id = id;
        EntityType = entityType;
        EntityId = entityId;
        Field = field;
        Value = value;
        Hlc = hlc;
    }

    public Guid Id { get; private set; }

    /// <summary>Nazwa tabeli, na przykład <c>Tasks</c>.</summary>
    public string EntityType { get; private set; }

    public Guid EntityId { get; private set; }

    public string Field { get; private set; }

    /// <summary>Wartość po stronie bazy, zakodowana jako JSON. Puste znaczy null.</summary>
    public string? Value { get; private set; }

    /// <summary>Znacznik zegara logicznego tej zmiany.</summary>
    public string Hlc { get; private set; }

    /// <summary>Czy wiersz trafił już do pliku wysyłkowego na Dysku.</summary>
    public bool Sent { get; private set; }

    public void MarkSent() => Sent = true;
}

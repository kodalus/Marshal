namespace Marshal.Domain.Sync;

/// <summary>
/// Znacznik zegara logicznego dla **bieżącej** wartości jednego pola.
/// </summary>
/// <remarks>
/// Bez tego scalanie per pole jest niewykonalne. Encja ma jeden <c>UpdatedAt</c>,
/// wspólny dla wszystkich pól, więc przy przyjściu zdalnej zmiany tytułu nie dałoby
/// się stwierdzić, czy lokalny tytuł jest od niej nowszy, czy tylko lokalna waga.
/// Odrzucenie albo przyjęcie całej encji gubiłoby jedną z dwóch zmian.
///
/// Rekord lokalny, niesynchronizowany: każde urządzenie liczy go sobie samo
/// z wpisów, które widziało.
/// </remarks>
public sealed class FieldStamp
{
    private FieldStamp()
    {
        EntityType = string.Empty;
        Field = string.Empty;
        Hlc = string.Empty;
    }

    public FieldStamp(string entityType, Guid entityId, string field, string hlc)
    {
        EntityType = entityType;
        EntityId = entityId;
        Field = field;
        Hlc = hlc;
    }

    public string EntityType { get; private set; }

    public Guid EntityId { get; private set; }

    public string Field { get; private set; }

    public string Hlc { get; private set; }

    public void Update(string hlc) => Hlc = hlc;
}

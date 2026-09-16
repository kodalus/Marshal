namespace Marshal.Domain.Primitives;

/// <summary>
/// Wspólna podstawa agregatów podlegających synchronizacji (spec 5.1).
/// </summary>
/// <remarks>
/// Usunięcie jest zawsze logiczne. Fizyczne <c>DELETE</c> w systemie synchronizowanym
/// jest niemożliwe: urządzenie offline nie odróżni „skasowane" od „jeszcze nie znam"
/// i wskrzesi rekord przy najbliższym scaleniu.
/// </remarks>
public abstract class Entity
{
    protected Entity(Guid id, DateTimeOffset createdAt, Hlc updatedAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("Identyfikator nie może być pusty.", nameof(id));

        Id = id;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>Dla EF Core.</summary>
    protected Entity()
    {
    }

    /// <summary>GUID v7, generowany na kliencie — dwa urządzenia offline nie kolidują.</summary>
    public Guid Id { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Hlc UpdatedAt { get; private set; }

    public bool Deleted { get; private set; }

    /// <summary>Znacznik zmiany. Wołane przy każdym zapisie pola.</summary>
    public void Touch(Hlc now)
    {
        if (now < UpdatedAt)
        {
            throw new ArgumentException(
                $"Znacznik {now} jest wcześniejszy niż obecny {UpdatedAt} — zegar logiczny nie może się cofnąć.",
                nameof(now));
        }

        UpdatedAt = now;
    }

    /// <summary>Nagrobek. Rekord zostaje w bazie i w logu synchronizacji.</summary>
    public void MarkDeleted(Hlc now)
    {
        Deleted = true;
        Touch(now);
    }

    public void Restore(Hlc now)
    {
        Deleted = false;
        Touch(now);
    }
}

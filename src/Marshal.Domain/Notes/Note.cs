using Marshal.Domain.Primitives;

namespace Marshal.Domain.Notes;

/// <summary>
/// Notatka — materiał referencyjny (spec 7, rozdz. 11).
/// </summary>
/// <remarks>
/// <para>
/// Powstaje najczęściej z drzewka przetwarzania: coś wpadło do skrzynki, nie wymaga
/// działania, ale ma być pod ręką. Konwersja jest zmianą rodzaju, nie skasowaniem
/// i wpisaniem od nowa — dlatego notatka pamięta, z jakiego zadania powstała.
/// </para>
/// <para>
/// Notatka <b>przypięta</b> to czwarty i piąty horyzont GTD (spec 1.8): wizja i sens,
/// czyli rzeczy, których nie da się odhaczyć. Wchodzą do kroku zerowego przeglądu —
/// są tam po to, żeby reszta działa się po ich przeczytaniu, a nie żeby cokolwiek
/// z nimi zrobić.
/// </para>
/// </remarks>
public sealed class Note : Entity
{
    private Note()
    {
        Title = string.Empty;
        Content = string.Empty;
    }

    private Note(Guid id, DateTimeOffset createdAt, Hlc updatedAt, string title)
        : base(id, createdAt, updatedAt)
    {
        Title = Normalize(title);
        Content = string.Empty;
    }

    public static Note Create(string title, DateTimeOffset now, Hlc stamp) =>
        new(Guid.CreateVersion7(), now, stamp, title);

    /// <summary>
    /// Notatka z pozycji skrzynki. Zachowuje identyfikator źródła, żeby dało się
    /// zobaczyć, skąd się wzięła.
    /// </summary>
    public static Note FromTask(
        Guid taskId, string title, string? content, DateTimeOffset now, Hlc stamp)
    {
        var note = new Note(Guid.CreateVersion7(), now, stamp, title)
        {
            FromTaskId = taskId,
            Content = content ?? string.Empty,
        };

        return note;
    }

    public string Title { get; private set; }

    /// <summary>Markdown.</summary>
    public string Content { get; private set; }

    /// <summary>Opcjonalny — notatka bywa ogólna, a wtedy przypisanie jej do obszaru kłamie.</summary>
    public Guid? AreaId { get; private set; }

    /// <summary>Zadanie, z którego powstała przy przetwarzaniu skrzynki.</summary>
    public Guid? FromTaskId { get; private set; }

    /// <summary>Wchodzi do kroku zerowego przeglądu (spec 8.3).</summary>
    public bool IsPinned { get; private set; }

    public void Rename(string title, Hlc stamp)
    {
        Title = Normalize(title);
        Touch(stamp);
    }

    public void SetContent(string? content, Hlc stamp)
    {
        Content = content ?? string.Empty;
        Touch(stamp);
    }

    public void SetArea(Guid? areaId, Hlc stamp)
    {
        AreaId = areaId == Guid.Empty ? null : areaId;
        Touch(stamp);
    }

    public void SetPinned(bool pinned, Hlc stamp)
    {
        IsPinned = pinned;
        Touch(stamp);
    }

    private static string Normalize(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return title.Trim();
    }
}

using Marshal.Domain.Primitives;

namespace Marshal.Domain.Attachments;

/// <summary>
/// Załącznik podpięty do zadania albo notatki (spec 9.2, katalog <c>files/</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Adresowany treścią.</b> Kluczem pliku jest skrót SHA-256 jego zawartości, nie
/// nazwa ani numer. Dwa takie same zdjęcia wgrane przy dwóch zadaniach zajmują jedno
/// miejsce, a plik raz zapisany nigdy się nie zmienia — to ta sama własność, na której
/// stoją porcje dziennika, i z tego samego powodu: niezmienny plik nie wymaga scalania.
/// </para>
/// <para>
/// Sam wpis się synchronizuje, bo „podpięłam ten plik tutaj" jest decyzją. Treść pliku
/// wędruje osobną drogą — zob. <c>IFileTransport</c> — bo dziennik zmian jest tekstowy
/// i wrzucanie w niego zdjęć rozsadziłoby go co do zasady.
/// </para>
/// </remarks>
public sealed class Attachment : Entity
{
    private Attachment()
    {
        Sha256 = string.Empty;
        FileName = string.Empty;
    }

    public Attachment(
        Guid id,
        DateTimeOffset createdAt,
        Hlc updatedAt,
        string sha256,
        string fileName,
        long size,
        Guid? taskId = null,
        Guid? noteId = null)
        : base(id, createdAt, updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (taskId is null && noteId is null)
        {
            throw new ArgumentException(
                "Załącznik musi być do czegoś podpięty — inaczej nikt go nigdy nie zobaczy.",
                nameof(taskId));
        }

        Sha256 = sha256;
        FileName = fileName;
        Size = size;
        TaskId = taskId;
        NoteId = noteId;
    }

    /// <summary>Skrót treści, małymi literami. To jest nazwa pliku w składnicy.</summary>
    public string Sha256 { get; private set; }

    /// <summary>Nazwa, pod jaką plik przyszedł. Do pokazania, nie do adresowania.</summary>
    public string FileName { get; private set; }

    public long Size { get; private set; }

    public Guid? TaskId { get; private set; }

    public Guid? NoteId { get; private set; }

    public void Rename(string fileName, Hlc stamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        FileName = fileName.Trim();
        Touch(stamp);
    }
}

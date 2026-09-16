using System.Security.Cryptography;
using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Application.Sync;
using Marshal.Domain.Attachments;

namespace Marshal.Application.UseCases;

/// <summary>
/// Załączniki: dodawanie, otwieranie, dosyłanie treści (spec 9.2).
/// </summary>
/// <remarks>
/// Wpis o załączniku wędruje dziennikiem zmian, treść osobną drogą. Skutek jest taki,
/// że na drugim urządzeniu wpis potrafi być **przed** plikiem — i to jest stan normalny,
/// nie awaria. Załącznik bez treści pokazuje się jako „jeszcze się nie ściągnął",
/// a nie znika i nie wyrzuca błędu.
/// </remarks>
public sealed class AttachmentService(
    IAttachmentRepository attachments,
    IFileTransport files,
    IUnitOfWork unitOfWork,
    IClock clock,
    IHlcSource hlc)
{
    /// <summary>
    /// Dodaje plik: liczy skrót, zapisuje treść, tworzy wpis.
    /// </summary>
    /// <remarks>
    /// Ten sam plik dodany dwa razy zajmuje jedno miejsce, bo adresem jest skrót treści.
    /// Wpisy zostają dwa — bo „podpięłam to tutaj" i „podpięłam to tam" są dwiema
    /// różnymi decyzjami.
    /// </remarks>
    public async Task<Attachment> AddAsync(
        Stream content,
        string fileName,
        Guid? taskId = null,
        Guid? noteId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var (skrot, bufor) = await HashAsync(content, ct);

        bufor.Position = 0;
        await files.PutAsync(skrot, bufor, ct);

        var zalacznik = new Attachment(
            Guid.CreateVersion7(), clock.Now, hlc.Next(),
            skrot, Path.GetFileName(fileName), bufor.Length, taskId, noteId);

        attachments.Add(zalacznik);
        await unitOfWork.SaveChangesAsync(ct);

        return zalacznik;
    }

    public Task<IReadOnlyList<Attachment>> ForTaskAsync(Guid taskId, CancellationToken ct = default) =>
        attachments.ForTaskAsync(taskId, ct);

    public Task<IReadOnlyList<Attachment>> ForNoteAsync(Guid noteId, CancellationToken ct = default) =>
        attachments.ForNoteAsync(noteId, ct);

    /// <summary>Treść załącznika albo <c>null</c>, gdy jeszcze nie dotarła.</summary>
    public Task<Stream?> OpenAsync(Attachment attachment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return files.OpenAsync(attachment.Sha256, ct);
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        if (await attachments.FindAsync(id, ct) is not { } zalacznik)
        {
            return;
        }

        // Nagrobek na wpisie; treść w składnicy zostaje. Dwa wpisy mogą wskazywać ten
        // sam plik, a liczenie, który był ostatni, wymagałoby przejścia po całej bazie
        // przy każdym usunięciu — za to samo płaci się kilkoma kilobajtami.
        zalacznik.MarkDeleted(hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Skrót treści razem z jej kopią w pamięci.
    /// </summary>
    /// <remarks>
    /// Kopia jest konieczna, bo strumień z pliku bywa jednokierunkowy: policzenie skrótu
    /// przewija go do końca i drugi raz już się go nie przeczyta. Załączniki to zdjęcia
    /// i dokumenty, więc rozmiar mieści się w pamięci bez zastanowienia.
    /// </remarks>
    private static async Task<(string Hash, MemoryStream Content)> HashAsync(
        Stream content, CancellationToken ct)
    {
        var bufor = new MemoryStream();
        await content.CopyToAsync(bufor, ct);
        bufor.Position = 0;

        var skrot = await SHA256.HashDataAsync(bufor, ct);

        return (Convert.ToHexString(skrot).ToLowerInvariant(), bufor);
    }
}

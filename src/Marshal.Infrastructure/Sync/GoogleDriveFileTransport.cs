using Marshal.Application.Sync;
using Marshal.Infrastructure.Sync.Google;

namespace Marshal.Infrastructure.Sync;

/// <summary>
/// Treść załączników na Dysku Google, w tym samym katalogu co porcje dziennika.
/// </summary>
/// <remarks>
/// <para>
/// Nazwą pliku jest sam skrót — bez rozszerzenia i bez nazwy, pod jaką plik przyszedł.
/// Nazwa do pokazania siedzi we wpisie i wędruje dziennikiem; tu liczy się wyłącznie
/// tożsamość treści.
/// </para>
/// <para>
/// <b>Nie sprawdzone na żywym koncie</b> — jak reszta drogi przez Dysk.
/// </para>
/// </remarks>
public sealed class GoogleDriveFileTransport(IDriveClient drive, string folderName = "Marshal")
    : IFileTransport
{
    private string? _folderId;

    public async Task<bool> ExistsAsync(string sha256, CancellationToken ct = default)
    {
        Validate(sha256);
        return await FindAsync(sha256, ct) is not null;
    }

    public async Task PutAsync(string sha256, Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        Validate(sha256);

        // Plik już jest — a skoro adresem jest skrót treści, to jest ta sama treść.
        if (await FindAsync(sha256, ct) is not null)
        {
            return;
        }

        using var czytnik = new StreamReader(content);
        await drive.CreateAsync(
            await FolderAsync(ct), sha256, await czytnik.ReadToEndAsync(ct), ct);
    }

    public async Task<Stream?> OpenAsync(string sha256, CancellationToken ct = default)
    {
        Validate(sha256);

        if (await FindAsync(sha256, ct) is not { } plik)
        {
            return null;
        }

        var tresc = await drive.DownloadAsync(plik.Id, ct);
        return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(tresc));
    }

    private async Task<DriveFile?> FindAsync(string sha256, CancellationToken ct) =>
        (await drive.ListAsync(await FolderAsync(ct), ct))
        .FirstOrDefault(f => f.Name == sha256);

    private async Task<string> FolderAsync(CancellationToken ct) =>
        _folderId ??= await drive.EnsureFolderAsync(folderName, ct);

    private static void Validate(string sha256)
    {
        if (sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException($"'{sha256}' nie jest skrótem SHA-256.", nameof(sha256));
        }
    }
}

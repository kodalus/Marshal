using Marshal.Application.Sync;

namespace Marshal.Infrastructure.Sync.Google;

/// <summary>
/// Składnica porcji na Dysku Google (spec 9.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Płasko, bez katalogów na urządzenie.</b> Nazwy na Dysku nie są unikalne —
/// dwa urządzenia zakładające jednocześnie katalog „telefon" dostaną dwa różne
/// katalogi o tej samej nazwie i każde będzie pisać do swojego. Identyfikator
/// urządzenia siedzi więc w nazwie pliku: <c>{urządzenie}.{porcja}.jsonl</c>.
/// Oba człony są sprawdzone przy zapisie, więc kropka jest bezpiecznym
/// rozdzielnikiem.
/// </para>
/// <para>
/// <b>Uprawnienie <c>drive.file</c> wystarcza.</b> Daje dostęp wyłącznie do plików
/// założonych przez aplikację — ale „aplikacja" to identyfikator klienta OAuth, nie
/// instalacja. Drugie urządzenie z tym samym identyfikatorem klienta i tym samym
/// kontem widzi porcje pierwszego. To jedyne uprawnienie Dysku, które nie wymaga
/// przeglądu Google, więc aplikacja nie wpada w limit stu użytkowników testowych.
/// </para>
/// </remarks>
public sealed class GoogleDriveTransport(IDriveClient drive, string folderName = "Marshal")
    : ISyncTransport
{
    private const string Extension = ".jsonl";

    private string? _folderId;

    public async Task<IReadOnlyList<LogSegment>> ListSegmentsAsync(CancellationToken ct = default)
    {
        var files = await drive.ListAsync(await FolderAsync(ct), ct);

        return files
            .Select(f => Parse(f.Name))
            .OfType<LogSegment>()
            // Ponowiony zapis po zerwaniu połączenia potrafi zostawić dwa pliki o tej
            // samej nazwie — Dysk na to pozwala. Treść obu jest ta sama, więc bierzemy
            // pierwszy; czytanie obu byłoby nieszkodliwe, ale kursor i tak minąłby drugi.
            .DistinctBy(s => (s.DeviceId, s.Name))
            .OrderBy(s => s.DeviceId, StringComparer.Ordinal)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<string> ReadSegmentAsync(LogSegment segment, CancellationToken ct = default)
    {
        var folder = await FolderAsync(ct);
        var name = NameOf(segment.DeviceId, segment.Name);

        var file = (await drive.ListAsync(folder, ct))
            .FirstOrDefault(f => f.Name == name);

        return file is null ? string.Empty : await drive.DownloadAsync(file.Id, ct);
    }

    public async Task WriteSegmentAsync(
        string deviceId, string name, string content, CancellationToken ct = default)
    {
        var fileName = NameOf(deviceId, name);
        var folder = await FolderAsync(ct);

        // Dysk nie ma „utwórz, jeśli nie istnieje", więc sprawdzenie i zapis nie są
        // jedną operacją. To nie szkodzi: nazwy porcji nadaje wyłącznie to urządzenie,
        // więc nikt inny nie może wejść w tę nazwę pomiędzy jednym a drugim.
        if ((await drive.ListAsync(folder, ct)).Any(f => f.Name == fileName))
        {
            throw new InvalidOperationException(
                $"Porcja '{name}' urządzenia '{deviceId}' już istnieje.");
        }

        await drive.CreateAsync(folder, fileName, content, ct);
    }

    private async Task<string> FolderAsync(CancellationToken ct) =>
        _folderId ??= await drive.EnsureFolderAsync(folderName, ct);

    private static string NameOf(string deviceId, string segment)
    {
        Validate(deviceId, nameof(deviceId));
        Validate(segment, nameof(segment));

        return $"{deviceId}.{segment}{Extension}";
    }

    /// <summary>Nazwa nie do rozebrania na człony nie jest porcją — pomijamy ją.</summary>
    private static LogSegment? Parse(string fileName)
    {
        if (!fileName.EndsWith(Extension, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = fileName[..^Extension.Length].Split('.');

        if (parts.Length != 2 || !Allowed(parts[0]) || !Allowed(parts[1]))
        {
            return null;
        }

        return new LogSegment(parts[0], parts[1]);
    }

    private static bool Allowed(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static void Validate(string value, string paramName)
    {
        if (!Allowed(value))
        {
            throw new ArgumentException(
                $"Wartość '{value}' zawiera znak niedozwolony w nazwie porcji.", paramName);
        }
    }
}

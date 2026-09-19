using System.Text;
using Google.Apis.Drive.v3;
using Google.Apis.Upload;
using GoogleFile = Google.Apis.Drive.v3.Data.File;

namespace Marshal.Infrastructure.Sync.Google;

/// <summary>
/// <see cref="IDriveClient"/> na prawdziwym API Dysku Google.
/// </summary>
/// <remarks>
/// <b>Nie sprawdzone na żywym koncie.</b> Cała logika składnicy siedzi po drugiej
/// stronie szwu i jest pokryta testami; tutaj zostaje samo wołanie API, które można
/// potwierdzić dopiero po podpięciu poświadczeń (patrz <c>docs/google-dysk.md</c>).
/// </remarks>
public sealed class GoogleDriveClient(DriveService service) : IDriveClient
{
    private const string FolderMime = "application/vnd.google-apps.folder";

    public async Task<string> EnsureFolderAsync(string name, CancellationToken ct = default)
    {
        var szukaj = service.Files.List();
        szukaj.Q = $"mimeType = '{FolderMime}' and name = '{Escape(name)}' and trashed = false";
        szukaj.Fields = "files(id, name)";
        szukaj.PageSize = 10;

        var znalezione = (await szukaj.ExecuteAsync(ct)).Files ?? [];

        // Dysk pozwala na dwa katalogi o tej samej nazwie, więc przy jednoczesnym
        // założeniu na dwóch urządzeniach mogą powstać dwa. Wybór najmniejszego
        // identyfikatora jest arbitralny, ale **jednakowy na wszystkich urządzeniach**,
        // więc rozjazd sam się schodzi po jednej synchronizacji.
        var selected = znalezione
            .Select(f => f.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .FirstOrDefault();

        if (selected is not null)
        {
            return selected;
        }

        var zaloz = service.Files.Create(new GoogleFile { Name = name, MimeType = FolderMime });
        zaloz.Fields = "id";

        return (await zaloz.ExecuteAsync(ct)).Id;
    }

    public async Task<IReadOnlyList<DriveFile>> ListAsync(
        string folderId, CancellationToken ct = default)
    {
        var result = new List<DriveFile>();
        string? strona = null;

        do
        {
            var zapytanie = service.Files.List();
            zapytanie.Q = $"'{Escape(folderId)}' in parents and trashed = false";
            zapytanie.Fields = "nextPageToken, files(id, name)";
            zapytanie.PageSize = 1000;
            zapytanie.PageToken = strona;

            var odpowiedz = await zapytanie.ExecuteAsync(ct);
            result.AddRange((odpowiedz.Files ?? []).Select(f => new DriveFile(f.Id, f.Name)));
            strona = odpowiedz.NextPageToken;
        }
        while (!string.IsNullOrEmpty(strona));

        return result;
    }

    public async Task<string> DownloadAsync(string fileId, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await service.Files.Get(fileId).DownloadAsync(buffer, ct);

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public async Task CreateAsync(
        string folderId, string name, string content, CancellationToken ct = default)
    {
        var opis = new GoogleFile { Name = name, Parents = [folderId] };
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var wyslij = service.Files.Create(opis, content, "application/json");
        wyslij.Fields = "id";

        var postep = await wyslij.UploadAsync(ct);

        // Wysyłka zwraca stan zamiast rzucać. Bez tego sprawdzenia urwane połączenie
        // wyglądałoby na zapisaną porcję i dziennik urwałby się bez śladu.
        if (postep.Status != UploadStatus.Completed)
        {
            throw postep.Exception
                ?? new InvalidOperationException($"Nie udało się wysłać '{name}' na Dysk.");
        }
    }

    /// <summary>
    /// W zapytaniach Dysku wartości stoją w apostrofach, więc apostrof i ukośnik
    /// wsteczny trzeba poprzedzić ukośnikiem.
    /// </summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
}

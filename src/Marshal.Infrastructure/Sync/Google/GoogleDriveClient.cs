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
        var find = service.Files.List();
        find.Q = $"mimeType = '{FolderMime}' and name = '{Escape(name)}' and trashed = false";
        find.Fields = "files(id, name)";
        find.PageSize = 10;

        var found = (await find.ExecuteAsync(ct)).Files ?? [];

        // Dysk pozwala na dwa katalogi o tej samej nazwie, więc przy jednoczesnym
        // założeniu na dwóch urządzeniach mogą powstać dwa. Wybór najmniejszego
        // identyfikatora jest arbitralny, ale **jednakowy na wszystkich urządzeniach**,
        // więc rozjazd sam się schodzi po jednej synchronizacji.
        var selected = found
            .Select(f => f.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .FirstOrDefault();

        if (selected is not null)
        {
            return selected;
        }

        var request = service.Files.Create(new GoogleFile { Name = name, MimeType = FolderMime });
        request.Fields = "id";

        return (await request.ExecuteAsync(ct)).Id;
    }

    public async Task<IReadOnlyList<DriveFile>> ListAsync(
        string folderId, CancellationToken ct = default)
    {
        var result = new List<DriveFile>();
        string? page = null;

        do
        {
            var query = service.Files.List();
            query.Q = $"'{Escape(folderId)}' in parents and trashed = false";
            query.Fields = "nextPageToken, files(id, name)";
            query.PageSize = 1000;
            query.PageToken = page;

            var response = await query.ExecuteAsync(ct);
            result.AddRange((response.Files ?? []).Select(f => new DriveFile(f.Id, f.Name)));
            page = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(page));

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
        var description = new GoogleFile { Name = name, Parents = [folderId] };
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var send = service.Files.Create(description, content, "application/json");
        send.Fields = "id";

        var postep = await send.UploadAsync(ct);

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

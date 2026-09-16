namespace Marshal.Infrastructure.Sync.Google;

/// <summary>Plik widziany na Dysku: tylko to, czego potrzebuje składnica.</summary>
public sealed record DriveFile(string Id, string Name);

/// <summary>
/// Cienka warstwa nad Dyskiem Google — cztery operacje, które składnica naprawdę robi.
/// </summary>
/// <remarks>
/// <para>
/// Ten szew istnieje po to, żeby <see cref="GoogleDriveTransport"/> dało się przetestować
/// bez konta i bez sieci. Cała logika — nazewnictwo porcji, porządek, odsiewanie
/// duplikatów — siedzi w składnicy i jest sprawdzona testami. Po drugiej stronie szwu
/// zostaje samo wołanie API, którego i tak nie da się sprawdzić inaczej niż na żywo.
/// </para>
/// <para>
/// Celowo nie ma tu operacji usunięcia ani podmiany. Dziennik jest przyrostowy i
/// niezmienny (spec 9.2); brak tych metod sprawia, że nie da się tego złamać przez
/// nieuwagę.
/// </para>
/// </remarks>
public interface IDriveClient
{
    /// <summary>Identyfikator katalogu roboczego. Zakłada go, jeśli jeszcze nie ma.</summary>
    Task<string> EnsureFolderAsync(string name, CancellationToken ct = default);

    /// <summary>Pliki w katalogu, bez tych wyrzuconych do kosza.</summary>
    Task<IReadOnlyList<DriveFile>> ListAsync(string folderId, CancellationToken ct = default);

    Task<string> DownloadAsync(string fileId, CancellationToken ct = default);

    Task CreateAsync(string folderId, string name, string content, CancellationToken ct = default);
}

using Marshal.Application.Sync;

namespace Marshal.Infrastructure.Sync;

/// <summary>Treść załączników w katalogu <c>files/</c> obok dziennika.</summary>
public sealed class LocalFolderFileTransport(string root) : IFileTransport
{
    private string Folder => Path.Combine(root, "files");

    public Task<bool> ExistsAsync(string sha256, CancellationToken ct = default) =>
        Task.FromResult(File.Exists(PathFor(sha256)));

    public async Task PutAsync(string sha256, Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var path = PathFor(sha256);

        // Plik już jest — a skoro adresem jest skrót treści, to jest dokładnie ta sama
        // treść. Nadpisywanie byłoby pracą bez żadnego skutku.
        if (File.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(Folder);

        // Zapis do pliku tymczasowego i przeniesienie: przerwane wgranie zostawia
        // plik tymczasowy, a nie połowę zdjęcia pod adresem, który obiecuje całość.
        var temp = path + ".tmp";
        await using (var docelowy = File.Create(temp))
        {
            await content.CopyToAsync(docelowy, ct);
        }

        File.Move(temp, path, overwrite: false);
    }

    public Task<Stream?> OpenAsync(string sha256, CancellationToken ct = default)
    {
        var path = PathFor(sha256);

        return Task.FromResult<Stream?>(
            File.Exists(path) ? File.OpenRead(path) : null);
    }

    /// <summary>
    /// Skrót trafia do nazwy pliku, więc musi być tym, na co wygląda: sześćdziesiąt
    /// cztery znaki szesnastkowe i nic więcej.
    /// </summary>
    private string PathFor(string sha256)
    {
        if (sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException($"'{sha256}' nie jest skrótem SHA-256.", nameof(sha256));
        }

        return Path.Combine(Folder, sha256);
    }
}

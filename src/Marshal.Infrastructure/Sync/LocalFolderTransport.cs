using System.Text;
using Marshal.Application.Sync;

namespace Marshal.Infrastructure.Sync;

/// <summary>
/// Składnica w katalogu na dysku: <c>log/{urządzenie}/{porcja}.jsonl</c>.
/// </summary>
/// <remarks>
/// Pełnoprawna droga synchronizacji przez katalog Dropboksa, OneDrive albo
/// Syncthinga — i jednocześnie to, na czym da się przetestować scalanie bez sieci.
/// </remarks>
public sealed class LocalFolderTransport(string root) : ISyncTransport
{
    private const string Extension = ".jsonl";

    private string LogFolder => Path.Combine(root, "log");

    public Task<IReadOnlyList<LogSegment>> ListSegmentsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(LogFolder))
        {
            return Task.FromResult<IReadOnlyList<LogSegment>>([]);
        }

        var segments = Directory.EnumerateDirectories(LogFolder)
            .SelectMany(folder => Directory
                .EnumerateFiles(folder, "*" + Extension)
                // Dopasowanie po masce potrafi złapać dłuższe rozszerzenie, a obok
                // porcji leżą pliki tymczasowe przerwanego zapisu.
                .Where(file => Path.GetExtension(file) == Extension)
                .Select(file => new LogSegment(
                    Path.GetFileName(folder), Path.GetFileNameWithoutExtension(file))))
            .OrderBy(s => s.DeviceId, StringComparer.Ordinal)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult<IReadOnlyList<LogSegment>>(segments);
    }

    public async Task<string> ReadSegmentAsync(LogSegment segment, CancellationToken ct = default)
    {
        var path = PathFor(segment.DeviceId, segment.Name);

        return File.Exists(path)
            ? await File.ReadAllTextAsync(path, Encoding.UTF8, ct)
            : string.Empty;
    }

    public async Task WriteSegmentAsync(
        string deviceId, string name, string content, CancellationToken ct = default)
    {
        var path = PathFor(deviceId, name);

        // Nadpisanie istniejącej porcji odebrałoby jej niezmienność, na której stoi
        // cały kursor: „przeczytana zostaje przeczytana". Lepiej głośno zawieść tutaj
        // niż po cichu odciąć drugiemu urządzeniu kawałek historii.
        if (File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Porcja '{name}' urządzenia '{deviceId}' już istnieje.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Zapis do pliku tymczasowego i przeniesienie: przerwanie w trakcie zostawia
        // plik tymczasowy, a nie porcję widoczną dla innych i uciętą w pół wiersza.
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), ct);
        File.Move(temp, path, overwrite: false);
    }

    private string PathFor(string deviceId, string name)
    {
        Validate(deviceId, nameof(deviceId));
        Validate(name, nameof(name));

        return Path.Combine(LogFolder, deviceId, name + Extension);
    }

    /// <summary>
    /// Identyfikator urządzenia i nazwa porcji trafiają do ścieżki, więc nie mogą
    /// zawierać niczego, co wyprowadza poza katalog ani co psuje nazwę na innym
    /// systemie plików.
    /// </summary>
    private static void Validate(string value, string paramName)
    {
        if (value.Length == 0 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
        {
            throw new ArgumentException(
                $"Wartość '{value}' zawiera znak niedozwolony w nazwie pliku.", paramName);
        }
    }
}

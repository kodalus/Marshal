using System.Text;
using Marshal.Application.Sync;

namespace Marshal.Infrastructure.Sync;

/// <summary>
/// Składnica w katalogu na dysku. Pełnoprawna droga synchronizacji przez katalog
/// Dropboksa, OneDrive albo Syncthinga — i jednocześnie to, na czym da się
/// przetestować scalanie bez sieci.
/// </summary>
public sealed class LocalFolderTransport(string root) : ISyncTransport
{
    private const string Extension = ".jsonl";

    private string LogFolder => Path.Combine(root, "log");

    public Task<IReadOnlyList<RemoteLog>> ListLogsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(LogFolder))
        {
            return Task.FromResult<IReadOnlyList<RemoteLog>>([]);
        }

        var logs = Directory.EnumerateFiles(LogFolder, "*" + Extension)
            .Select(path => new RemoteLog(Path.GetFileNameWithoutExtension(path), new FileInfo(path).Length))
            .OrderBy(l => l.DeviceId, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult<IReadOnlyList<RemoteLog>>(logs);
    }

    public async Task<string> ReadFromAsync(string deviceId, long offset, CancellationToken ct = default)
    {
        var path = PathFor(deviceId);

        if (!File.Exists(path))
        {
            return string.Empty;
        }

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        if (offset >= stream.Length)
        {
            return string.Empty;
        }

        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    public async Task AppendAsync(string deviceId, string content, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        Directory.CreateDirectory(LogFolder);

        await using var stream = new FileStream(
            PathFor(deviceId), FileMode.Append, FileAccess.Write, FileShare.Read);

        var bytes = Encoding.UTF8.GetBytes(content);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private string PathFor(string deviceId)
    {
        // Identyfikator urządzenia trafia do nazwy pliku, więc nie może zawierać
        // niczego, co wyprowadza poza katalog ani co psuje nazwę na innym systemie.
        if (deviceId.Length == 0 || deviceId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
        {
            throw new ArgumentException(
                $"Identyfikator urządzenia '{deviceId}' zawiera znak niedozwolony w nazwie pliku.",
                nameof(deviceId));
        }

        return Path.Combine(LogFolder, deviceId + Extension);
    }
}

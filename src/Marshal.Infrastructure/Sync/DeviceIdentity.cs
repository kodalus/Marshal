using System.Security.Cryptography;
using Marshal.Application.Abstractions;
using Marshal.Domain.Sync;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Sync;

/// <summary>
/// Trwały identyfikator tego urządzenia, zapisany przy pierwszym uruchomieniu.
/// </summary>
/// <remarks>
/// <para>
/// Nazwa maszyny się do tego nie nadaje, choć wygląda kusząco. Na Androidzie
/// <see cref="Environment.MachineName"/> zwraca „localhost" na każdym urządzeniu,
/// a dwa urządzenia o tym samym identyfikatorze psują trzy rzeczy naraz: rozstrzyganie
/// remisów zegara logicznego przestaje być jednoznaczne, nazwy porcji w składnicy
/// wchodzą sobie w drogę, a każde z urządzeń uznaje dziennik drugiego za własny
/// i przestaje go czytać. Żadna z tych rzeczy nie rzuca wyjątku — po prostu część
/// zmian nigdy nie dociera.
/// </para>
/// <para>
/// Losowy człon zapewnia jednoznaczność, a człon czytelny bierze się z nazwy maszyny
/// wyłącznie po to, żeby w składnicy dało się poznać, z czego jest który plik.
/// </para>
/// </remarks>
public sealed class DeviceIdentity(MarshalDbContext db) : IDeviceIdentity
{
    private string? _id;

    public string Id => _id ??= Resolve();

    private string Resolve()
    {
        var saved = db.LocalSettings
            .AsNoTracking()
            .FirstOrDefault(s => s.Key == LocalSetting.DeviceIdKey);

        if (saved is not null)
        {
            return saved.Value;
        }

        var created = Generate(Environment.MachineName);
        db.LocalSettings.Add(new LocalSetting(LocalSetting.DeviceIdKey, created));
        db.SaveChanges();

        return created;
    }

    /// <summary>
    /// Wynik trafia do nazw plików w składnicy, więc same małe litery, cyfry i myślnik
    /// — to samo, czego pilnują obie implementacje składnicy.
    /// </summary>
    internal static string Generate(string machineName)
    {
        var readable = new string(machineName
            .Where(char.IsAsciiLetterOrDigit)
            .Take(12)
            .ToArray())
            .ToLowerInvariant();

        // Naprawdę losowe, nie z zegara. Identyfikator siódmej wersji zaczyna się od
        // 48-bitowego znacznika czasu w milisekundach, więc jego pierwsze dwanaście
        // znaków szesnastkowych **to jest ten znacznik** — dwa urządzenia zakładane
        // w tej samej milisekundzie dostałyby ten sam człon. Test to wychwycił.
        var random = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

        return readable.Length == 0 ? random : $"{readable}-{random}";
    }
}

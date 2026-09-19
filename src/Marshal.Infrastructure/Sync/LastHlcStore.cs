using Marshal.Domain.Primitives;
using Marshal.Domain.Sync;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Sync;

/// <summary>
/// Ostatni znacznik wydany przez zegar logiczny tego urządzenia, trwale.
/// </summary>
/// <remarks>
/// <para>
/// Bez tego zegar po każdym uruchomieniu startuje od zera i opiera się wyłącznie
/// na zegarze ściennym. Wystarczy, że ten cofnie się między uruchomieniami —
/// poprawka z serwera czasu, zmiana strefy, rozładowana bateria podtrzymania na
/// Androidzie — a nowe zmiany dostaną znaczniki **wcześniejsze** od tych już
/// wysłanych i przepadną przy scalaniu. Bez wyjątku, bez śladu: po prostu druga
/// strona uzna je za starsze.
/// </para>
/// <para>
/// Zapis idzie w tej samej transakcji co zmiana, która ten znacznik zużyła, więc
/// nie da się mieć wysłanej zmiany bez zapisanego znacznika.
/// </para>
/// </remarks>
internal static class LastHlcStore
{
    public static Hlc? Read(DbContext db, string deviceId)
    {
        var saved = db.Set<LocalSetting>()
            .AsNoTracking()
            .FirstOrDefault(s => s.Key == LocalSetting.LastHlcKey);

        if (saved is null || !Hlc.TryParse(saved.Value, out var stamp))
        {
            return null;
        }

        // Znacznik innego urządzenia oznacza przeniesioną bazę. Wznowienie z niego
        // byłoby błędem — zegar jest zegarem tego urządzenia i tylko jego.
        return stamp.DeviceId == deviceId ? stamp : null;
    }

    /// <summary>Dopisuje wartość do kontekstu; zapis do bazy robi wołający.</summary>
    public static void Stage(DbContext db, Hlc stamp)
    {
        var text = stamp.ToString();

        var existing = db.ChangeTracker.Entries<LocalSetting>()
                .Select(e => e.Entity)
                .FirstOrDefault(s => s.Key == LocalSetting.LastHlcKey)
            ?? db.Set<LocalSetting>().FirstOrDefault(s => s.Key == LocalSetting.LastHlcKey);

        if (existing is null)
        {
            db.Add(new LocalSetting(LocalSetting.LastHlcKey, text));
        }
        else if (string.CompareOrdinal(text, existing.Value) > 0)
        {
            // Nigdy w tył: znacznik zapisany jest górną granicą tego, co wydaliśmy.
            existing.Set(text);
        }
    }
}

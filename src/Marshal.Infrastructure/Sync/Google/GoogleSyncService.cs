using Marshal.Application.Abstractions;
using Marshal.Infrastructure.Data;

namespace Marshal.Infrastructure.Sync.Google;

/// <summary>Co się stało przy próbie synchronizacji — do pokazania jednym zdaniem.</summary>
public sealed record SyncOutcome(bool Ok, string Message, int Sent = 0, int Applied = 0);

/// <summary>
/// Synchronizacja przez Dysk Google: poświadczenia z ustawień, logowanie, przebieg (spec 9).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ręcznie, nie w tle.</b> Pierwsze przebiegi na żywym koncie mają być wywołane
/// świadomie i mieć widoczny wynik — synchronizacja uruchamiana po cichu przy starcie
/// znaczy, że pierwszy błąd zobaczysz jako brakujące zadania, a nie jako komunikat.
/// Automat dochodzi dopiero wtedy, gdy wiadomo, że droga działa.
/// </para>
/// <para>
/// Połączenie zakładane na czas przebiegu i zamykane po nim. Żeton odświeżalny leży
/// w danych aplikacji, więc przeglądarka otwiera się **tylko za pierwszym razem**;
/// trzymanie otwartego połączenia między przebiegami nic nie oszczędza, a dokłada
/// stan, który trzeba by unieważniać po zmianie poświadczeń.
/// </para>
/// </remarks>
public sealed class GoogleSyncService(
    MarshalDbContext db,
    ISettings settings,
    IHlcSource hlc,
    IDeviceIdentity device,
    string databasePath)
{
    /// <summary>Czy w ogóle jest czym się logować.</summary>
    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(settings.GoogleClientId)
        && !string.IsNullOrWhiteSpace(settings.GoogleClientSecret);

    /// <summary>
    /// Katalog na żeton odświeżalny — w danych aplikacji, nie na Dysku i nie obok
    /// dziennika. Żeton jest tajemnicą konta, a dziennik jedzie do chmury.
    /// </summary>
    public string TokenFolder => Path.Combine(Path.GetDirectoryName(databasePath)!, "google");

    public async Task<SyncOutcome> SyncAsync(CancellationToken ct = default)
    {
        if (!HasCredentials)
        {
            return new SyncOutcome(false, "Najpierw wpisz identyfikator klienta i tajemnicę.");
        }

        try
        {
            using var polaczenie = await GoogleDriveFactory.ConnectAsync(
                settings.GoogleClientId!,
                settings.GoogleClientSecret!,
                TokenFolder,
                ct);

            var silnik = new SyncEngine(db, polaczenie.Transport, hlc, device.Id);
            var raport = await silnik.SyncAsync(ct);

            return new SyncOutcome(
                true,
                $"Wysłane {raport.Sent}, przyjęte {raport.Applied}.",
                raport.Sent,
                raport.Applied);
        }
        catch (OperationCanceledException)
        {
            return new SyncOutcome(false, "Przerwane.");
        }
        catch (Exception e)
        {
            // Treść wyjątku, nie „nie udało się". Przy pierwszym logowaniu prawie każdy
            // błąd jest do naprawienia w konsoli Google — zły identyfikator, adres
            // powrotu, brak konta na liście testowej — ale tylko wtedy, gdy widać który.
            return new SyncOutcome(false, e.Message);
        }
    }
}

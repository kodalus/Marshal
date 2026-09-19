using Google.Apis.Auth.OAuth2.Responses;
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
/// <b>Sama, ale z bramą.</b> Przebieg rusza teraz również bez kliknięcia — chwilę po
/// zmianie i co kilka minut przy otwartej aplikacji. Dlatego cała jego praca na bazie
/// idzie przez <see cref="IKolejkaBazy"/>: kontekst bazy jest w tej aplikacji jeden
/// na proces, a dwie rzeczy naraz na jednym kontekście to nie rzadki pech, tylko
/// awaria na żądanie. Wynik ręcznego przebiegu nadal widać w oknie; automat milczy,
/// dopóki się udaje.
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
    string databasePath,
    IDbQueue? queue = null)
{
    // Brama na bazę. Domyślnie wprost, żeby testy i wywołania ręczne nie musiały
    // jej podawać — ale w złożonej aplikacji jest zawsze ta jedna, wspólna z oknem.
    private readonly IDbQueue _kolejka = queue ?? new KolejkaWprost();

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
            return await PrzebiegAsync(ct);
        }
        catch (TokenResponseException e) when (e.Error?.Error == "invalid_grant")
        {
            // Żeton przestał być ważny. Przy aplikacji w trybie testowym Google wydaje
            // żeton odświeżalny **na siedem dni** — niezależnie od zakresu — więc to
            // nie jest awaria, tylko tydzień, który minął. Zapisany żeton jest wtedy
            // bezużyteczny i jedynym wyjściem jest zapytać o zgodę jeszcze raz;
            // bez tego synchronizacja przestawałaby działać co tydzień, zostawiając
            // komunikat, z którego nic nie wynika.
            try
            {
                if (Directory.Exists(TokenFolder))
                {
                    Directory.Delete(TokenFolder, recursive: true);
                }

                return await PrzebiegAsync(ct);
            }
            catch (Exception ponownie) when (ponownie is not OperationCanceledException)
            {
                return new SyncOutcome(false, ponownie.Message);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Treść wyjątku, nie „nie udało się". Przy pierwszym logowaniu prawie każdy
            // błąd jest do naprawienia w konsoli Google — zły identyfikator, adres
            // powrotu, brak konta na liście testowej — ale tylko wtedy, gdy widać który.
            return new SyncOutcome(false, e.Message);
        }
    }

    private async Task<SyncOutcome> PrzebiegAsync(CancellationToken ct)
    {
        using var polaczenie = await GoogleDriveFactory.ConnectAsync(
            settings.GoogleClientId!,
            settings.GoogleClientSecret!,
            TokenFolder,
            settings.GoogleCalendarEnabled,
            ct);

        var silnik = new SyncEngine(db, polaczenie.Transport, hlc, device.Id, _kolejka);
        var raport = await silnik.SyncAsync(ct);

        return new SyncOutcome(
            true,
            $"Wysłane {raport.Sent}, przyjęte {raport.Applied}.",
            raport.Sent,
            raport.Applied);
    }
}

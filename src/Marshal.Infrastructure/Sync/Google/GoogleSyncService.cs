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
/// idzie przez <see cref="IDbQueue"/>: kontekst bazy jest w tej aplikacji jeden
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
    private readonly IDbQueue _queue = queue ?? new DirectQueue();

    /// <summary>Czy w ogóle jest czym się logować.</summary>
    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(settings.GoogleClientId)
        && !string.IsNullOrWhiteSpace(settings.GoogleClientSecret);

    /// <summary>
    /// Katalog na żeton odświeżalny — w danych aplikacji, nie na Dysku i nie obok
    /// dziennika. Żeton jest tajemnicą konta, a dziennik jedzie do chmury.
    /// </summary>
    public string TokenFolder => Path.Combine(Path.GetDirectoryName(databasePath)!, "google");

    /// <summary>Czy w katalogu żetonu naprawdę coś leży.</summary>
    /// <remarks>
    /// <para>
    /// <b>Istnienie katalogu nie znaczy, że jest czym się zalogować.</b> Katalog
    /// zakłada sama składnica żetonu przy pierwszym sięgnięciu — a sięga po nią także
    /// kalendarz, własną drogą. Wystarczy więc jedna próba wysłania zadania do
    /// kalendarza, żeby po wygasłej zgodzie został pusty katalog: żetonu nie ma,
    /// a wszystko, co pytało o sam katalog, odpowiadało „podłączona".
    /// </para>
    /// <para>
    /// Kosztowało to jeden dzień szukania nie tam, gdzie trzeba, i jeden fałszywy wpis
    /// w dzienniku — czyli dokładnie to, przed czym ten wpis miał chronić.
    /// </para>
    /// </remarks>
    public bool HasToken
    {
        get
        {
            try
            {
                return Directory.Exists(TokenFolder)
                    && Directory.EnumerateFiles(TokenFolder).Any();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Nie da się zajrzeć — a to nie jest powód, żeby cokolwiek przewracać.
                // Nieudane logowanie powie o sobie samo i powie więcej.
                return false;
            }
        }
    }

    /// <summary>
    /// Zapomnienie żetonu — następne logowanie pyta o zgodę od nowa.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Potrzebne częściej, niż się wydaje, i dotąd nie było na to drogi z aplikacji.
    /// Żeton wydany, gdy aplikacja stała w konsoli jako testowa, żyje siedem dni —
    /// i zostaje przy tym czasie także po opublikowaniu aplikacji, bo czas życia
    /// ustala się przy wydaniu. Żeby dostać bezterminowy, trzeba poprosić o zgodę
    /// na nowo, a żeby o nią poproszono, nie może być zapisanego żetonu.
    /// </para>
    /// <para>
    /// <b>Na Androidzie nie było jak tego zrobić w ogóle.</b> Ekran ustawień mówił
    /// „skasuj ten katalog" i podawał ścieżkę — prawdziwą, ale leżącą w prywatnych
    /// danych aplikacji, do których telefon nie daje dostępu. Rada wykonalna na
    /// komputerze i niewykonalna tam, gdzie ten sam ekran ją pokazywał.
    /// </para>
    /// </remarks>
    public bool ForgetToken()
    {
        if (!Directory.Exists(TokenFolder))
        {
            return false;
        }

        Directory.Delete(TokenFolder, recursive: true);
        return true;
    }

    public async Task<SyncOutcome> SyncAsync(CancellationToken ct = default)
    {
        if (!HasCredentials)
        {
            return new SyncOutcome(false, "Najpierw wpisz identyfikator klienta i tajemnicę.");
        }

        try
        {
            return await RunAsync(ct);
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

                return await RunAsync(ct);
            }
            catch (Exception again) when (again is not OperationCanceledException)
            {
                return new SyncOutcome(false, again.Message);
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

    private async Task<SyncOutcome> RunAsync(CancellationToken ct)
    {
        using var connection = await GoogleDriveFactory.ConnectAsync(
            settings.GoogleClientId!,
            settings.GoogleClientSecret!,
            TokenFolder,
            settings.GoogleCalendarEnabled,
            ct);

        var engine = new SyncEngine(db, connection.Transport, hlc, device.Id, _queue);
        var report = await engine.SyncAsync(ct);

        return new SyncOutcome(
            true,
            $"Wysłane {report.Sent}, przyjęte {report.Applied}.",
            report.Sent,
            report.Applied);
    }
}

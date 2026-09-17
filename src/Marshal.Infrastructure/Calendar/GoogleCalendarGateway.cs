using Google.Apis.Calendar.v3;
using Google.Apis.Services;
using Marshal.Application.Abstractions;
using Marshal.Application.Calendar;
using Marshal.Domain.Calendar;
using Marshal.Infrastructure.Sync.Google;

namespace Marshal.Infrastructure.Calendar;

/// <summary>
/// Kanał kalendarza Google podłączony przy składaniu zależności, logujący się dopiero
/// przy pierwszym pobraniu.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GoogleCalendarFeed"/> wymaga gotowej usługi Google, a ta powstaje dopiero
/// po zalogowaniu — czyli po tym, jak kontener już wszystko złożył. Bez tego pośrednika
/// kanału Google **nie było w ogóle wśród zarejestrowanych kanałów**: odświeżanie
/// kalendarzy przechodziło po źródłach, nie znajdowało kanału dla rodzaju Google
/// i po cichu je pomijało. Włączenie API w konsoli niczego nie zmieniało, bo aplikacja
/// nigdy nie zadawała pytania.
/// </para>
/// <para>
/// Bez poświadczeń albo bez zgody na kalendarz zwraca pusty wynik zamiast rzucać:
/// brak podłączonego konta to stan normalny, a nie awaria kalendarza.
/// </para>
/// </remarks>
public sealed class GoogleCalendarGateway(ISettings settings, string databasePath) : ICalendarFeed
{
    private GoogleCalendarFeed? _kanal;

    private CalendarService? _usluga;

    public CalendarKind Kind => CalendarKind.Google;

    public async Task<FeedResult> FetchAsync(
        CalendarSource source, string? syncToken, CancellationToken ct = default)
    {
        if (await PolaczAsync(ct) is not { } kanal)
        {
            return new FeedResult([], SyncToken: null, IsFull: false);
        }

        return await kanal.FetchAsync(source, syncToken, ct);
    }

    private async Task<GoogleCalendarFeed?> PolaczAsync(CancellationToken ct)
    {
        if (_kanal is not null)
        {
            return _kanal;
        }

        if (!settings.GoogleCalendarEnabled
            || string.IsNullOrWhiteSpace(settings.GoogleClientId)
            || string.IsNullOrWhiteSpace(settings.GoogleClientSecret))
        {
            return null;
        }

        var poswiadczenie = await GoogleDriveFactory.AuthorizeAsync(
            settings.GoogleClientId!,
            settings.GoogleClientSecret!,
            Path.Combine(Path.GetDirectoryName(databasePath)!, "google"),
            withCalendar: true,
            ct);

        _usluga = new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = poswiadczenie,
            ApplicationName = "Marshal",
        });

        return _kanal = new GoogleCalendarFeed(_usluga);
    }
}

using Marshal.Application.Abstractions;
using Marshal.Domain.Sync;

namespace Marshal.Infrastructure.Data;

/// <summary>
/// Ustawienia urządzenia w tabeli <c>LocalSettings</c> (spec 3.4, 11).
/// </summary>
/// <remarks>
/// Wartości trzymane w pamięci po pierwszym odczycie, bo <see cref="ISettings.Zone"/>
/// woła zegar przy **każdym** pobraniu czasu. Odczyt z bazy przy każdym takim wołaniu
/// byłby zapytaniem w pętli rysowania listy.
/// </remarks>
public sealed class LocalSettings(MarshalDbContext db) : ISettings
{
    public const string ZoneKey = "time-zone";

    public const string ThemeKey = "theme";

    public const string GoogleClientIdKey = "google-client-id";

    public const string GoogleClientSecretKey = "google-client-secret";

    public const string GoogleCalendarKey = "google-calendar";

    /// <summary>
    /// Strefa domyślna, gdy nic nie zapisano (spec 3.4). Wpisana wprost, nie brana
    /// z systemu — żeby świeżo zainstalowana aplikacja liczyła dni tak samo na
    /// telefonie kupionym z inną strefą fabryczną.
    /// </summary>
    public const string DefaultZoneId = "Europe/Warsaw";

    private readonly Lock _gate = new();

    private TimeZoneInfo? _zone;

    private string? _klopotZeStrefa;

    private ThemeChoice? _theme;

    private string? _googleId;

    private string? _googleSecret;

    private bool? _googleCalendar;

    public TimeZoneInfo Zone
    {
        get
        {
            lock (_gate)
            {
                return _zone ??= Resolve(Read(ZoneKey) ?? DefaultZoneId);
            }
        }
    }

    public string? ZoneProblem
    {
        get
        {
            lock (_gate)
            {
                // Sięgnięcie po strefę, bo dopiero ono ją rozstrzyga — inaczej pytanie
                // o kłopot przed pierwszym użyciem zegara zawsze dawałoby „nie ma".
                _ = _zone ??= Resolve(Read(ZoneKey) ?? DefaultZoneId);

                return _klopotZeStrefa;
            }
        }
    }

    public ThemeChoice Theme
    {
        get
        {
            lock (_gate)
            {
                return _theme ??= Enum.TryParse<ThemeChoice>(Read(ThemeKey), out var wybor)
                    ? wybor
                    : ThemeChoice.System;
            }
        }
    }

    public string? GoogleClientId
    {
        get { lock (_gate) { return _googleId ??= Read(GoogleClientIdKey) ?? string.Empty; } }
    }

    public string? GoogleClientSecret
    {
        get { lock (_gate) { return _googleSecret ??= Read(GoogleClientSecretKey) ?? string.Empty; } }
    }

    public bool GoogleCalendarEnabled
    {
        get { lock (_gate) { return _googleCalendar ??= Read(GoogleCalendarKey) == "1"; } }
    }

    public void SetGoogleCalendarEnabled(bool enabled)
    {
        lock (_gate)
        {
            Write(GoogleCalendarKey, enabled ? "1" : "0");
            _googleCalendar = enabled;
        }
    }

    public void SetGoogle(string? clientId, string? clientSecret)
    {
        lock (_gate)
        {
            // Przycinane, bo kopiowanie z konsoli Google wciąga spację albo koniec
            // wiersza, a wtedy logowanie odbija się komunikatem o złym kliencie —
            // i nie ma po nim jak poznać, że chodziło o jeden znak.
            var id = clientId?.Trim() ?? string.Empty;
            var tajemnica = clientSecret?.Trim() ?? string.Empty;

            Write(GoogleClientIdKey, id);
            Write(GoogleClientSecretKey, tajemnica);

            _googleId = id;
            _googleSecret = tajemnica;
        }
    }

    public void SetZone(string id)
    {
        // Wybranie strefy ręcznie kasuje poprzedni kłopot: jeśli nowa się rozstrzyga,
        // ostrzeżenie o starej byłoby już nieprawdą.
        _klopotZeStrefa = null;

        var strefa = Resolve(id);

        lock (_gate)
        {
            Write(ZoneKey, strefa.Id);
            _zone = strefa;
        }
    }

    public void SetTheme(ThemeChoice theme)
    {
        lock (_gate)
        {
            Write(ThemeKey, theme.ToString());
            _theme = theme;
        }
    }

    /// <summary>
    /// Strefa po identyfikatorze, z odwrotem do domyślnej.
    /// </summary>
    /// <remarks>
    /// Identyfikatory stref różnią się między systemami („Europe/Warsaw" kontra
    /// „Central European Standard Time"). Od .NET 8 obie postacie działają na obu
    /// systemach, ale baza przeniesiona z Windowsa na Androida mogłaby nieść nazwę
    /// zapisaną kiedyś inaczej — a nieznana strefa nie może wywrócić startu
    /// aplikacji. Cofamy się wtedy do domyślnej, bo brak czasu jest gorszy niż
    /// czas w niewłaściwej strefie.
    /// </remarks>
    /// <summary>
    /// Strefa po identyfikatorze, z odnotowaniem zastępstwa.
    /// </summary>
    /// <remarks>
    /// Zastępstwo było dotąd ciche i to była najgorsza możliwa cicha awaria w tym
    /// projekcie: przesuwała **wszystkie** godziny naraz, a przesunięte wszystko
    /// wygląda identycznie jak źle pobrane dane. Teraz zostaje ślad, który widać
    /// w ustawieniach i w dzienniku.
    /// </remarks>
    private TimeZoneInfo Resolve(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            if (id != DefaultZoneId)
            {
                var zastepcza = Resolve(DefaultZoneId);

                _klopotZeStrefa =
                    $"Ten system nie zna strefy „{id}”. Godziny liczone są w „{zastepcza.Id}”.";

                return zastepcza;
            }

            _klopotZeStrefa =
                $"Ten system nie zna strefy „{id}”. Godziny liczone są w czasie uniwersalnym, "
                + "czyli o godzinę lub dwie wcześniej niż w Polsce. Wybierz strefę w ustawieniach.";

            return TimeZoneInfo.Utc;
        }
    }

    private string? Read(string key) =>
        db.LocalSettings.FirstOrDefault(s => s.Key == key)?.Value;

    private void Write(string key, string value)
    {
        if (db.LocalSettings.FirstOrDefault(s => s.Key == key) is { } istniejace)
        {
            istniejace.Set(value);
        }
        else
        {
            db.LocalSettings.Add(new LocalSetting(key, value));
        }

        db.SaveChanges();
    }
}

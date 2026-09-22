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

    public const string MainCalendarKey = "main-calendar";

    public const string DailyBackupKey = "daily-backup";

    public const string BackupFolderKey = "backup-folder";

    /// <summary>
    /// Strefa domyślna, gdy nic nie zapisano (spec 3.4). Wpisana wprost, nie brana
    /// z systemu — żeby świeżo zainstalowana aplikacja liczyła dni tak samo na
    /// telefonie kupionym z inną strefą fabryczną.
    /// </summary>
    public const string DefaultZoneId = "Europe/Warsaw";

    private readonly Lock _gate = new();

    private TimeZoneInfo? _zone;

    private string? _zoneTrouble;

    private ThemeChoice? _theme;

    private string? _googleId;

    private string? _googleSecret;

    private bool? _googleCalendar;

    private string? _primaryCalendar;

    private bool? _dailyBackup;

    private string? _backupFolder;

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

                return _zoneTrouble;
            }
        }
    }

    public ThemeChoice Theme
    {
        get
        {
            lock (_gate)
            {
                return _theme ??= Enum.TryParse<ThemeChoice>(Read(ThemeKey), out var choice)
                    ? choice
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

    public Guid? MainCalendarId
    {
        get
        {
            lock (_gate)
            {
                _primaryCalendar ??= Read(MainCalendarKey) ?? string.Empty;

                return Guid.TryParse(_primaryCalendar, out var id) ? id : null;
            }
        }
    }

    public void SetMainCalendar(Guid? calendarId)
    {
        lock (_gate)
        {
            var patch = calendarId?.ToString() ?? string.Empty;
            Write(MainCalendarKey, patch);
            _primaryCalendar = patch;
        }
    }

    public const string CalendarAccountsKey = "calendar-accounts";

    private string? _accounts;

    /// <summary>
    /// Dodatkowe konta Google. Adresy rozdzielone znakiem nowej linii.
    /// </summary>
    /// <remarks>
    /// Nowa linia, nie przecinek: adres pocztowy przecinka nie zawiera, ale rozdzielanie
    /// nim jest tym rodzajem założenia, który przestaje być prawdziwy, gdy ktoś wpisze
    /// coś, czego nie przewidzieliśmy. W adresie nie ma też znaku nowej linii i tego
    /// akurat pilnuje już sam format.
    /// </remarks>
    public IReadOnlyList<string> CalendarAccounts
    {
        get
        {
            lock (_gate)
            {
                _accounts ??= Read(CalendarAccountsKey) ?? string.Empty;

                return _accounts
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            }
        }
    }

    public void AddCalendarAccount(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        lock (_gate)
        {
            var address = email.Trim();

            var now = (Read(CalendarAccountsKey) ?? string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            // Powtórzenie nie jest błędem: ponowne dodanie konta to najczęstsza reakcja
            // na „chyba nie zadziałało" i ma po prostu odświeżyć żeton.
            if (!now.Contains(address, StringComparer.OrdinalIgnoreCase))
            {
                now.Add(address);
            }

            Save(now);
        }
    }

    public void RemoveCalendarAccount(string email)
    {
        lock (_gate)
        {
            var now = (Read(CalendarAccountsKey) ?? string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(k => !string.Equals(k, email?.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

            Save(now);
        }
    }

    private void Save(IEnumerable<string> accounts)
    {
        var patch = string.Join('\n', accounts);
        Write(CalendarAccountsKey, patch);
        _accounts = patch;
    }

    public void SetGoogleCalendarEnabled(bool enabled)
    {
        lock (_gate)
        {
            Write(GoogleCalendarKey, enabled ? "1" : "0");
            _googleCalendar = enabled;
        }
    }

    /// <summary>
    /// Codzienna kopia — **włączona, dopóki nie wyłączona**.
    /// </summary>
    /// <remarks>
    /// Brak wpisu znaczy „nikt o tym nie decydował", a domyślną odpowiedzią na to
    /// pytanie jest „tak": kopia, którą trzeba było włączyć, nie chroni nikogo.
    /// Świeżo zainstalowana aplikacja robi więc kopię od pierwszego dnia.
    /// </remarks>
    public bool DailyBackup
    {
        get
        {
            lock (_gate)
            {
                return _dailyBackup ??= Read(DailyBackupKey) != "0";
            }
        }
    }

    public void SetDailyBackup(bool on)
    {
        lock (_gate)
        {
            Write(DailyBackupKey, on ? "1" : "0");
            _dailyBackup = on;
        }
    }

    public string? BackupFolder
    {
        get
        {
            lock (_gate)
            {
                // Pusty tekst i brak wpisu znaczą to samo — „folder domyślny" — więc
                // oba mają wyjść jako pustka. Inaczej wyczyszczenie pola zapisywałoby
                // ścieżkę o zerowej długości i kopie lądowałyby w katalogu roboczym.
                return _backupFolder ??= Read(BackupFolderKey) ?? string.Empty;
            }
        }
    }

    public void SetBackupFolder(string? path)
    {
        lock (_gate)
        {
            var patch = path?.Trim() ?? string.Empty;
            Write(BackupFolderKey, patch);
            _backupFolder = patch;
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
            var secret = clientSecret?.Trim() ?? string.Empty;

            Write(GoogleClientIdKey, id);
            Write(GoogleClientSecretKey, secret);

            _googleId = id;
            _googleSecret = secret;
        }
    }

    public void SetZone(string id)
    {
        // Wybranie strefy ręcznie kasuje poprzedni kłopot: jeśli nowa się rozstrzyga,
        // ostrzeżenie o starej byłoby już nieprawdą.
        _zoneTrouble = null;

        var zone = Resolve(id);

        lock (_gate)
        {
            Write(ZoneKey, zone.Id);
            _zone = zone;
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
                var fallback = Resolve(DefaultZoneId);

                _zoneTrouble =
                    $"Ten system nie zna strefy „{id}”. Godziny liczone są w „{fallback.Id}”.";

                return fallback;
            }

            _zoneTrouble =
                $"Ten system nie zna strefy „{id}”. Godziny liczone są w czasie uniwersalnym, "
                + "czyli o godzinę lub dwie wcześniej niż w Polsce. Wybierz strefę w ustawieniach.";

            return TimeZoneInfo.Utc;
        }
    }

    private string? Read(string key) =>
        db.LocalSettings.FirstOrDefault(s => s.Key == key)?.Value;

    private void Write(string key, string value)
    {
        if (db.LocalSettings.FirstOrDefault(s => s.Key == key) is { } existing)
        {
            existing.Set(value);
        }
        else
        {
            db.LocalSettings.Add(new LocalSetting(key, value));
        }

        db.SaveChanges();
    }
}

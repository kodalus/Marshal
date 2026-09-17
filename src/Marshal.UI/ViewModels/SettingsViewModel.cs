using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application.Abstractions;
using Marshal.Infrastructure.Backup;
using Marshal.Infrastructure.Sync.Google;

namespace Marshal.UI.ViewModels;

public sealed record ThemeOption(ThemeChoice Value, string Label)
{
    public static readonly IReadOnlyList<ThemeOption> All =
    [
        new(ThemeChoice.System, "za systemem"),
        new(ThemeChoice.Light, "jasny"),
        new(ThemeChoice.Dark, "ciemny"),
    ];

    public override string ToString() => Label;
}

/// <summary>
/// Ustawienia: motyw, strefa, kopia zapasowa (spec 11, 12).
/// </summary>
/// <remarks>
/// Wybranie pliku musi zrobić okno, nie model widoku: na Androidzie to jest dialog
/// systemowy podpięty do bieżącego ekranu, a nie ścieżka na dysku. Stąd
/// <see cref="SaveRequested"/> i <see cref="OpenRequested"/> podstawiane przez kod
/// okna — model widoku wie, **co** zapisać, a nie **gdzie**.
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettings _settings;
    private readonly BackupService _backup;
    private readonly IClock _clock;
    private readonly GoogleSyncService _dysk;

    /// <summary>Wstrzymuje zapis w chwili wypełniania pól wartościami z ustawień.</summary>
    private bool _wczytywanie;

    public SettingsViewModel(
        ISettings settings, BackupService backup, IClock clock, GoogleSyncService dysk)
    {
        _settings = settings;
        _backup = backup;
        _clock = clock;
        _dysk = dysk;
    }

    /// <summary>
    /// Wczytanie bieżących ustawień. **Nie w konstruktorze**: modele widoków powstają
    /// przy składaniu zależności, czyli zanim <c>PrepareAsync</c> założy bazę —
    /// sięgnięcie po ustawienia w konstruktorze wywracałoby start na tabeli,
    /// której jeszcze nie ma.
    /// </summary>
    public void Load()
    {
        // Wypełnienie idzie przez właściwości, więc bez tej blokady samo otwarcie
        // ekranu zapisywałoby do bazy motyw i strefę, których nikt nie zmieniał.
        _wczytywanie = true;

        Theme = ThemeOption.All.First(t => t.Value == _settings.Theme);
        Zone = Zones.Contains(_settings.Zone.Id) ? _settings.Zone.Id : Zones[0];
        GoogleClientId = _settings.GoogleClientId ?? string.Empty;
        GoogleClientSecret = _settings.GoogleClientSecret ?? string.Empty;

        _wczytywanie = false;

        // Wejście na ekran zawsze zastaje przycisk czynny. Gdyby poprzednia próba
        // utknęła mimo wszystko, wyjście i powrót ma wystarczyć zamiast restartu.
        IsSyncing = false;

        OnPropertyChanged(nameof(TokenFolder));

        OnPropertyChanged(nameof(Now));
    }

    /// <summary>Otwiera strumień do zapisu kopii albo zwraca <c>null</c>, gdy zrezygnowano.</summary>
    public Func<string, Task<Stream?>>? SaveRequested { get; set; }

    public Func<Task<Stream?>>? OpenRequested { get; set; }

    public IReadOnlyList<ThemeOption> Themes => ThemeOption.All;

    /// <summary>
    /// Strefy do wyboru: te, w których realnie bywasz.
    /// </summary>
    /// <remarks>
    /// Pełna lista systemowa ma kilkaset pozycji i szuka się w niej gorzej niż
    /// nie szuka. Gdy zapisana strefa jest spoza listy, dokładamy ją na miejscu —
    /// baza przeniesiona skądinąd ma się otworzyć, a nie po cichu przestawić.
    /// </remarks>
    public IReadOnlyList<string> Zones { get; } = BuildZones();

    [ObservableProperty]
    public partial ThemeOption? Theme { get; set; }

    [ObservableProperty]
    public partial string Zone { get; set; } = string.Empty;

    /// <summary>Jedno zdanie po ostatniej czynności. Bez okienek z „OK".</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    /// <summary>Co z tym, co już jest w bazie. Podmiana całości wymaga świadomego kliknięcia.</summary>
    [ObservableProperty]
    public partial bool ReplaceOnImport { get; set; }

    [ObservableProperty]
    public partial string GoogleClientId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GoogleClientSecret { get; set; } = string.Empty;

    /// <summary>Wynik ostatniej próby synchronizacji. Osobno od Status, bo dotyczy czego innego.</summary>
    [ObservableProperty]
    public partial string SyncStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSyncing { get; set; }

    /// <summary>Gdzie ląduje żeton — żeby dało się go skasować i zalogować od nowa.</summary>
    public string TokenFolder => _dysk.TokenFolder;

    /// <summary>
    /// Ile czekamy na powrót z przeglądarki, zanim uznamy, że nie wróci.
    /// </summary>
    /// <remarks>
    /// Zgoda w przeglądarce potrafi trwać: logowanie, drugi składnik, wybór konta.
    /// Pięć minut mieści to z zapasem i jednocześnie **kończy** czekanie, gdy powrotu
    /// nie będzie — a nie będzie go zawsze, gdy Google odrzuci zgodę, bo wtedy
    /// przeglądarka zostaje na stronie błędu i nie woła nas wcale.
    /// </remarks>
    private static readonly TimeSpan CzasNaZgode = TimeSpan.FromMinutes(5);

    private CancellationTokenSource? _przerwanie;

    /// <summary>
    /// Zapisanie poświadczeń i przebieg. Jedno polecenie, bo to jedna czynność:
    /// poświadczenia bez sprawdzenia nie mówią nic, a sprawdzić da się je tylko przebiegiem.
    /// </summary>
    [RelayCommand]
    private async Task SyncAsync()
    {
        _settings.SetGoogle(GoogleClientId, GoogleClientSecret);

        // Nasłuch na porcie pętli zwrotnej czeka na powrót z przeglądarki. Gdy Google
        // odrzuci zgodę — bo konta nie ma na liście testowej albo aplikacja nie jest
        // opublikowana — przeglądarka zostaje na stronie błędu i **nie wraca nigdy**.
        // Bez ograniczenia czasu i bez przerwania czekanie trwa do zamknięcia
        // aplikacji, a przycisk zostaje martwy: nie da się nawet poprawić poświadczeń.
        _przerwanie?.Dispose();
        _przerwanie = new CancellationTokenSource(CzasNaZgode);

        IsSyncing = true;
        SyncStatus = "Łączenie… przy pierwszym razie otworzy się przeglądarka.";

        try
        {
            var wynik = await _dysk.SyncAsync(_przerwanie.Token);
            SyncStatus = wynik.Message;
        }
        catch (OperationCanceledException)
        {
            SyncStatus = "Przerwane — zgoda w przeglądarce nie wróciła. "
                + "Jeśli Google pokazał stronę z błędem, popraw ustawienia w konsoli i spróbuj jeszcze raz.";
        }
        catch (Exception e)
        {
            // Polecenie wołane bez oczekiwania na wynik — wyjątek, którego tu nie
            // złapiemy, nie ma dokąd trafić.
            SyncStatus = e.Message;
        }
        finally
        {
            IsSyncing = false;
        }
    }

    /// <summary>Przerwanie czekania na zgodę. Bez tego jedynym wyjściem jest restart.</summary>
    [RelayCommand]
    private void CancelSync() => _przerwanie?.Cancel();

    /// <summary>Wgranie kopii zmienia wszystko, więc ekran pod spodem musi się przeliczyć.</summary>
    public event EventHandler? Imported;

    private static IReadOnlyList<string> BuildZones()
    {
        string[] czeste =
        [
            "Europe/Warsaw", "Europe/London", "Europe/Berlin", "Europe/Kyiv",
            "Europe/Lisbon", "Europe/Athens", "UTC",
        ];

        return czeste.Where(Istnieje).ToArray();
    }

    private static bool Istnieje(string id)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    partial void OnThemeChanged(ThemeOption? value)
    {
        if (value is not null && !_wczytywanie)
        {
            _settings.SetTheme(value.Value);
            ThemeChanged?.Invoke(this, value.Value);
        }
    }

    /// <summary>Podpinane przez aplikację — przestawienie motywu jest rzeczą okna, nie bazy.</summary>
    public event EventHandler<ThemeChoice>? ThemeChanged;

    partial void OnZoneChanged(string value)
    {
        if (_wczytywanie || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        _settings.SetZone(value);
        Status = $"Dni liczone w strefie {_settings.Zone.Id}.";
        OnPropertyChanged(nameof(Now));
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application.Abstractions;
using Marshal.Infrastructure.Backup;

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

    /// <summary>Wstrzymuje zapis w chwili wypełniania pól wartościami z ustawień.</summary>
    private bool _wczytywanie;

    public SettingsViewModel(ISettings settings, BackupService backup, IClock clock)
    {
        _settings = settings;
        _backup = backup;
        _clock = clock;
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

        _wczytywanie = false;

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

    public string Now => $"{_clock.Now:dd.MM.yyyy HH:mm} — dzisiaj to {_clock.Today:dd.MM.yyyy}";

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (SaveRequested is null)
        {
            return;
        }

        var nazwa = $"marshal-{_clock.Today:yyyy-MM-dd}.json";

        try
        {
            await using var strumien = await SaveRequested(nazwa);

            if (strumien is null)
            {
                return;
            }

            await _backup.ExportAsync(strumien);
            Status = $"Zapisane do {nazwa}.";
        }
        catch (Exception e)
        {
            // Łapane szeroko **celowo**. Polecenie wołane jest bez oczekiwania na wynik,
            // więc wyjątek, którego tu nie złapiemy, nie ma dokąd trafić: przycisk
            // wygląda na kliknięty, pliku nie ma i nikt się o tym nie dowie. Przy kopii
            // zapasowej cicha porażka jest gorsza niż brak kopii, bo zostawia
            // przekonanie, że kopia jest. Treść wyjątku, nie „coś poszło nie tak".
            Status = $"Nie udało się zapisać: {e.Message}";
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (OpenRequested is null)
        {
            return;
        }

        try
        {
            await using var strumien = await OpenRequested();

            if (strumien is null)
            {
                return;
            }

            var tryb = ReplaceOnImport ? ImportMode.Replace : ImportMode.Merge;
            var raport = await _backup.ImportAsync(strumien, tryb);

            Status = raport.Applied == 0
                ? $"Wczytane {raport.Read} wpisów — wszystkie starsze niż to, co już jest."
                : $"Wczytane {raport.Read} wpisów, nałożone {raport.Applied}.";

            Imported?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e)
        {
            // Jak wyżej. Wgranie jest w transakcji, więc baza została w stanie sprzed
            // próby — komunikat jest jedyną rzeczą, której brakuje.
            Status = $"Nie udało się wczytać: {e.Message}";
        }
    }

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

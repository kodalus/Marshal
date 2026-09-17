using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application.Abstractions;
using Marshal.Infrastructure.Backup;
using System.Collections.ObjectModel;
using Marshal.Application.Calendar;
using Marshal.Infrastructure.Calendar;
using Marshal.Domain.Calendar;
using Marshal.Domain.Diagnostics;
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
    private readonly CalendarSyncService _kalendarze;
    private readonly GoogleCalendarGateway _google;
    private readonly IActivityLog _dziennik;

    /// <summary>Wstrzymuje zapis w chwili wypełniania pól wartościami z ustawień.</summary>
    private bool _wczytywanie;

    public SettingsViewModel(
        ISettings settings,
        BackupService backup,
        IClock clock,
        GoogleSyncService dysk,
        CalendarSyncService kalendarze,
        GoogleCalendarGateway google,
        IActivityLog dziennik)
    {
        _settings = settings;
        _backup = backup;
        _clock = clock;
        _dysk = dysk;
        _kalendarze = kalendarze;
        _google = google;
        _dziennik = dziennik;
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
        GoogleCalendar = _settings.GoogleCalendarEnabled;
        AvailableGoogleCalendars.Clear();
        OnPropertyChanged(nameof(HasAvailableGoogleCalendars));

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

    /// <summary>
    /// Czy prosić także o odczyt kalendarza. Osobno, bo to uprawnienie ma inną cenę —
    /// zob. <see cref="ISettings.GoogleCalendarEnabled"/>.
    /// </summary>
    [ObservableProperty]
    public partial bool GoogleCalendar { get; set; }

    public ObservableCollection<CalendarSource> Calendars { get; } = [];

    public bool HasCalendars => Calendars.Count > 0;

    /// <summary>
    /// Kalendarze pobrane z konta, do wyboru jednym kliknięciem.
    /// </summary>
    /// <remarks>
    /// Pierwsza wersja tego ekranu kazała wpisywać identyfikator ręcznie. Skończyło się
    /// pięcioma kalendarzami dodanymi po nazwie i pięcioma błędami 404 z rzędu — bo
    /// „Praca" jest nazwą, a Google chce czegoś w rodzaju abc@group.calendar.google.com.
    /// Kazać człowiekowi szukać identyfikatora w ustawieniach Google było błędem
    /// ekranu, nie użytkownika: aplikacja jest zalogowana i może zapytać sama.
    /// </remarks>
    public ObservableCollection<GoogleCalendarInfo> AvailableGoogleCalendars { get; } = [];

    public bool HasAvailableGoogleCalendars => AvailableGoogleCalendars.Count > 0;

    [RelayCommand]
    private async Task LoadGoogleCalendarsAsync()
    {
        CalendarStatus = "Pobieranie listy kalendarzy…";

        try
        {
            var lista = await _google.ListAsync();

            AvailableGoogleCalendars.Clear();
            foreach (var kalendarz in lista)
            {
                AvailableGoogleCalendars.Add(kalendarz);
            }

            OnPropertyChanged(nameof(HasAvailableGoogleCalendars));

            CalendarStatus = lista.Count == 0
                ? "Konto nie ma żadnych kalendarzy."
                : $"Znalezione: {lista.Count}. Wybierz, które podłączyć.";

            await _dziennik.RecordAsync(
                "Kalendarze Google: lista", $"znalezionych {lista.Count}");
        }
        catch (Exception e)
        {
            CalendarStatus = e.Message;
            await _dziennik.RecordAsync(
                "Kalendarze Google: lista", "nie udało się", ActivityLevel.Problem, e.Message);
        }
    }

    [RelayCommand]
    private async Task AddGoogleCalendarAsync(GoogleCalendarInfo? kalendarz)
    {
        if (kalendarz is null)
        {
            return;
        }

        await DodajAsync(CalendarKind.Google, kalendarz.Id, kalendarz.Name, kalendarz.Color);
    }

    [ObservableProperty]
    public partial string NewIcalUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewIcalName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CalendarStatus { get; set; } = string.Empty;

    [RelayCommand]
    private async Task AddIcalAsync()
    {
        if (string.IsNullOrWhiteSpace(NewIcalUrl))
        {
            return;
        }

        var nazwa = string.IsNullOrWhiteSpace(NewIcalName) ? "Kanał iCal" : NewIcalName;

        await DodajAsync(CalendarKind.Ical, NewIcalUrl, nazwa);
        NewIcalUrl = string.Empty;
        NewIcalName = string.Empty;
    }

    [RelayCommand]
    private async Task RemoveCalendarAsync(CalendarSource? source)
    {
        if (source is null)
        {
            return;
        }

        await _kalendarze.RemoveAsync(source.Id);
        await ReloadCalendarsAsync();
    }

    /// <summary>
    /// Odświeżenie na żądanie, z wymuszeniem. Bez wymuszenia kanał odpytany w ciągu
    /// ostatniej godziny zostałby pominięty — a przy sprawdzaniu, czy konfiguracja
    /// w ogóle działa, „pominięte" wygląda identycznie jak „nic nie ma".
    /// </summary>
    [RelayCommand]
    private async Task RefreshCalendarsAsync()
    {
        CalendarStatus = "Pobieranie…";

        try
        {
            var raport = await _kalendarze.RefreshAsync(force: true);

            if (raport.Sources == 0 && raport.Failed == 0)
            {
                CalendarStatus = "Nie ma podłączonego żadnego kalendarza.";
                return;
            }

            var podsumowanie =
                $"Odświeżone {raport.Sources}, wydarzeń {raport.Events}, nieudanych {raport.Failed}.";

            // Powody, nie sama liczba. „Nieudanych 6" wygląda tak samo przy braku zgody,
            // przy złym adresie kanału i przy padniętej sieci — a to trzy różne rzeczy
            // do zrobienia. Powtórzone odsiewane, bo sześć kopii jednego zdania nie jest
            // sześcioma informacjami.
            CalendarStatus = raport.Problems.Count == 0
                ? podsumowanie
                : podsumowanie + Environment.NewLine
                    + string.Join(Environment.NewLine, raport.Problems.Distinct());

            await _dziennik.RecordAsync(
                "Kalendarz: pobranie",
                podsumowanie,
                raport.Failed > 0 ? ActivityLevel.Problem : ActivityLevel.Ok,
                string.Join(Environment.NewLine, raport.Problems.Distinct()));
        }
        catch (Exception e)
        {
            CalendarStatus = e.Message;
            await _dziennik.RecordAsync(
                "Kalendarz: pobranie", "nie udało się", ActivityLevel.Problem, e.Message);
        }
    }

    private async Task DodajAsync(CalendarKind kind, string externalId, string name, string? color = null)
    {
        try
        {
            await _kalendarze.AddAsync(kind, externalId, name, color);
            await _dziennik.RecordAsync("Kalendarz: podłączenie", $"{name} ({kind})");
            await ReloadCalendarsAsync();
            await RefreshCalendarsAsync();
        }
        catch (Exception e)
        {
            CalendarStatus = e.Message;
            await _dziennik.RecordAsync(
                "Kalendarz: podłączenie", $"{name} ({kind}) — nie udało się",
                ActivityLevel.Problem, e.Message);
        }
    }

    private async Task ReloadCalendarsAsync()
    {
        Calendars.Clear();
        foreach (var zrodlo in await _kalendarze.SourcesAsync())
        {
            Calendars.Add(zrodlo);
        }

        OnPropertyChanged(nameof(HasCalendars));
    }

    /// <summary>Gdzie ląduje żeton — żeby dało się go skasować i zalogować od nowa.</summary>
    public string TokenFolder => _dysk.TokenFolder;

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
            await _dziennik.RecordAsync("Kopia: zapis", nazwa);
        }
        catch (Exception e)
        {
            // Łapane szeroko **celowo**. Polecenie wołane jest bez oczekiwania na wynik,
            // więc wyjątek, którego tu nie złapiemy, nie ma dokąd trafić: przycisk
            // wygląda na kliknięty, pliku nie ma i nikt się o tym nie dowie. Przy kopii
            // zapasowej cicha porażka jest gorsza niż brak kopii, bo zostawia
            // przekonanie, że kopia jest. Treść wyjątku, nie „coś poszło nie tak".
            Status = $"Nie udało się zapisać: {e.Message}";
            await _dziennik.RecordAsync(
                "Kopia: zapis", "nie udało się", ActivityLevel.Problem, e.Message);
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

            await _dziennik.RecordAsync(
                "Kopia: wczytanie",
                $"przeczytane {raport.Read}, nałożone {raport.Applied}, pominięte {raport.Skipped}");

            Imported?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e)
        {
            // Jak wyżej. Wgranie jest w transakcji, więc baza została w stanie sprzed
            // próby — komunikat jest jedyną rzeczą, której brakuje.
            Status = $"Nie udało się wczytać: {e.Message}";
            await _dziennik.RecordAsync(
                "Kopia: wczytanie", "nie udało się", ActivityLevel.Problem, e.Message);
        }
    }

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
        _settings.SetGoogleCalendarEnabled(GoogleCalendar);

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

            await _dziennik.RecordAsync(
                "Synchronizacja",
                $"wysłane {wynik.Sent}, nałożone {wynik.Applied}",
                wynik.Ok ? ActivityLevel.Ok : ActivityLevel.Problem,
                wynik.Message);
        }
        catch (OperationCanceledException)
        {
            SyncStatus = "Przerwane — zgoda w przeglądarce nie wróciła. "
                + "Jeśli Google pokazał stronę z błędem, popraw ustawienia w konsoli i spróbuj jeszcze raz.";

            await _dziennik.RecordAsync(
                "Synchronizacja", "przerwane po czasie oczekiwania na zgodę",
                ActivityLevel.Problem);
        }
        catch (Exception e)
        {
            // Polecenie wołane bez oczekiwania na wynik — wyjątek, którego tu nie
            // złapiemy, nie ma dokąd trafić.
            SyncStatus = e.Message;
            await _dziennik.RecordAsync(
                "Synchronizacja", "nie udało się", ActivityLevel.Problem, e.Message);
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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application.Abstractions;
using Marshal.Infrastructure.Notifications;
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
    private readonly InAppNotifier _powiadomienia;
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
        IActivityLog dziennik,
        InAppNotifier powiadomienia)
    {
        _powiadomienia = powiadomienia;
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
        WczytajKonta();

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

    /// <summary>
    /// Czy pokazać tajemnicę klienta zamiast kropek.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kropki są tu odruchem, a nie zabezpieczeniem. Tajemnica klienta w aplikacji
    /// instalowanej u użytkownika nie jest tajemnicą — chroni zgoda w przeglądarce,
    /// nie ona — a zakryta na stałe robi z tego pola pułapkę: konsola Google pokazuje
    /// ją **raz, przy zakładaniu**, więc jeśli nie została wtedy zapisana, jedynym
    /// miejscem, w którym jeszcze jest, bywa właśnie to pole.
    /// </para>
    /// <para>
    /// Domyślnie zakryte, bo ekran ustawień otwiera się też przy kimś obok.
    /// </para>
    /// </remarks>
    [ObservableProperty]
    public partial bool ShowSecret { get; set; }

    /// <summary>
    /// Znak zasłaniający tajemnicę. Zero znaczy „nie zasłaniaj".
    /// </summary>
    /// <remarks>
    /// Właściwość widoku zamiast przelicznika wartości — tak jak wszędzie w tym
    /// projekcie. XAML wiąże się tu wprost i nie ma czego szukać w osobnym pliku.
    /// </remarks>
    public char SecretChar => ShowSecret ? '\0' : '\u2022';

    partial void OnShowSecretChanged(bool value) => OnPropertyChanged(nameof(SecretChar));

    /// <summary>Wynik ostatniej próby synchronizacji. Osobno od Status, bo dotyczy czego innego.</summary>
    [ObservableProperty]
    public partial string SyncStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSyncing { get; set; }

    /// <summary>Adres przepisany z paska przeglądarki, gdy nie wróciła sama.</summary>
    [ObservableProperty]
    public partial string ConsentUrl { get; set; } = string.Empty;

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

    /// <summary>Pozycja „nigdzie" na liście kalendarza domyślnego.</summary>
    private static readonly MainCalendarChoice BezKalendarza = new(null, "nigdzie — tylko w Marshalu");

    private bool _wczytywanieKalendarza;

    /// <summary>
    /// Kalendarz, w którym lądują zadania z godziną.
    /// </summary>
    /// <remarks>
    /// Bez tego każde zadanie trzeba było przenosić do kalendarza ręcznie, jedno po
    /// drugim — a synchronizacja, o której trzeba pamiętać przy każdym zadaniu, nie
    /// jest synchronizacją. Zadanie stoi w jednym kalendarzu naraz: przeniesione do
    /// wspólnego znika z tego, a wraca po cofnięciu.
    /// </remarks>
    [ObservableProperty]
    public partial MainCalendarChoice? MainCalendar { get; set; }

    public ObservableCollection<MainCalendarChoice> MainCalendars { get; } = [];

    /// <summary>
    /// Co wyszło z ostatniej próby powiadomienia. Przy powiadomieniach to jedyny sposób,
    /// żeby czegokolwiek się dowiedzieć.
    /// </summary>
    /// <remarks>
    /// „Nie ma powiadomienia" ma trzy przyczyny wyglądające identycznie: nie podpięła
    /// się obsługa systemowa, podpięła się i system odmówił, albo nie było czego
    /// pokazać. Pierwsza jest do naprawienia w kodzie, druga po stronie systemu —
    /// i bez rozróżnienia obie naprawia się na oślep.
    /// </remarks>
    [ObservableProperty]
    public partial string NotificationStatus { get; set; } = string.Empty;

    /// <summary>Próbne powiadomienie. Wynik od razu na ekranie, nie w dzienniku.</summary>
    [RelayCommand]
    private async Task TestNotificationAsync()
    {
        // Napis bez nazwy systemu. Ta sama aplikacja chodzi na Windowsie i na Androidzie,
        // a powiadomienie mówiące o dymku Windowsa na telefonie wygląda jak wzięte
        // z cudzego programu — i każe szukać czegoś, czego tam nie ma.
        await _powiadomienia.ShowAsync(new Notification(
            Guid.Empty,
            "Marshal — próba",
            "Jeśli widzisz to poza oknem aplikacji, powiadomienia systemowe działają."));

        NotificationStatus = InAppNotifier.StanSystemowych == "podpięte"
            ? "Podpięte, wysłane. Jeśli powiadomienie się nie pokazało, zatrzymał je "
                + "system — na Windowsie najczęściej brak aplikacji w menu Start albo "
                + "tryb skupienia, na Androidzie odmowa zgody na powiadomienia."
            : $"Powiadomienia systemowe nie działają: {InAppNotifier.StanSystemowych}";
    }

    partial void OnMainCalendarChanged(MainCalendarChoice? value)
    {
        if (_wczytywanieKalendarza)
        {
            return;
        }

        _settings.SetMainCalendar(value?.Id);
    }

    public bool HasAvailableGoogleCalendars => AvailableGoogleCalendars.Count > 0;

    [RelayCommand]
    private async Task LoadGoogleCalendarsAsync()
    {
        CalendarStatus = "Pobieranie listy kalendarzy…";

        List<GoogleCalendarInfo> lista = [];
        List<string> klopoty = [];

        // Konto główne i każde dodane — po kolei, a nie „wszystko albo nic". Jedno
        // konto bez zgody na tym urządzeniu nie ma zabierać kalendarzy pozostałych:
        // wtedy przycisk zwracałby pustą listę i wyglądałoby to na brak kalendarzy
        // w ogóle, zamiast na brak zgody jednego konta.
        var konta = new List<string?> { null };
        konta.AddRange(_settings.CalendarAccounts);

        foreach (var konto in konta)
        {
            try
            {
                lista.AddRange(await _google.ListAsync(konto));
            }
            catch (Exception e)
            {
                klopoty.Add($"{konto ?? "konto główne"}: {e.Message}");
                await _dziennik.RecordAsync(
                    "Kalendarze Google: lista",
                    konto ?? "konto główne",
                    ActivityLevel.Problem,
                    e.Message);
            }
        }

        AvailableGoogleCalendars.Clear();
        foreach (var kalendarz in lista)
        {
            AvailableGoogleCalendars.Add(kalendarz);
        }

        OnPropertyChanged(nameof(HasAvailableGoogleCalendars));

        var podsumowanie = lista.Count == 0
            ? "Żadne konto nie pokazało kalendarzy."
            : $"Znalezione: {lista.Count}. Wybierz, które podłączyć.";

        CalendarStatus = klopoty.Count == 0
            ? podsumowanie
            : podsumowanie + Environment.NewLine + string.Join(Environment.NewLine, klopoty);

        if (klopoty.Count == 0)
        {
            await _dziennik.RecordAsync(
                "Kalendarze Google: lista", $"znalezionych {lista.Count}");
        }
    }

    /// <summary>Konta, z których podłączono kalendarze. Bez konta głównego.</summary>
    public ObservableCollection<string> CalendarAccounts { get; } = [];

    public bool HasCalendarAccounts => CalendarAccounts.Count > 0;

    /// <summary>
    /// Zgoda drugiego konta Google.
    /// </summary>
    /// <remarks>
    /// Osobno od logowania głównego, bo to nie to samo: konto główne trzyma Dysk
    /// z dziennikiem synchronizacji, dodatkowe wnosi wyłącznie kalendarze. Droga przez
    /// udostępnienie kalendarza samemu sobie zostaje i dalej jest prostsza tam, gdzie
    /// wystarcza — ale kalendarza służbowego często nie wolno udostępnić na zewnątrz,
    /// a wtedy nie ma czym jej zastąpić poza drugą zgodą.
    /// </remarks>
    [RelayCommand]
    private async Task AddGoogleAccountAsync()
    {
        CalendarStatus = "Czekam na zgodę w przeglądarce…";

        try
        {
            var adres = await _google.DodajKontoAsync();

            _settings.AddCalendarAccount(adres);
            WczytajKonta();

            CalendarStatus = $"Konto {adres} dodane. Pobierz kalendarze, żeby je podłączyć.";
            await _dziennik.RecordAsync("Kalendarz: konto Google", adres);

            await LoadGoogleCalendarsAsync();
        }
        catch (Exception e)
        {
            CalendarStatus = e.Message;
            await _dziennik.RecordAsync(
                "Kalendarz: konto Google", "nie udało się", ActivityLevel.Problem, e.Message);
        }
    }

    /// <summary>
    /// Odłączenie konta. Podłączone z niego kalendarze zostają — i mówią, czego im brak.
    /// </summary>
    /// <remarks>
    /// Kasowanie przy okazji kalendarzy tego konta byłoby kasowaniem danych, które jadą
    /// na inne urządzenia — tam zgoda może dalej być. Odłączenie konta jest decyzją
    /// tego urządzenia, więc i skutek ma mieć tylko tutaj.
    /// </remarks>
    [RelayCommand]
    private async Task RemoveGoogleAccountAsync(string? adres)
    {
        if (string.IsNullOrWhiteSpace(adres))
        {
            return;
        }

        _settings.RemoveCalendarAccount(adres);
        WczytajKonta();

        CalendarStatus = $"Konto {adres} odłączone od tego urządzenia.";
        await _dziennik.RecordAsync("Kalendarz: konto odłączone", adres);
    }

    private void WczytajKonta()
    {
        CalendarAccounts.Clear();
        foreach (var konto in _settings.CalendarAccounts)
        {
            CalendarAccounts.Add(konto);
        }

        OnPropertyChanged(nameof(HasCalendarAccounts));
    }

    [RelayCommand]
    private async Task AddGoogleCalendarAsync(GoogleCalendarInfo? kalendarz)
    {
        if (kalendarz is null)
        {
            return;
        }

        await DodajAsync(
            CalendarKind.Google, kalendarz.Id, kalendarz.Name, kalendarz.Color, kalendarz.Account);
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
                $"Odświeżone {raport.Sources}, wydarzeń {raport.Events}, nieudanych {raport.Failed}."
                + (raport.Folded > 0
                    ? $" Odrzucone powtórzone podłączenia: {raport.Folded}."
                    : string.Empty);

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

    private async Task DodajAsync(
        CalendarKind kind,
        string externalId,
        string name,
        string? color = null,
        string? konto = null)
    {
        try
        {
            await _kalendarze.AddAsync(kind, externalId, name, color, konto);

            // Konto w dzienniku, bo ten sam kalendarz podłączony z dwóch kont daje dwa
            // wiersze o tej samej nazwie — i bez adresu nie widać, który jest który.
            var skad = string.IsNullOrWhiteSpace(konto) ? kind.ToString() : $"{kind}, {konto}";
            await _dziennik.RecordAsync("Kalendarz: podłączenie", $"{name} ({skad})");
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

        // Do wyboru tylko te, do których umiemy pisać. Kanał iCal jest do odczytu,
        // więc postawienie go tu byłoby ustawieniem bez skutku.
        MainCalendars.Clear();
        MainCalendars.Add(BezKalendarza);

        foreach (var zrodlo in Calendars.Where(z => _kalendarze.CanWrite(z.Kind)))
        {
            MainCalendars.Add(new MainCalendarChoice(zrodlo.Id, zrodlo.Name));
        }

        _wczytywanieKalendarza = true;
        MainCalendar = MainCalendars.FirstOrDefault(w => w.Id == _settings.MainCalendarId)
            ?? BezKalendarza;
        _wczytywanieKalendarza = false;

        OnPropertyChanged(nameof(HasCalendars));
    }

    /// <summary>Gdzie ląduje żeton — żeby dało się go skasować i zalogować od nowa.</summary>
    public string TokenFolder => _dysk.TokenFolder;

    public string Now =>
        $"{_clock.Now:dd.MM.yyyy HH:mm zzz} — dzisiaj to {_clock.Today:dd.MM.yyyy}";

    /// <summary>Co jest nie tak ze strefą. Puste, gdy działa ta wybrana.</summary>
    public string? ZoneProblem => _settings.ZoneProblem;

    public bool HasZoneProblem => !string.IsNullOrEmpty(ZoneProblem);

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
    /// <summary>
    /// Dokończenie zgody adresem przepisanym z przeglądarki.
    /// </summary>
    /// <remarks>
    /// Droga ratunkowa, nie zwykła. Przeglądarka po zgodzie ma wrócić do aplikacji
    /// sama; na telefonie potrafi tego nie zrobić, bo system odkłada do zamrażarki
    /// proces, który zszedł w tło — wtedy jądro przyjmuje połączenie, ale nie ma go
    /// komu obsłużyć i przeglądarka wisi. Adres z jej paska zawiera ten sam kod zgody,
    /// który przyszedłby przez gniazdo.
    /// </remarks>
    [RelayCommand]
    private void FinishConsent()
    {
        if (string.IsNullOrWhiteSpace(ConsentUrl))
        {
            SyncStatus = "Wklej adres z paska przeglądarki — ten, na którym się zatrzymała.";
            return;
        }

        SyncStatus = PowrotZgody.Podaj(ConsentUrl.Trim())
            ? "Adres przyjęty — dokańczam logowanie."
            : "Nic nie czeka na adres. Najpierw kliknij „Zapisz i zsynchronizuj”.";

        ConsentUrl = string.Empty;
    }

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

/// <summary>Kalendarz domyślny na liście wyboru. Puste znaczy „nigdzie".</summary>
public sealed record MainCalendarChoice(Guid? Id, string Name)
{
    public override string ToString() => Name;
}

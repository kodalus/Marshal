using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application.Abstractions;
using Marshal.Application.Calendar;
using Marshal.Domain.Diagnostics;
using Marshal.Application.Repositories;
using Marshal.Application.Review;
using Marshal.Application.UseCases;
using Marshal.Domain.Areas;
using Marshal.Domain.Calendar;
using Marshal.Domain.Projects;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Tasks;
using Marshal.Infrastructure.Notifications;
using Marshal.Infrastructure.Sync.Google;

namespace Marshal.UI.ViewModels;

public enum Screen
{
    Today,
    Now,
    Inbox,
    Clarify,
    Next,
    Projects,
    Someday,
    Waiting,
    Calendar,
    Notes,
    Filters,
    Settings,
    Archive,
    Review,

    /// <summary>Co aplikacja zrobiła i co z tego wyszło (spec 12).</summary>
    Journal,
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly InboxService _inbox;
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly IAreaRepository _areas;
    private readonly IClock _clock;
    private readonly TaskEditService _edit;
    private readonly FocusService _focus;
    private readonly IReviewQueries _queries;
    private readonly InAppNotifier _notifier;
    private readonly IActivityLog _dziennik;
    private readonly NoteService _notes;
    private readonly StructureEditService _szkielet;
    private readonly TaskMirror _odbicie;
    private readonly CalendarSyncService _kalendarze;
    private readonly ReminderService _przypomnienia;

    private readonly GoogleSyncService _dysk;

    private readonly DayRolloverService _przejscieDnia;

    /// <summary>
    /// Czy od ostatniego przebiegu coś zapisano w oknie.
    /// </summary>
    /// <remarks>
    /// Podnoszone spoza wątku okna — zapis kończy się tam, gdzie skończyła się baza —
    /// więc czytane i zerowane przez <see cref="Interlocked"/>. Zwykłe pole
    /// <c>bool</c> wystarczyłoby do odczytu, ale nie do „sprawdź i wyzeruj" jednym
    /// ruchem: między sprawdzeniem a zerowaniem zmieściłby się kolejny zapis i wypadłby
    /// z rachunku.
    /// </remarks>
    private int _zmiana;

    /// <summary>Kiedy ostatni przebieg się skończył. Do odmierzania przerwy.</summary>
    private DateTimeOffset _ostatniaSynchronizacja = DateTimeOffset.MinValue;

    /// <summary>Czy przebieg właśnie trwa.</summary>
    /// <remarks>
    /// Przebieg bywa dłuższy od minuty — sieć, logowanie, kilka odcinków — a minutnik
    /// nie czeka. Bez tej blokady wolna synchronizacja prosiłaby sama siebie o drugą.
    /// </remarks>
    private bool _trwaSynchronizacja;

    /// <summary>Co ile sprawdzać Dysk, gdy nic się nie zmieniło.</summary>
    /// <remarks>
    /// Po to, żeby zobaczyć **cudze** zmiany: to, co dopisał telefon, przychodzi tylko
    /// przez odczyt. Pięć minut, bo tyle znosi się jako opóźnienie, a częściej znaczy
    /// kilkadziesiąt zapytań do Dysku na godzinę bez powodu.
    /// </remarks>
    private static readonly TimeSpan Przerwa = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Dzień, na którym stanęło okno. Do wykrycia północy przy otwartej aplikacji.
    /// </summary>
    /// <remarks>
    /// Pusty do pierwszego sprawdzenia, a nie ustawiony w konstruktorze. Zegar liczy
    /// dzień w strefie z ustawień, czyli **czyta bazę** — a składanie zależności musi
    /// się obejść bez bazy, bo dzieje się przed migracjami. Pilnuje tego test
    /// „złożenie zależności nie sięga do bazy".
    /// </remarks>
    private DateOnly? _dzien;

    public MainViewModel(
        InboxService inbox,
        ITaskRepository tasks,
        IProjectRepository projects,
        IAreaRepository areas,
        IClock clock,
        TaskEditService edit,
        FocusService focus,
        IReviewQueries queries,
        InAppNotifier notifier,
        ClarifyViewModel clarify,
        TaskDetailViewModel detail,
        ReviewViewModel review,
        NowViewModel nowVm,
        CalendarViewModel calendar,
        NotesViewModel notes,
        FiltersViewModel filters,
        SettingsViewModel settings,
        JournalViewModel journal,
        IActivityLog dziennik,
        NoteService noteService,
        StructureEditService szkielet,
        TaskMirror odbicie,
        CalendarSyncService kalendarze,
        ReminderService przypomnienia,
        GoogleSyncService dysk,
        DayRolloverService przejscieDnia,
        ISygnalZapisu sygnal)
    {
        _inbox = inbox;
        _szkielet = szkielet;
        _odbicie = odbicie;
        _kalendarze = kalendarze;
        _przypomnienia = przypomnienia;
        _dysk = dysk;
        _przejscieDnia = przejscieDnia;

        // Znak z jednostki pracy przychodzi z cudzego wątku, więc wolno tu zrobić
        // dokładnie jedno: odłożyć notatkę. Przebieg rusza z minutnika, czyli z okna.
        sygnal.Zapisano += () => Interlocked.Exchange(ref _zmiana, 1);
        _tasks = tasks;
        _projects = projects;
        _areas = areas;
        _clock = clock;
        _edit = edit;
        _focus = focus;
        _queries = queries;
        _notifier = notifier;
        _dziennik = dziennik;
        _notes = noteService;
        Clarify = clarify;
        Detail = detail;
        Review = review;
        Now = nowVm;
        Calendar = calendar;
        Notes = notes;
        Filters = filters;
        Settings = settings;
        Journal = journal;
        Clarify.Emptied += (_, _) => Bezpiecznie("Skrzynka opróżniona", ShowInboxAsync);

        // Po zapisie szczegółu ekran musi się przeliczyć: zmiana terminu albo dnia
        // wykonania potrafi przenieść zadanie na inną listę niż ta, z której je otwarto.
        Detail.Saved += (_, _) => Bezpiecznie("Ekran: odświeżenie po zapisie", ReloadAsync);

        // Przegląd zmienia stan zadań i projektów, więc ekran pod spodem musi się
        // przeliczyć — także liczniki niezmienników w „Dzisiaj".
        Review.Changed += (_, _) => Bezpiecznie("Ekran: odświeżenie po przeglądzie", ReloadAsync);

        // Krok skrzynki prowadzi do drzewka przetwarzania. Przegląd zostaje otwarty —
        // wznowi się na tym samym kroku, bo jego stan siedzi w bazie, a nie w ekranie.
        Review.InboxRequested += (_, _) => Bezpiecznie("Przegląd: skrzynka", ShowClarifyAsync);
        Now.Changed += (_, _) => Bezpiecznie("Teraz: odświeżenie piątki", RefreshFocusAsync);

        // Wgranie kopii zmienia wszystko naraz, więc ekran pod spodem musi się
        // przeliczyć — inaczej lista pokazuje stan sprzed wczytania, wyglądając
        // na aktualną.
        Settings.Imported += (_, _) => Bezpiecznie("Ekran: odświeżenie po wczytaniu kopii", ReloadAsync);

        // Kliknięcie w blok na siatce otwiera tę samą nakładkę, co kliknięcie na liście.
        Calendar.NewTaskRequested += (dzien, pora) =>
            Bezpiecznie("Kalendarz: nowe zadanie", () => Detail.NewAsync(dzien, pora));

        Calendar.TaskRequested += id => Bezpiecznie("Kalendarz: otwarcie zadania", async () =>
        {
            if (await _tasks.FindAsync(id) is { } zadanie)
            {
                await Detail.LoadAsync(zadanie);
            }
        });
    }

    public ClarifyViewModel Clarify { get; }

    public TaskDetailViewModel Detail { get; }

    public ReviewViewModel Review { get; }

    public NowViewModel Now { get; }

    public CalendarViewModel Calendar { get; }

    public NotesViewModel Notes { get; }

    public FiltersViewModel Filters { get; }

    public SettingsViewModel Settings { get; }

    public JournalViewModel Journal { get; }

    public ObservableCollection<TaskItem> InboxItems { get; } = [];

    public ObservableCollection<TaskRow> NextActions { get; } = [];

    public ObservableCollection<TaskRow> TodayItems { get; } = [];

    /// <summary>Przypomnienia, które odezwały się przy tym uruchomieniu.</summary>
    public ObservableCollection<Notification> Reminders { get; } = [];

    public ObservableCollection<WaitingItem> WaitingItems { get; } = [];

    /// <summary>Tabela równowagi (8.5). Widoczna wyłącznie tutaj i w kroku 8 przeglądu.</summary>
    /// <summary>Projekty bez następnej akcji (N1) — pozycja w „Dzisiaj".</summary>
    public ObservableCollection<BlockedProject> BlockedProjects { get; } = [];

    /// <summary>Oczekiwane, którym minął próg ponaglenia (N3) — pozycja w „Dzisiaj".</summary>
    public ObservableCollection<WaitingItem> Nudges { get; } = [];

    /// <summary>Pięć slotów wyboru na dziś (spec 8.6).</summary>
    public ObservableCollection<TaskItem> FocusItems { get; } = [];

    /// <summary>
    /// Piątka na dziś, której nie ma jeszcze na siatce.
    /// </summary>
    /// <remarks>
    /// Pasek nad kalendarzem ma przypominać o tym, czego **nie widać** poniżej.
    /// Zadanie z godziną ma już swój blok, więc powtórzone w pasku byłoby drugą kopią
    /// tej samej rzeczy — i to tą, która zabiera miejsce nad siatką.
    /// </remarks>
    public ObservableCollection<TaskItem> FocusOffGrid { get; } = [];

    /// <summary>Kandydaci do wyboru na dziś: „Następne" i zaplanowane na dziś lub wcześniej.</summary>
    public ObservableCollection<TaskRow> FocusCandidates { get; } = [];

    public ObservableCollection<TaskItem> SomedayItems { get; } = [];

    public ObservableCollection<TaskItem> ArchiveItems { get; } = [];

    public ObservableCollection<ProjectTreeRow> ProjectRows { get; } = [];

    /// <summary>
    /// Wyjaśnienie odmowy — jeden pasek na całe okno.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Napis na ekranie, nie okienko: odmowa jest odpowiedzią na kliknięcie, które nie
    /// przyniosło skutku, a okienko każe kliknąć jeszcze raz, żeby wrócić tam, gdzie
    /// się było.
    /// </para>
    /// <para>
    /// Jeden pasek, nie po jednym na ekran. Odmowy zdarzały się w miejscach, w których
    /// nie było ich gdzie pokazać — „pięć slotów zajętych" miało swoją ramkę wyłącznie
    /// na „Dzisiaj", więc wzięcie na dziś z „Kiedyś" po prostu nic nie robiło i nie
    /// mówiło dlaczego.
    /// </para>
    /// </remarks>
    [ObservableProperty]
    public partial string Notice { get; set; } = string.Empty;

    [RelayCommand]
    private void DismissNotice() => Notice = string.Empty;

    /// <summary>
    /// Kalendarz jest ekranem startowym: pierwsze pytanie dnia brzmi „co dziś jest
    /// umówione", a nie „co mam na liście".
    /// </summary>
    /// <remarks>
    /// Wartość początkowa, a nie samo wywołanie w <c>InitializeAsync</c>. Okno rysuje
    /// się, zanim wczytywanie dobiegnie końca, więc przy „Dzisiaj" na starcie widać
    /// było mignięcie listy, po którym dopiero wchodził kalendarz.
    /// </remarks>
    [ObservableProperty]
    public partial Screen Current { get; set; } = Screen.Calendar;

    /// <summary>
    /// Czy okno jest wąskie — czyli czy to telefon albo wąskie okno na pulpicie.
    /// </summary>
    /// <remarks>
    /// Piętnaście przycisków nawigacji zawiniętych w pięć rzędów zjada na telefonie
    /// trzecią część ekranu, **zanim pojawi się cokolwiek treści**. Na pulpicie te same
    /// przyciski mieszczą się w jednym rzędzie i są najszybszą drogą do każdego ekranu.
    /// To nie jest jeden układ do poprawienia, tylko dwa różne układy do tej samej
    /// nawigacji: szeroko — wszystko naraz, wąsko — cztery pod kciukiem i reszta
    /// pod „Więcej".
    /// </remarks>
    [ObservableProperty]
    public partial bool IsNarrow { get; set; }

    /// <summary>Rozwinięta lista pozostałych ekranów. Tylko przy wąskim oknie.</summary>
    [ObservableProperty]
    public partial bool IsMoreOpen { get; set; }

    /// <summary>Nawigacja szeroka: wszystkie ekrany naraz.</summary>
    public bool ShowWideNav => !IsClarify && !IsNarrow;

    /// <summary>Pasek dolny: cztery pod kciukiem i „Więcej".</summary>
    public bool ShowBottomNav => !IsClarify && IsNarrow;

    /// <summary>
    /// Wrzut jest dostępny z każdego ekranu poza przetwarzaniem: myśl przychodzi wtedy,
    /// kiedy przychodzi, a nie wtedy, gdy akurat jesteś w skrzynce (spec 1.3).
    /// </summary>
    public bool ShowCapture => !IsClarify;

    [ObservableProperty]
    public partial int InboxCount { get; set; }

    /// <summary>Pole szybkiego wrzutu. Zasada 1.3: tylko tytuł, żadnych innych pól.</summary>
    [ObservableProperty]
    public partial string CaptureText { get; set; } = string.Empty;

    /// <summary>Avalonia nie zamienia liczby na wartość logiczną — potrzebne wprost.</summary>
    public bool HasInbox => InboxCount > 0;

    public bool HasReminders => Reminders.Count > 0;

    public bool IsToday => Current == Screen.Today;

    public bool IsNow => Current == Screen.Now;

    public bool FocusIsFull => FocusItems.Count >= FocusService.Slots;

    public string FocusCount => $"{FocusItems.Count} z {FocusService.Slots}";

    /// <summary>
    /// Zadanie czekające na zwolnienie slotu. Puste, dopóki piątka nie jest pełna.
    /// </summary>
    /// <remarks>
    /// Pytanie „które schodzi" bez pokazania czego dotyczy nie jest pytaniem, więc
    /// odmowa niesie ze sobą obecną piątkę i tę pozycję (spec 8.6).
    /// </remarks>
    [ObservableProperty]
    public partial TaskItem? PendingFocus { get; set; }

    public bool HasPendingFocus => PendingFocus is not null;

    partial void OnPendingFocusChanged(TaskItem? value) => OnPropertyChanged(nameof(HasPendingFocus));

    public bool IsInbox => Current == Screen.Inbox;

    public bool IsClarify => Current == Screen.Clarify;

    public bool IsNext => Current == Screen.Next;

    public bool IsProjects => Current == Screen.Projects;

    public bool IsSomeday => Current == Screen.Someday;

    public bool IsWaiting => Current == Screen.Waiting;

    public bool IsCalendar => Current == Screen.Calendar;

    public bool IsNotes => Current == Screen.Notes;

    public bool IsJournal => Current == Screen.Journal;

    public bool IsFilters => Current == Screen.Filters;

    public bool IsSettings => Current == Screen.Settings;

    public bool IsReview => Current == Screen.Review;

    public bool HasNudges => Nudges.Count > 0;

    /// <summary>
    /// Czy jest cokolwiek na dziś. Pusta lista ma powiedzieć, że jest pusto — nie
    /// zostawić prostokąta, po którym nie wiadomo, czy to brak zadań, czy brak wczytania.
    /// </summary>
    public bool HasToday => TodayItems.Count > 0;

    public bool HasBlocked => BlockedProjects.Count > 0;

    public bool IsArchive => Current == Screen.Archive;

    partial void OnInboxCountChanged(int value) => OnPropertyChanged(nameof(HasInbox));

    partial void OnCurrentChanged(Screen value)
    {
        OnPropertyChanged(nameof(IsToday));
        OnPropertyChanged(nameof(IsNow));
        OnPropertyChanged(nameof(IsInbox));
        OnPropertyChanged(nameof(IsClarify));
        OnPropertyChanged(nameof(IsNext));
        OnPropertyChanged(nameof(IsProjects));
        OnPropertyChanged(nameof(IsSomeday));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(IsCalendar));
        OnPropertyChanged(nameof(IsNotes));
        OnPropertyChanged(nameof(IsJournal));
        OnPropertyChanged(nameof(IsFilters));
        OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(IsReview));
        OnPropertyChanged(nameof(IsArchive));

        // Wybranie czegokolwiek zamyka „Więcej". Lista, która zostaje otwarta nad
        // wybranym ekranem, wymaga drugiego gestu na zamknięcie i uczy, że nawigacja
        // to dwa kroki zamiast jednego.
        IsMoreOpen = false;

        OnPropertyChanged(nameof(ShowWideNav));
        OnPropertyChanged(nameof(ShowBottomNav));
        OnPropertyChanged(nameof(ShowCapture));
    }

    partial void OnIsNarrowChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowWideNav));
        OnPropertyChanged(nameof(ShowBottomNav));
    }

    [RelayCommand]
    private void ToggleMore() => IsMoreOpen = !IsMoreOpen;

    [RelayCommand]
    private void CloseMore() => IsMoreOpen = false;

    public async Task InitializeAsync()
    {
        CollectReminders();
        await RefreshInboxAsync();

        // Wczytanie tego, co i tak jest już wybrane: ekranem startowym jest kalendarz
        // (patrz Current), a wybór bez danych to pusta siatka.
        await ShowCalendarAsync();

        // Synchronizacja przy starcie, bez czekania na nią.
        //
        // Do dziś pierwsze przebiegi były świadomie ręczne: dopóki nie było wiadomo,
        // czy droga w ogóle działa, cicha synchronizacja przy starcie znaczyłaby, że
        // pierwszy błąd widać jako **brakujące zadania**, a nie jako komunikat. Droga
        // działa, więc powód zniknął — a został ten po drugiej stronie: aplikacja
        // otwarta na telefonie pokazywała stan sprzed ostatniej synchronizacji i nie
        // było po niej widać, że jest nieświeży.
        Bezpiecznie("Synchronizacja przy starcie", SynchronizujCichoAsync);
    }

    /// <summary>
    /// Sprawdzenie przypomnień. Wołane co minutę z okna.
    /// </summary>
    /// <remarks>
    /// Do dziś przypomnienia sprawdzały się **wyłącznie przy starcie aplikacji**.
    /// Przypomnienie ustawione na siedemnastą przy aplikacji otwartej od rana nie
    /// odzywało się nigdy — a to jest dokładnie ten przypadek, dla którego ustawia się
    /// przypomnienia. Sprawdzenie przy starcie zostaje, bo łapie to, co wypadło przy
    /// zamkniętej aplikacji; minutnik dokłada resztę.
    /// </remarks>
    public async Task CheckRemindersAsync()
    {
        await PrzejscieDniaAsync();

        if (await _przypomnienia.RunAsync() > 0)
        {
            CollectReminders();
        }

        // Osobno zabezpieczona: nieudany przebieg do Dysku nie ma prawa zabrać ze sobą
        // przypomnień, które właśnie się policzyły.
        await Probuj("Synchronizacja sama", SynchronizujSamaAsync);
    }

    /// <summary>
    /// Synchronizacja bez klikania. Sprawdzana minutnikiem razem z przypomnieniami.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Do dziś jedyną drogą na Dysk był przycisk „Zapisz i zsynchronizuj" — czyli
    /// każda zmiana wymagała pamiętania o niej. Przy dwóch urządzeniach znaczyło to,
    /// że telefon pokazywał stan sprzed ostatniego kliknięcia, a nie stan rzeczy.
    /// </para>
    /// <para>
    /// Dwa powody na przebieg, bo synchronizacja ma dwie strony. <b>Zmiana</b> jest
    /// powodem do wysłania: zapis z okna podnosi znak i najbliższa minuta go zabiera.
    /// <b>Przerwa</b> jest powodem do odczytu: to, co dopisał telefon, nie zapowiada
    /// się niczym po tej stronie, więc trzeba po prostu zajrzeć.
    /// </para>
    /// <para>
    /// Z minutnika, a nie z własnego odliczania: to ta sama odpowiedź na upływ czasu,
    /// co przypomnienia i północ, i idzie wątkiem okna. Sam znak przychodzi spoza tego
    /// wątku i dlatego nie robi nic poza podniesieniem się.
    /// </para>
    /// </remarks>
    private async Task SynchronizujSamaAsync()
    {
        // Najpierw blokada, dopiero potem znak: przebieg bywa dłuższy od minuty,
        // a zabranie znaku teraz znaczyłoby zgubienie zmiany, która czeka na wysłanie.
        if (_trwaSynchronizacja)
        {
            return;
        }

        var zmiana = Interlocked.Exchange(ref _zmiana, 0) == 1;
        var przerwa = _clock.Now - _ostatniaSynchronizacja >= Przerwa;

        if (!zmiana && !przerwa)
        {
            return;
        }

        await PrzebiegAsync(zmiana ? "Synchronizacja po zmianie" : "Synchronizacja co jakiś czas", cicha: true);
    }

    /// <summary>
    /// Północ przy otwartej aplikacji.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Przejście dnia i wygaszenie wyborów działy się wyłącznie przy starcie: aplikacja
    /// zastawała różnicę dat i nadrabiała ją. Przy oknie otwartym przez dobę — a tak
    /// wygląda praca na komputerze — nie zastawała jej nigdy.
    /// </para>
    /// <para>
    /// Skutek widać było dopiero od czasu, gdy wybór na dziś zakłada wydarzenie
    /// w Google: wczorajsze wybory zostawały tam do następnego uruchomienia, bo to
    /// wygaszenie je stamtąd zdejmuje. Samo zadanie wracało do puli poprawnie —
    /// wygaszenie zdejmuje z niego dzień wyboru i nie rusza stanu, więc następnego
    /// dnia można je wziąć na nowo.
    /// </para>
    /// <para>
    /// Sprawdzane minutnikiem razem z przypomnieniami: to ta sama odpowiedź na upływ
    /// czasu, a drugi minutnik na tę samą minutę byłby drugim miejscem do zatrzymania.
    /// </para>
    /// </remarks>
    private async Task PrzejscieDniaAsync()
    {
        var dzis = _clock.Today;

        // Pierwsze sprawdzenie tylko zapamiętuje dzień. Start nadrabia przejście własną
        // drogą (CatchUpAsync), więc robienie tego drugi raz byłoby pracą bez skutku.
        if (_dzien is null)
        {
            _dzien = dzis;
            return;
        }

        if (_dzien == dzis)
        {
            return;
        }

        _dzien = dzis;

        await _przejscieDnia.RunAsync();
        await _focus.ExpireAsync();

        await _dziennik.RecordAsync("Przejście dnia", $"nowy dzień: {dzis:yyyy-MM-dd}");
        await ReloadAsync();
    }

    /// <summary>
    /// Odbiera to, co uzbierała usługa przypomnień przy starcie.
    /// </summary>
    /// <remarks>
    /// Przypomnienia odpalają się w <c>PrepareAsync</c>, zanim okno ma cokolwiek
    /// wczytane. Zbieranie ich do odebrania, zamiast pokazywania od razu, jest tym,
    /// co pozwala im przetrwać tę chwilę.
    /// </remarks>
    private void CollectReminders()
    {
        foreach (var przypomnienie in _notifier.Drain())
        {
            Reminders.Add(przypomnienie);
        }

        OnPropertyChanged(nameof(HasReminders));
    }

    [RelayCommand]
    private void DismissReminders()
    {
        Reminders.Clear();
        OnPropertyChanged(nameof(HasReminders));
    }

    /// <summary>
    /// Robota wywołana zdarzeniem, z której wyjątek ma dokąd trafić.
    /// </summary>
    /// <remarks>
    /// Wszystkie te zdarzenia były dotąd obsługiwane wyrażeniami <c>async</c> bez
    /// wartości zwracanej. Taki kod nie ma komu oddać wyjątku: awaria odświeżania
    /// ekranu po zapisie kończyła się tym, że ekran po prostu **nie odświeżał się**,
    /// bez śladu w oknie i bez śladu nigdzie indziej. Wyglądało to dokładnie tak,
    /// jakby zapis się nie udał — a zapis się udawał.
    /// </remarks>
    private void Bezpiecznie(string co, Func<Task> praca) => _ = Probuj(co, praca);

    private async Task Probuj(string co, Func<Task> praca)
    {
        try
        {
            await praca();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await _dziennik.RecordAsync(
                co, "nie udało się", ActivityLevel.Problem, $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Przebieg w tle przy starcie. Cicho, ale nie po cichu: wynik idzie do dziennika.
    /// </summary>
    /// <remarks>
    /// Wyłącznie wtedy, gdy jest czym: bez poświadczeń i bez zapisanego żetonu
    /// logowanie chciałoby otworzyć przeglądarkę — przy starcie aplikacji byłoby to
    /// okno wyskakujące bez powodu, zanim zdążysz cokolwiek zrobić.
    /// </remarks>
    private Task SynchronizujCichoAsync() => PrzebiegAsync("Synchronizacja przy starcie");

    /// <summary>
    /// Jeden przebieg do Dysku wywołany nie przez rękę.
    /// </summary>
    /// <param name="co">Nazwa do dziennika — mówi, co go wywołało.</param>
    /// <param name="cicha">
    /// Czy pomijać wpis w dzienniku, gdy przebieg się udał i nic nie przeniósł.
    /// Przebieg co pięć minut to blisko trzysta wpisów na dobę, a dziennik trzyma
    /// pięćset — bez tego zjadłby sam siebie i nie byłoby w nim widać niczego innego.
    /// Awarie i przebiegi, które coś przeniosły, zostają zawsze.
    /// </param>
    private async Task PrzebiegAsync(string co, bool cicha = false)
    {
        if (!_dysk.HasCredentials || !Directory.Exists(_dysk.TokenFolder))
        {
            return;
        }

        _trwaSynchronizacja = true;

        SyncOutcome wynik;

        try
        {
            wynik = await _dysk.SyncAsync();
        }
        finally
        {
            // Także po wywrotce: inaczej jedna awaria zatrzymywałaby automat na zawsze.
            // Przerwa liczona od końca przebiegu, nie od początku — długi przebieg nie
            // ma się kończyć w chwili, w której należy się następny.
            _trwaSynchronizacja = false;
            _ostatniaSynchronizacja = _clock.Now;
        }

        var niemo = cicha && wynik is { Ok: true, Sent: 0, Applied: 0 };

        if (!niemo)
        {
            await _dziennik.RecordAsync(
                co,
                wynik.Ok ? $"wysłane {wynik.Sent}, przyjęte {wynik.Applied}" : wynik.Message,
                wynik.Ok ? ActivityLevel.Ok : ActivityLevel.Problem);
        }

        // Przeliczenie tylko wtedy, gdy coś przyszło: przerysowanie ekranu pod ręką,
        // która właśnie coś na nim robi, jest kosztem bez pożytku.
        if (wynik is { Ok: true, Applied: > 0 })
        {
            // Przypomnienia sprawdzane od razu, nie dopiero za minutę. Przyniesione
            // przez synchronizację bywa już zaległe — zadanie zmienione na drugim
            // urządzeniu przychodzi tu z godziną, która zdążyła minąć.
            if (await _przypomnienia.RunAsync() > 0)
            {
                CollectReminders();
            }

            await ReloadAsync();
        }
    }

    /// <summary>
    /// Przeliczenie ekranu po zmianie danych — po zapisie, który mógł zmienić przynależność.
    /// </summary>
    /// <remarks>
    /// Skrzynka i bieżący ekran osobno, każde z własnym zabezpieczeniem. Do dziś szły
    /// jednym ciągiem: wywrotka przy liczeniu skrzynki zabierała ze sobą odświeżenie
    /// ekranu, na którym akurat się było, i nic się nie przerysowywało — po zapisie,
    /// który się udał. Jedno popsute miejsce ma psuć jedno miejsce.
    /// </remarks>
    private async Task ReloadAsync()
    {
        await Probuj("Ekran: przeliczenie skrzynki", RefreshInboxAsync);

        var zadanie = Current switch
        {
            Screen.Today => ShowTodayAsync(),
            Screen.Now => Now.LoadAsync(),
            Screen.Next => ShowNextAsync(),
            Screen.Someday => ShowSomedayAsync(),
            Screen.Archive => ShowArchiveAsync(),
            Screen.Projects => ShowProjectsAsync(),
            Screen.Waiting => ShowWaitingAsync(),
            Screen.Calendar => Calendar.LoadAsync(),
            Screen.Notes => Notes.LoadAsync(),
            Screen.Journal => Journal.LoadAsync(),
            Screen.Filters => Filters.RunCommand.ExecuteAsync(null),
            _ => Task.CompletedTask,
        };

        await Probuj($"Ekran: przeliczenie ({Current})", () => zadanie);
    }

    /// <summary>
    /// Czy lista kandydatów na dziś ma sięgać także do „kiedyś-może".
    /// </summary>
    /// <remarks>
    /// Zadanie, któremu dopisano długość i poziom sił, jest już opisane tak, jak opisuje
    /// się rzeczy do zrobienia — a mimo to nie dawało się go wybrać na dziś inaczej niż
    /// z menu podręcznego na ekranie „Kiedyś". Przełącznik przenosi tę możliwość tam,
    /// gdzie się o niej myśli, nie zacierając przy tym granicy: domyślnie wyłączony.
    /// </remarks>
    [ObservableProperty]
    public partial bool AlsoSomeday { get; set; }

    partial void OnAlsoSomedayChanged(bool value) =>
        Bezpiecznie("Dzisiaj: kandydaci", RefreshFocusAsync);

    /// <summary>
    /// Piątka na dziś i kandydaci do niej. Kandydaci to „Następne" oraz zaplanowane
    /// na dziś albo wcześniej (spec 8.6) — nie wszystko, co ma dzisiejszą datę.
    /// </summary>
    private async Task RefreshFocusAsync()
    {
        var dzis = Today();

        FocusItems.Clear();
        FocusOffGrid.Clear();

        foreach (var zadanie in await _focus.TodayAsync())
        {
            FocusItems.Add(zadanie);

            // Bez godziny nie ma bloku na siatce, więc pasek nad kalendarzem jest
            // jedynym miejscem, gdzie to zadanie w ogóle widać.
            if (zadanie.DoTime is null && zadanie.State != TaskState.Done)
            {
                FocusOffGrid.Add(zadanie);
            }
        }

        var wybrane = FocusItems.Select(t => t.Id).ToHashSet();
        var kandydaci = (await _tasks.ByStateAsync(TaskState.Next))
            .Concat(await _tasks.ByStateAsync(TaskState.Scheduled))
            .Where(t => t.State == TaskState.Next || t.DoDate <= dzis)
            .ToList();

        // „Kiedyś-może" tylko na wyraźne życzenie. Wzięcie stamtąd czegoś na dziś
        // wyjmuje to z tego stanu — więc lista kandydatów jest właściwym miejscem
        // na taką decyzję, ale nie na stałe: inaczej wszystko, co się kiedykolwiek
        // odłożyło, wracałoby tu codziennie i odkładanie przestałoby cokolwiek dawać.
        if (AlsoSomeday)
        {
            kandydaci.AddRange((await _tasks.ByStateAsync(TaskState.Someday))
                .Where(t => t.DeferUntil is null || t.DeferUntil <= dzis));
        }

        kandydaci = kandydaci.Where(t => !wybrane.Contains(t.Id)).ToList();

        FocusCandidates.Clear();
        foreach (var zadanie in kandydaci)
        {
            FocusCandidates.Add(TaskRow.From(zadanie, dzis));
        }

        OnPropertyChanged(nameof(FocusIsFull));
        OnPropertyChanged(nameof(FocusCount));
    }

    /// <summary>Wybór zadania na dziś. Przy pełnej piątce pyta, które schodzi.</summary>
    [RelayCommand]
    private async Task FocusAsync(TaskRow? row)
    {
        if (row is null)
        {
            return;
        }

        var wynik = await _focus.TryFocusAsync(row.Task.Id);

        if (!wynik.Accepted)
        {
            PendingFocus = row.Task;
            await RefreshFocusAsync();
            return;
        }

        PendingFocus = null;
        await RefreshFocusAsync();
    }

    /// <summary>
    /// Zdjęcie z wyboru. Jeśli coś czekało na slot, wchodzi na zwolnione miejsce.
    /// </summary>
    [RelayCommand]
    private async Task UnfocusAsync(TaskItem? task)
    {
        if (task is null)
        {
            return;
        }

        await _focus.UnfocusAsync(task.Id);

        if (PendingFocus is { } czekajace)
        {
            await _focus.TryFocusAsync(czekajace.Id);
            PendingFocus = null;
        }

        await RefreshFocusAsync();
    }

    [RelayCommand]
    private void CancelPendingFocus() => PendingFocus = null;

    /// <summary>Otwarcie szczegółu — jedyne wejście do terminu, przypomnienia i rytmu.</summary>
    [RelayCommand]
    private async Task OpenAsync(TaskRow? row)
    {
        if (row is not null)
        {
            await Detail.LoadAsync(row.Task);
        }
    }

    /// <summary>
    /// Wrzut. Dostępny z każdego ekranu poza przetwarzaniem — myśl przychodzi wtedy,
    /// kiedy przychodzi, a nie wtedy, gdy akurat jesteś w skrzynce.
    /// </summary>
    [RelayCommand]
    private async Task CaptureAsync()
    {
        if (string.IsNullOrWhiteSpace(CaptureText))
        {
            return;
        }

        await _inbox.CaptureAsync(CaptureText);
        CaptureText = string.Empty;
        await RefreshInboxAsync();
    }

    [RelayCommand]
    private Task ShowInboxAsync()
    {
        Current = Screen.Inbox;
        return RefreshInboxAsync();
    }

    [RelayCommand]
    private async Task ShowClarifyAsync()
    {
        if (InboxCount == 0)
        {
            return;
        }

        Current = Screen.Clarify;
        await Clarify.LoadAsync();
    }

    [RelayCommand]
    private async Task ShowNextAsync()
    {
        Current = Screen.Next;
        await Fill(NextActions, _tasks.ByStateAsync(TaskState.Next));
    }

    [RelayCommand]
    private async Task ShowProjectsAsync()
    {
        Current = Screen.Projects;
        ProjectRows.Clear();

        var zablokowane = (await _queries.BlockedProjectsAsync())
            .Select(p => p.ProjectId)
            .ToHashSet();

        var rows = ProjectTree.Build(
            await _areas.ActiveAsync(), await _projects.ActiveAsync(), zablokowane);

        // Równowaga obszarów wpisana w te same wiersze, a nie na osobnym ekranie.
        // Dwa ekrany na te same obiekty dawały różne możliwości w każdym z nich:
        // tu barwa i usunięcie, tam zakładanie, a nazwa tylko tam. Jeden ekran, jeden
        // zestaw czynności — a liczby są cechą obszaru, więc stoją przy nim.
        var rownowaga = (await _queries.BalanceAsync(Today()))
            .ToDictionary(w => w.AreaId);

        foreach (var row in rows)
        {
            ProjectRows.Add(new ProjectTreeRow(
                row,
                row.IsArea && rownowaga.TryGetValue(row.Id, out var w) ? w : null));
        }
    }

    [RelayCommand]
    private async Task ShowWaitingAsync()
    {
        Current = Screen.Waiting;
        WaitingItems.Clear();

        foreach (var pozycja in await _queries.WaitingAsync(Today()))
        {
            WaitingItems.Add(pozycja);
        }
    }

    /// <summary>Notatki — materiał referencyjny, którego nie trzeba robić.</summary>
    [RelayCommand]
    private async Task ShowNotesAsync()
    {
        Current = Screen.Notes;
        await Notes.LoadAsync();
    }

    /// <summary>Ustawienia: motyw, strefa, kopia zapasowa (spec 11, 12).</summary>
    [RelayCommand]
    private void ShowSettings()
    {
        Settings.Load();
        Current = Screen.Settings;
    }

    /// <summary>
    /// Co się działo (spec 12).
    /// </summary>
    /// <remarks>
    /// Osobny ekran, a nie kawałek Ustawień: zagląda się tu wtedy, gdy coś nie wyszło,
    /// i wtedy nie chce się przewijać pola na sekrety Google, żeby dojść do odpowiedzi.
    /// </remarks>
    [RelayCommand]
    private async Task ShowJournalAsync()
    {
        Current = Screen.Journal;
        await Journal.LoadAsync();
    }

    /// <summary>Własne widoki — konstruktor warunków i Ulubione (spec 11.5).</summary>
    [RelayCommand]
    private async Task ShowFiltersAsync()
    {
        Current = Screen.Filters;
        await Filters.LoadAsync();
    }

    /// <summary>Kalendarz godzinowy — wydarzenia i zadania na jednej siatce.</summary>
    [RelayCommand]
    private async Task ShowCalendarAsync()
    {
        Current = Screen.Calendar;
        await Calendar.LoadAsync();
    }

    /// <summary>Kreator przeglądu. Wznawia niedokończony albo zakłada nowy.</summary>
    [RelayCommand]
    private async Task ShowReviewAsync()
    {
        Current = Screen.Review;
        await Review.OpenAsync();
    }

    /// <summary>Widok „Teraz" — trzy do pięciu pozycji po wyborze czasu i energii.</summary>
    [RelayCommand]
    private async Task ShowNowAsync()
    {
        Current = Screen.Now;
        await Now.LoadAsync();
    }

    [RelayCommand]
    private async Task ShowTodayAsync()
    {
        Current = Screen.Today;
        var dzis = Today();
        await Fill(TodayItems, _tasks.TodayAsync(dzis));
        OnPropertyChanged(nameof(HasToday));
        await RefreshFocusAsync();

        // Ponaglenia (N3) i projekty zablokowane (N1) idą na „Dzisiaj", bo są sprawami
        // na dziś. Cisza obszarów (N10) **nigdy tu nie trafia** — to nie jest sprawa na
        // dziś, a codzienne przypominanie o niej zamieniłoby ją w szum (spec 6).
        Nudges.Clear();
        foreach (var pozycja in (await _queries.WaitingAsync(dzis)).Where(w => w.NeedsNudge))
        {
            Nudges.Add(pozycja);
        }

        BlockedProjects.Clear();
        foreach (var projekt in await _queries.BlockedProjectsAsync())
        {
            BlockedProjects.Add(projekt);
        }

        OnPropertyChanged(nameof(HasNudges));
        OnPropertyChanged(nameof(HasBlocked));
    }

    [RelayCommand]
    private async Task ShowSomedayAsync()
    {
        Current = Screen.Someday;
        await FillPlain(SomedayItems, _tasks.ByStateAsync(TaskState.Someday));
    }

    [RelayCommand]
    private async Task ShowArchiveAsync()
    {
        Current = Screen.Archive;
        await FillPlain(ArchiveItems, _tasks.ArchiveAsync(limit: 200));
    }

    private DateOnly Today() => _clock.Today;

    /// <summary>Dzisiaj w strefie z ustawień — dla menu, które samo zegara nie ma.</summary>
    public DateOnly Dzisiaj => _clock.Today;

    private async Task Fill(ObservableCollection<TaskRow> target, Task<IReadOnlyList<TaskItem>> source)
    {
        var items = await source;
        var dzis = Today();

        target.Clear();
        foreach (var item in items)
        {
            target.Add(TaskRow.From(item, dzis));
        }
    }

    private static async Task FillPlain(
        ObservableCollection<TaskItem> target, Task<IReadOnlyList<TaskItem>> source)
    {
        var items = await source;
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    /// <summary>
    /// Czynności menu podręcznego. Każda bierze samo zadanie, bo wiersze list różnią się
    /// typem, a menu ma być jedno dla wszystkich.
    /// </summary>
    [RelayCommand]
    public async Task OpenTaskAsync(TaskItem? task)
    {
        if (task is not null)
        {
            await Detail.LoadAsync(task);
        }
    }

    [RelayCommand]
    public async Task FocusTaskAsync(TaskItem? task)
    {
        if (task is null)
        {
            return;
        }

        var wynik = await _focus.TryFocusAsync(task.Id);

        PendingFocus = wynik.Accepted ? null : task;

        // Odmowa musi być słychać z każdego ekranu. Ramka z pytaniem „co schodzi"
        // stoi tylko na „Dzisiaj", więc wzięcie na dziś z „Kiedyś" przy pełnej piątce
        // nie robiło nic i nie mówiło dlaczego.
        Notice = wynik.Accepted
            ? string.Empty
            : $"Pięć zadań na dziś już jest. Zdejmij coś na ekranie „Dzisiaj”, "
                + $"żeby zmieścić „{task.Title}”.";

        // Przeliczenie **całego** ekranu, nie samej piątki. Bez tego lista, z której
        // zadanie właśnie wzięto, pokazywała je dalej w starym miejscu — wyglądało to
        // tak, jakby kliknięcie nic nie zrobiło.
        await ReloadAsync();
        await RefreshFocusAsync();
    }

    /// <summary>Oszacowanie i siła z menu podręcznego — bez otwierania szczegółu.</summary>
    /// <remarks>
    /// Bez tych dwóch pól zadanie nigdy nie pojawi się w „Teraz": ten ekran pyta
    /// „ile mam czasu i sił", więc zadanie, które na to nie odpowiada, nie ma jak
    /// zostać wybrane. Do dziś dawało się je wpisać wyłącznie przy przetwarzaniu
    /// skrzynki albo w szczegółach, czyli nie tam, gdzie się o nich myśli.
    /// </remarks>
    public async Task SetEstimateAsync(TaskItem task, int? minutes)
    {
        ArgumentNullException.ThrowIfNull(task);

        await _edit.SetEstimateAsync(task.Id, minutes, task.Energy);
        await ReloadAsync();
    }

    public async Task SetEnergyAsync(TaskItem task, Energy energy)
    {
        ArgumentNullException.ThrowIfNull(task);

        await _edit.SetEstimateAsync(task.Id, task.EstimatedMinutes, energy);
        await ReloadAsync();
    }

    /// <summary>Przełożenie o dobę. Godzina zostaje — przesunięcie dnia jej nie dotyczy.</summary>
    [RelayCommand]
    private async Task PostponeTaskAsync(TaskItem? task)
    {
        if (task is null)
        {
            return;
        }

        var skad = task.DoDate ?? Today();

        await _edit.RescheduleAsync(task.Id, skad.AddDays(1), task.DoTime);
        await ReloadAsync();
    }

    [RelayCommand]
    public async Task TrashTaskAsync(TaskItem? task)
    {
        if (task is not null)
        {
            await _inbox.TrashAsync(task.Id);
            await ReloadAsync();
        }
    }

    /// <summary>
    /// Czynności menu podręcznego, wołane wprost z okna.
    /// </summary>
    /// <remarks>
    /// Metody, nie polecenia: menu składa się w kodzie okna, a każda pozycja niesie
    /// własny argument — rodzaj rytmu, wagę, projekt. Polecenie przyjmuje jeden
    /// parametr, więc byłoby ich tyle, ile pozycji, i każde z własną obsługą pustki.
    /// </remarks>
    public async Task SetDateAsync(TaskItem task, DateOnly? day)
    {
        if (day is { } dzien)
        {
            await _edit.RescheduleAsync(task.Id, dzien, task.DoTime);
        }
        else
        {
            await _edit.ApplyAsync(task.Id, Bez(task));
        }

        await ReloadAsync();
    }

    /// <summary>Ten sam zestaw pól, tylko bez dnia wykonania — reszta ma zostać.</summary>
    private static TaskEdit Bez(TaskItem task) =>
        new(task.Title, task.Note, null, task.Deadline, task.ReminderAt, task.Recurrence,
            task.Priority, task.EstimatedMinutes, task.Energy, task.AreaId, null);

    public async Task SetPriorityAsync(TaskItem task, Priority priority)
    {
        await _edit.SetPriorityAsync(task.Id, priority);
        await ReloadAsync();
    }

    public async Task SetRecurrenceAsync(TaskItem task, RecurrenceKind? kind)
    {
        await _edit.SetRecurrenceAsync(task.Id, kind);
        await ReloadAsync();
    }

    public async Task SetProjectAsync(TaskItem task, Guid? projectId)
    {
        await _edit.SetProjectAsync(task.Id, projectId);
        await ReloadAsync();
    }

    /// <summary>
    /// Kalendarze, do których da się udostępnić zadanie. Puste, gdy żadnego nie ma.
    /// </summary>
    public async Task<IReadOnlyList<CalendarSource>> WritableCalendarsAsync() =>
        (await _kalendarze.SourcesAsync())
            .Where(z => _kalendarze.CanWrite(z.Kind))
            .ToList();

    /// <summary>Kalendarz, w którym zadania lądują domyślnie.</summary>
    public Guid? MainCalendarId => _odbicie.MainCalendarId;

    /// <summary>
    /// Przeniesienie zadania do wybranego kalendarza.
    /// </summary>
    /// <remarks>
    /// Po to, żeby ktoś bez Marshala widział u siebie to, co go dotyczy. Pojedyncze
    /// zadania, nie całe obszary: obszar rodzinny mieści i „odebrać dziecko",
    /// i „kupić prezent", a widzieć je mają różne osoby. Przeniesienie, nie dołożenie —
    /// zadanie stoi w jednym kalendarzu naraz.
    /// </remarks>
    public async Task ShareTaskAsync(TaskItem task, Guid calendarId)
    {
        ArgumentNullException.ThrowIfNull(task);

        Notice = await _odbicie.ShareAsync(task.Id, calendarId) ?? string.Empty;
        await ReloadAsync();
    }

    public async Task UnshareTaskAsync(TaskItem task)
    {
        ArgumentNullException.ThrowIfNull(task);

        await _odbicie.UnshareAsync(task.Id);
        Notice = string.Empty;
        await ReloadAsync();
    }

    /// <summary>Nazwa zakładanego obszaru — pole na ekranie „Obszary i projekty".</summary>
    [ObservableProperty]
    public partial string NewAreaName { get; set; } = string.Empty;

    /// <summary>
    /// Nowy obszar.
    /// </summary>
    /// <remarks>
    /// Obszarów nie dawało się dotąd ani założyć, ani usunąć — była tylko dziesiątka
    /// zasiana przy pierwszym uruchomieniu. Podział odpowiedzialności jest rzeczą
    /// osobistą i zmienia się w życiu; lista, której nie da się ruszyć, zmusza do
    /// wciskania własnego życia w cudzy podział.
    /// </remarks>
    [RelayCommand]
    public async Task AddAreaAsync()
    {
        var nazwa = NewAreaName.Trim();

        if (nazwa.Length == 0)
        {
            return;
        }

        await _szkielet.AddAreaAsync(nazwa);
        NewAreaName = string.Empty;
        Notice = string.Empty;
        await ShowProjectsAsync();
    }

    /// <summary>
    /// Nowy projekt w obszarze albo podprojekt pod projektem.
    /// </summary>
    /// <remarks>
    /// Rodzicem bywa obszar albo projekt i to jest jedno wejście na oba, bo z punktu
    /// widzenia ręki to ta sama czynność: „tutaj ma powstać nowy".
    /// </remarks>
    public async Task AddProjectAsync(ProjectTreeRow parent, string outcome)
    {
        ArgumentNullException.ThrowIfNull(parent);

        if (string.IsNullOrWhiteSpace(outcome))
        {
            return;
        }

        await _szkielet.AddProjectAsync(parent.Id, outcome.Trim());
        Notice = string.Empty;
        await ShowProjectsAsync();
    }

    /// <summary>Nowa nazwa wiersza — obszaru albo projektu.</summary>
    public async Task RenameRowAsync(ProjectTreeRow row, string name)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (row.IsArea)
        {
            await _szkielet.RenameAreaAsync(row.Id, name.Trim());
        }
        else
        {
            await _szkielet.RenameProjectAsync(row.Id, name.Trim());
        }

        await ShowProjectsAsync();
    }

    /// <summary>Usunięcie wiersza — obszaru albo projektu, i tylko pustego.</summary>
    public async Task DeleteRowAsync(ProjectTreeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        Notice = (row.IsArea
            ? await _szkielet.DeleteAreaAsync(row.Id)
            : await _szkielet.DeleteProjectAsync(row.Id)) ?? string.Empty;

        await ShowProjectsAsync();
    }

    /// <summary>Czy ten wiersz da się usunąć — menu ma nie proponować rzeczy bez skutku.</summary>
    public Task<string?> WhyCannotDeleteRowAsync(ProjectTreeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return row.IsArea
            ? _szkielet.WhyCannotDeleteAreaAsync(row.Id)
            : _szkielet.WhyCannotDeleteAsync(row.Id);
    }

    /// <summary>Barwa wiersza z ekranu „Projekty" — obszaru albo projektu.</summary>
    public async Task SetRowColorAsync(ProjectTreeRow row, string? color)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.IsArea)
        {
            await _szkielet.SetAreaColorAsync(row.Id, color);
        }
        else
        {
            await _szkielet.SetProjectColorAsync(row.Id, color);
        }

        Notice = string.Empty;
        await ShowProjectsAsync();
    }

    public async Task ToNoteAsync(TaskItem task)
    {
        await _notes.ConvertToNoteAsync(task.Id);
        await ReloadAsync();
    }

    /// <summary>Pokazanie zadania na siatce — kalendarz przeskakuje na jego dzień.</summary>
    public async Task ShowInCalendarAsync(TaskItem task)
    {
        if (task.DoDate is { } dzien)
        {
            Calendar.Anchor = dzien;
        }

        await ShowCalendarAsync();
    }

    /// <summary>Projekty do wyboru w menu. Same czynne — zamkniętego nie ma po co proponować.</summary>
    public async Task<IReadOnlyList<Project>> ActiveProjectsAsync() =>
        (await _projects.AllAsync())
            .Where(p => !p.Deleted && p.State == ProjectState.Active)
            .OrderBy(p => p.Outcome, StringComparer.CurrentCulture)
            .ToList();

    /// <summary>Zadanie po identyfikatorze — dla bloków siatki, które niosą sam identyfikator.</summary>
    public Task<TaskItem?> FindTaskAsync(Guid id) => _tasks.FindAsync(id);

    /// <summary>Odhaczenie zadania podanego wprost — piątka „Na dziś" niesie same zadania.</summary>
    [RelayCommand]
    public async Task CompleteTaskAsync(TaskItem? task)
    {
        if (task is not null)
        {
            await _edit.CompleteAsync(task.Id);
            await ReloadAsync();
        }
    }

    /// <summary>
    /// Zdjęcie ptaszka — zadanie znowu jest do zrobienia.
    /// </summary>
    /// <remarks>
    /// Odhaczenie kosztuje jedno kliknięcie, więc omyłkowe zdarza się tak samo łatwo
    /// jak właściwe. Cofnięcie musi kosztować tyle samo; do dziś nie dało się go zrobić
    /// z żadnego ekranu, mimo że model to umiał.
    /// </remarks>
    [RelayCommand]
    public async Task ReopenTaskAsync(TaskItem? task)
    {
        if (task is not null)
        {
            await _edit.ReopenAsync(task.Id);
            await ReloadAsync();
        }
    }

    /// <summary>
    /// Odhaczenie z listy. Idzie przez szczegół, bo zadanie powtarzalne musi przy
    /// okazji zrodzić kolejne wystąpienie (8.4) — a z listy tego nie widać.
    /// </summary>
    [RelayCommand]
    private async Task CompleteAsync(TaskRow? row)
    {
        if (row is null)
        {
            return;
        }

        await _edit.CompleteAsync(row.Task.Id);
        await ReloadAsync();
    }

    private async Task RefreshInboxAsync()
    {
        InboxItems.Clear();
        foreach (var item in await _inbox.ListAsync())
        {
            InboxItems.Add(item);
        }

        InboxCount = InboxItems.Count;
    }
}

using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application;
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
    private readonly IActivityLog _activity;
    private readonly NoteService _notes;
    private readonly StructureEditService _shell;
    private readonly TaskMirror _mirror;
    private readonly CalendarSyncService _calendars;
    private readonly ReminderService _reminders;

    private readonly GoogleSyncService _drive;

    private readonly DayRolloverService _dayRollover;

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
    private int _change;

    /// <summary>Kiedy ostatni przebieg się skończył. Do odmierzania przerwy.</summary>
    private DateTimeOffset _lastSync = DateTimeOffset.MinValue;

    /// <summary>Czy przebieg właśnie trwa.</summary>
    /// <remarks>
    /// Przebieg bywa dłuższy od minuty — sieć, logowanie, kilka odcinków — a minutnik
    /// nie czeka. Bez tej blokady wolna synchronizacja prosiłaby sama siebie o drugą.
    /// </remarks>
    private bool _syncing;

    /// <summary>Co ile sprawdzać Dysk, gdy nic się nie zmieniło.</summary>
    /// <remarks>
    /// <para>
    /// Po to, żeby zobaczyć **cudze** zmiany: to, co dopisał telefon, przychodzi tylko
    /// przez odczyt.
    /// </para>
    /// <para>
    /// Minuta, nie pięć. Pięć brało się z rachunku zapytań do Dysku, ale rachunek był
    /// nie ten: liczyłem koszt przebiegu, a kosztem jest czekanie. Zadanie zapisane
    /// na telefonie nie pojawiało się na komputerze przez kilka minut, więc obie
    /// aplikacje pokazywały różne rzeczy — a to jest dokładnie ta sytuacja, w której
    /// przestaje się im ufać i sprawdza wszystko dwa razy. Przebieg bez zmian to jedno
    /// zapytanie i żadnego wpisu w dzienniku.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan Gap = TimeSpan.FromMinutes(1);

    /// <summary>Ile czekać z wysyłką po zapisie.</summary>
    /// <remarks>
    /// Nie zero: zapis w szczegółach zadania to zwykle kilka zapisów pod rząd, a każdy
    /// z osobna znaczyłby osobny przebieg po sieci. Pięć sekund zbiera je w jeden
    /// i nadal jest poniżej progu, przy którym człowiek zaczyna patrzeć na drugie
    /// urządzenie i zastanawiać się, czy zadziałało.
    /// </remarks>
    private static readonly TimeSpan SendDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Dzień, na którym stanęło okno. Do wykrycia północy przy otwartej aplikacji.
    /// </summary>
    /// <remarks>
    /// Pusty do pierwszego sprawdzenia, a nie ustawiony w konstruktorze. Zegar liczy
    /// dzień w strefie z ustawień, czyli **czyta bazę** — a składanie zależności musi
    /// się obejść bez bazy, bo dzieje się przed migracjami. Pilnuje tego test
    /// „złożenie zależności nie sięga do bazy".
    /// </remarks>
    private DateOnly? _day;

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
        IActivityLog activity,
        NoteService noteService,
        StructureEditService shell,
        TaskMirror mirror,
        CalendarSyncService calendars,
        ReminderService reminders,
        GoogleSyncService drive,
        DayRolloverService dayRollover,
        IWriteSignal signal)
    {
        _inbox = inbox;
        _shell = shell;
        _mirror = mirror;
        _calendars = calendars;
        _reminders = reminders;
        _drive = drive;
        _dayRollover = dayRollover;

        // Znak z jednostki pracy przychodzi z cudzego wątku, więc wolno tu zrobić
        // dokładnie dwie rzeczy: odłożyć notatkę i poprosić wątek okna o wysyłkę.
        // Sam przebieg rusza stamtąd, bo kończy się przerysowaniem list.
        signal.Saved += () =>
        {
            Interlocked.Exchange(ref _change, 1);
            AskForSend();
            AskForRecompute();
        };
        _tasks = tasks;
        _projects = projects;
        _areas = areas;
        _clock = clock;
        _edit = edit;
        _focus = focus;
        _queries = queries;
        _notifier = notifier;
        _activity = activity;
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
        Clarify.Emptied += (_, _) => Safely("Skrzynka opróżniona", ShowInboxAsync);

        // Po zapisie szczegółu ekran musi się przeliczyć: zmiana terminu albo dnia
        // wykonania potrafi przenieść zadanie na inną listę niż ta, z której je otwarto.
        Detail.Saved += (_, _) => Safely("Ekran: odświeżenie po zapisie", ReloadAsync);

        // „Pokaż osobie" działa na wydarzeniu w Google, a zadanie z godziną ma tam swoje
        // odbicie — czyli dokładnie takie wydarzenie. Kalendarz zna jedyną drogę do Google
        // i nie ma jej dublować, a nakładka szczegółu nie zna jej wcale; kto jest otwarty,
        // wie tylko okno, więc podstawia je tutaj.
        Detail.Calendar = Calendar;

        Detail.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(Detail.IsOpen) or nameof(Detail.CanShowToPerson)))
            {
                return;
            }

            Calendar.OpenedTaskEvent = Detail is
            {
                IsOpen: true,
                CanShowToPerson: true,
                SharedCalendarId: { } source,
                SharedEventId: { } entry,
            }
                ? (source, entry, Detail.Title)
                : null;
        };

        // Przegląd zmienia stan zadań i projektów, więc ekran pod spodem musi się
        // przeliczyć — także liczniki niezmienników w „Dzisiaj".
        Review.Changed += (_, _) => Safely("Ekran: odświeżenie po przeglądzie", ReloadAsync);

        // Krok skrzynki prowadzi do drzewka przetwarzania. Przegląd zostaje otwarty —
        // wznowi się na tym samym kroku, bo jego stan siedzi w bazie, a nie w ekranie.
        Review.InboxRequested += (_, _) => Safely("Przegląd: skrzynka", ShowClarifyAsync);
        Now.Changed += (_, _) => Safely("Teraz: odświeżenie piątki", RefreshFocusAsync);

        // Wgranie kopii zmienia wszystko naraz, więc ekran pod spodem musi się
        // przeliczyć — inaczej lista pokazuje stan sprzed wczytania, wyglądając
        // na aktualną.
        Settings.Imported += (_, _) => Safely("Ekran: odświeżenie po wczytaniu kopii", ReloadAsync);

        // Kliknięcie w blok na siatce otwiera tę samą nakładkę, co kliknięcie na liście.
        Calendar.NewTaskRequested += (day, time) =>
            Safely("Kalendarz: nowe zadanie", () => Detail.NewAsync(day, time));

        Calendar.TaskRequested += id => Safely("Kalendarz: otwarcie zadania", async () =>
        {
            if (await _tasks.FindAsync(id) is { } task)
            {
                await Detail.LoadAsync(task);
            }
        });
    }

    /// <summary>
    /// Wejście z zewnątrz okna: kalendarz, a na nim wskazane zadanie.
    /// </summary>
    /// <remarks>
    /// Kalendarz najpierw i bezwarunkowo, szczegół dopiero po nim. Odwrotna kolejność
    /// otwierałaby szczegół nad ekranem, na którym akurat się stało — a po jego
    /// zamknięciu zostawałby ten ekran, nie kalendarz. Z widgetu przychodzi się
    /// **na kalendarz**, nawet gdy zadania już nie ma.
    /// </remarks>
    public void ShowCalendar(CalendarRequest? what)
    {
        Safely("Kalendarz: wejście z widgetu", async () =>
        {
            await ShowCalendarAsync();

            // Wydarzenie z podłączonego kalendarza nie ma u nas identyfikatora zadania
            // i dlatego dotąd kończyło tę drogę na samym kalendarzu: kafelek nie miał
            // czego przekazać, więc wchodziło się „gdzieś w okolice" i dalej trzeba
            // było szukać wzrokiem. Ma własną kartę i własną drogę do niej.
            if (what is { IsEvent: true, Source: { } source, Event: { } external, Day: { } shown })
            {
                await Calendar.ShowEventAsync(source, external, shown);
                return;
            }

            if (what?.Task is not { } id)
            {
                return;
            }

            if (await _tasks.FindAsync(id) is not { } found)
            {
                // Dotknięto wpisu, którego już nie ma — kafelek pokazywał zapamiętany
                // obrazek sprzed zmiany. Kalendarz zostaje, bo to nadal sensowna
                // odpowiedź, ale cisza w tym miejscu wyglądałaby identycznie jak
                // zgubiony identyfikator, a to zupełnie inna usterka.
                await _activity.RecordAsync(
                    "Kalendarz: wejście z widgetu",
                    "zadania już nie ma",
                    ActivityLevel.Problem,
                    $"Identyfikator {id}.");

                return;
            }

            // Na dzień zadania, a nie na ten, na którym kalendarz akurat stał. Kafelek
            // ma strzałki i pokazuje dowolny tydzień, a zadanie bywa zaległe — dotknięcie
            // wpisu z przyszłego wtorku otwierało więc kartę nad **tym** tygodniem, a po
            // jej zamknięciu zostawał widok, który z dotkniętym zadaniem nie miał nic
            // wspólnego. Dzień wykonania, a gdy go nie ma — termin: blok zadania stoi
            // na siatce w jednym i drugim.
            //
            // Osobno od karty i pod własnym zabezpieczeniem: przesunięcie kalendarza
            // jest wygodą, karta jest tym, po co się tu przyszło. Nieudane przesunięcie
            // zabierało kartę razem ze sobą, bo oba stały pod jednym „try".
            if ((found.DoDate ?? found.Deadline) is { } day)
            {
                await Try("Kalendarz: dzień zadania z widgetu", () => Calendar.ShowAsync(day, found.DoTime));
            }

            await Detail.LoadAsync(found);
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

    /// <summary>
    /// Ślad odwiedzonych ekranów — do cofania.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lista, nie stos, wyłącznie po to, żeby dało się jej uciąć początek. Bez tego
    /// pół godziny klikania po nawigacji znaczyłoby pół godziny cofania — a nikt nie
    /// cofa się dwudziesty raz, żeby wyjść z aplikacji.
    /// </para>
    /// <para>
    /// <b>Lokalny, nie zapisywany.</b> Ślad opisuje tę jedną chwilę przy telefonie,
    /// a nie stan systemu zadań; przeniesiony na drugie urządzenie albo przeżywający
    /// zamknięcie aplikacji byłby obietnicą powrotu tam, skąd nikt nie wychodził.
    /// </para>
    /// </remarks>
    private readonly List<Screen> _trail = [];

    /// <summary>Najdłuższy zapamiętywany ślad.</summary>
    private const int TrailLength = 16;

    /// <summary>
    /// Ekran domowy — ten, na którym kończy się cofanie.
    /// </summary>
    /// <remarks>
    /// Kalendarz, bo to on jest wartością początkową <see cref="Current"/> i pierwszym
    /// pytaniem dnia. Napisane tu raz, a nie wpisane w dwóch miejscach: dwa razy
    /// wpisany ekran domowy to dwa miejsca, w których trzeba pamiętać o zmianie —
    /// a przy rozjechanych cofanie kończy się gdzie indziej, niż pyta o to okno.
    /// </remarks>
    private const Screen Home = Screen.Calendar;

    /// <summary>Czy trwa cofanie. Wtedy zmiana ekranu nie dopisuje się do śladu.</summary>
    /// <remarks>
    /// Bez tego cofnięcie dokładałoby do śladu ekran, z którego się cofa — i drugie
    /// cofnięcie wracałoby tam, skąd się właśnie przyszło. Przycisk wstecz zamieniłby
    /// się w przełącznik między dwoma ostatnimi ekranami.
    /// </remarks>
    private bool _goingBack;

    /// <summary>
    /// Czy ten ekran wolno zapamiętać jako miejsce, do którego da się wrócić.
    /// </summary>
    /// <remarks>
    /// Przetwarzanie skrzynki i przegląd są <b>trybami</b>, nie miejscami: wejście
    /// w nie ma początek i koniec, a cofnięcie się do środka porzuconego przetwarzania
    /// stawia człowieka w połowie czynności, której nie zaczynał.
    /// </remarks>
    private static bool Remembered(Screen screen) =>
        screen is not (Screen.Clarify or Screen.Review);

    partial void OnCurrentChanging(Screen value)
    {
        // Current trzyma tu jeszcze **stary** ekran — zmiana przypisuje się po tym
        // wywołaniu. Stąd ślad da się prowadzić w jednym miejscu, zamiast dopisywać
        // się w każdym z piętnastu poleceń nawigacji; piętnaście dopisań rozjechałoby
        // się przy pierwszym nowym ekranie, którego ktoś nie dopisze.
        if (_goingBack || value == Current || !Remembered(Current))
        {
            return;
        }

        _trail.Add(Current);

        if (_trail.Count > TrailLength)
        {
            _trail.RemoveAt(0);
        }
    }

    /// <summary>
    /// Cofnięcie o jeden ekran. Fałsz znaczy „nie ma dokąd".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kolejność jest tu całą treścią. Najpierw ślad — czyli to, skąd się przyszło.
    /// Gdy śladu nie ma, zostaje kalendarz: ekran domowy tej aplikacji, i wracanie
    /// tam jest lepsze niż zamknięcie, bo zamknięcie z widoku „Co się działo" wygląda
    /// jak awaria, a nie jak nawigacja.
    /// </para>
    /// <para>
    /// Dopiero stojąc na ekranie domowym z pustym śladem oddajemy cofnięcie systemowi —
    /// czyli aplikacja się zamyka. Tak działa każda inna aplikacja na tym telefonie
    /// i odebranie tego byłoby zamknięciem człowieka w środku: przycisk wstecz, który
    /// nigdy nie wychodzi, przestaje być przyciskiem wstecz.
    /// </para>
    /// </remarks>
    public async Task BackAsync()
    {
        if (Pop() is not { } screen)
        {
            return;
        }

        _goingBack = true;

        try
        {
            await OpenAsync(screen);
        }
        finally
        {
            // Po pierwszym oczekiwaniu wewnątrz polecenia ekran jest już przypisany,
            // więc znacznik zdejmujemy dopiero tutaj — wcześniej zdjęty przepuściłby
            // własne cofnięcie z powrotem do śladu.
            _goingBack = false;
        }
    }

    /// <summary>
    /// Czy cofnięcie ma dokąd pójść. Fałsz znaczy „oddaj je systemowi".
    /// </summary>
    /// <remarks>
    /// Pytanie osobno od czynności, bo okno musi odpowiedzieć <b>od razu</b>, czy zajęło
    /// się cofnięciem. Android nie czeka na zakończenie wczytywania ekranu: albo
    /// odpowiedź jest w tej chwili, albo cofnięcie idzie dalej i zamyka aplikację.
    /// </remarks>
    public bool CanGoBack => _trail.Any(e => e != Current) || Current != Home;

    /// <summary>Dokąd cofnąć. Puste, gdy nie ma dokąd i cofnięcie należy do systemu.</summary>
    private Screen? Pop()
    {
        while (_trail.Count > 0)
        {
            var last = _trail[^1];
            _trail.RemoveAt(_trail.Count - 1);

            // Ten sam ekran w śladzie to nie jest miejsce do cofnięcia — najczęściej
            // bierze się z wejścia w tryb i wyjścia z niego.
            if (last != Current)
            {
                return last;
            }
        }

        return Current == Home ? null : Home;
    }

    private Task OpenAsync(Screen screen)
    {
        switch (screen)
        {
            case Screen.Settings:
                ShowSettingsCommand.Execute(null);
                return Task.CompletedTask;

            default:
                return Command(screen).ExecuteAsync(null);
        }
    }

    private IAsyncRelayCommand Command(Screen screen) => screen switch
    {
        Screen.Today => ShowTodayCommand,
        Screen.Now => ShowNowCommand,
        Screen.Inbox => ShowInboxCommand,
        Screen.Next => ShowNextCommand,
        Screen.Projects => ShowProjectsCommand,
        Screen.Someday => ShowSomedayCommand,
        Screen.Waiting => ShowWaitingCommand,
        Screen.Calendar => ShowCalendarCommand,
        Screen.Notes => ShowNotesCommand,
        Screen.Filters => ShowFiltersCommand,
        Screen.Archive => ShowArchiveCommand,
        Screen.Journal => ShowJournalCommand,

        // Tryby i wszystko nieznane lądują na „Dzisiaj". Cofnięcie ma skończyć się
        // ekranem, a nie brakiem odpowiedzi.
        _ => ShowTodayCommand,
    };

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

        // Karta zadania chowa na pulpicie podpisy pól, które odsłaniają przyciski
        // z ikonami: tam przycisk ma dymek i wystarcza za podpis.
        Detail.IsNarrow = value;
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
        Safely("Synchronizacja przy starcie", SyncQuietlyAsync);

        // Kalendarze tak samo: obok, bez czekania. Odświeżenie zeszło z drogi do
        // gotowości — sięga po sieć i potrafiło zatrzymać start na osiem sekund — więc
        // siatka rusza z tym, co w bazie, a świeże wydarzenia dochodzą do niej same.
        Safely("Kalendarze przy starcie", Calendar.RefreshFromSourcesAsync);
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
        await DayRolloverAsync();

        if (await _reminders.RunAsync() > 0)
        {
            CollectReminders();
        }

        // Zaległe kasowania odbić: wydarzenie po zadaniu, którego już nie ma, wisi
        // w cudzym kalendarzu do skutku, a skutek ma tylko wtedy, gdy ktoś spróbuje
        // ponownie. To ta sama odpowiedź na upływ czasu, co reszta tutaj.
        await Try("Kalendarz: zaległe odbicia", () => _mirror.FinishDeletionAsync());

        await Try("Kalendarz: pobranie w tle", FetchCalendarsAsync);

        // Osobno zabezpieczona: nieudany przebieg do Dysku nie ma prawa zabrać ze sobą
        // przypomnień, które właśnie się policzyły.
        await Try("Synchronizacja sama", SyncAloneAsync);
    }

    /// <summary>
    /// Pobranie kalendarzy zewnętrznych bez klikania.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Do dziś kalendarz pobierał się <b>wyłącznie</b> po naciśnięciu „Pobierz".
    /// Odstęp między odczytami był napisany i pilnowany, ale nikt go nie wołał —
    /// więc wydarzenie dodane na komputerze nie pojawiało się na telefonie wcale,
    /// dopóki się o nie nie poprosiło. Zadania jechały tymczasem same, co dawało
    /// najgorszy z możliwych obrazów: połowa tej samej rzeczy dociera, druga nie,
    /// i nie widać żadnej reguły, która by to tłumaczyła.
    /// </para>
    /// <para>
    /// Bez wymuszania: odstęp pilnuje sam, żeby nie chodzić po sieci częściej, niż
    /// trzeba. Przeliczenie ekranu tylko wtedy, gdy coś przyszło — przerysowanie siatki
    /// pod ręką, która właśnie coś na niej robi, jest kosztem bez pożytku.
    /// </para>
    /// </remarks>
    private async Task FetchCalendarsAsync()
    {
        var report = await _calendars.RefreshAsync();

        if (report.Events > 0 || report.Folded > 0)
        {
            await ReloadAsync();
        }
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
    /// <summary>Ile czekać z przeliczeniem ekranu po zapisie zrobionym w tle.</summary>
    /// <remarks>
    /// Sekunda, bo tyle mniej więcej trwa wyprawa do Google — a to jest ten zapis,
    /// o którym ekran nie ma jak wiedzieć.
    /// </remarks>
    private static readonly TimeSpan RecomputeDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Przeliczenie ekranu po zapisie, który nie przyszedł z ekranu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Objaw, który to wywołał: wyrzucone zadanie zostawiało na siatce swoje odbicie
    /// z Google — wpis wyglądający jak zadanie okrojone, bo zadania już nie było, a wpis
    /// jeszcze był. Dane były w porządku sekundę później; nie był w porządku ekran.
    /// </para>
    /// <para>
    /// Odbicie do kalendarza jest robione <b>po</b> kliknięciu, żeby odhaczenie nie
    /// czekało na cudzy serwer. Siatka przeliczała się natomiast <b>od razu</b> po
    /// wyrzuceniu — czyli dokładnie w chwili, gdy zadania już nie ma, a jego wydarzenia
    /// jeszcze nikt nie zdjął. Rysowała więc stan prawdziwy, tyle że przejściowy,
    /// i zostawała z nim, bo nic jej potem nie ruszało.
    /// </para>
    /// <para>
    /// Zamiast łatać ten jeden przypadek: <b>ekran idzie za bazą</b>. Każdy zapis,
    /// z którejkolwiek strony, prosi o przeliczenie. Zapisy z ekranu przeliczają go
    /// i tak od razu, więc to jest drugie przeliczenie sekundę później — kilka odczytów
    /// bez widocznej zmiany. Tyle kosztuje to, żeby żadna robota w tle nie kończyła się
    /// widokiem nieodpowiadającym danym.
    /// </para>
    /// </remarks>
    private CancellationTokenSource? _deferredRecompute;

    private void AskForRecompute() => Dispatcher.UIThread.Post(() =>
    {
        _deferredRecompute?.Cancel();
        _deferredRecompute?.Dispose();

        var source = new CancellationTokenSource();
        _deferredRecompute = source;

        _ = RecomputeSoonAsync(source.Token);
    });

    private async Task RecomputeSoonAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(RecomputeDelay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await Try("Ekran: przeliczenie po zapisie w tle", ReloadAsync);
    }

    /// <summary>Odłożona wysyłka po zapisie. Kolejny zapis odsuwa ją, a nie dokłada.</summary>
    /// <remarks>
    /// Do dziś zapis czekał na najbliższy przebieg minutnika, czyli do minuty — a przy
    /// dwóch urządzeniach minuta ciszy wygląda jak awaria, nie jak opóźnienie.
    /// Odwołanie poprzedniego odłożenia jest tu sednem: pięć zapisów pod rząd ma dać
    /// jeden przebieg pięć sekund po ostatnim, a nie pięć przebiegów.
    /// </remarks>
    private CancellationTokenSource? _deferredSend;

    private void AskForSend() => Dispatcher.UIThread.Post(() =>
    {
        _deferredSend?.Cancel();
        _deferredSend?.Dispose();

        var source = new CancellationTokenSource();
        _deferredSend = source;

        _ = SendSoonAsync(source.Token);
    });

    private async Task SendSoonAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(SendDelay, ct);
        }
        catch (OperationCanceledException)
        {
            // Przyszedł kolejny zapis i to on wyznacza nową chwilę.
            return;
        }

        await Try("Synchronizacja po zapisie", SyncAloneAsync);
    }

    /// <summary>
    /// Odczyt przy powrocie do okna, bez czekania na przerwę.
    /// </summary>
    /// <remarks>
    /// Powrót do okna jest najlepszym momentem na zajrzenie na Dysk, jaki ta aplikacja
    /// w ogóle ma: dokładnie wtedy ktoś zaczyna patrzeć na listę i dokładnie wtedy
    /// różnica między urządzeniami jest widoczna. Przerwa zostaje dla okna, przy którym
    /// się siedzi.
    /// </remarks>
    public async Task SyncAfterReturnAsync()
    {
        if (_syncing)
        {
            return;
        }

        await RunAsync("Synchronizacja po powrocie", quiet: true);

        // Kalendarze też: powrót do okna jest chwilą, w której patrzy się na siatkę.
        await Try("Kalendarz: pobranie po powrocie", FetchCalendarsAsync);
    }

    private async Task SyncAloneAsync()
    {
        // Najpierw blokada, dopiero potem znak: przebieg bywa dłuższy od minuty,
        // a zabranie znaku teraz znaczyłoby zgubienie zmiany, która czeka na wysłanie.
        if (_syncing)
        {
            return;
        }

        var change = Interlocked.Exchange(ref _change, 0) == 1;
        var gap = _clock.Now - _lastSync >= Gap;

        if (!change && !gap)
        {
            return;
        }

        await RunAsync(change ? "Synchronizacja po zmianie" : "Synchronizacja co jakiś czas", quiet: true);
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
    private async Task DayRolloverAsync()
    {
        var today = _clock.Today;

        // Pierwsze sprawdzenie tylko zapamiętuje dzień. Start nadrabia przejście własną
        // drogą (CatchUpAsync), więc robienie tego drugi raz byłoby pracą bez skutku.
        if (_day is null)
        {
            _day = today;
            return;
        }

        if (_day == today)
        {
            return;
        }

        _day = today;

        await _dayRollover.RunAsync();
        await _focus.ExpireAsync();

        await _activity.RecordAsync("Przejście dnia", $"nowy dzień: {today:yyyy-MM-dd}");
        await ReloadAsync();
    }

    /// <summary>
    /// Opróżnienie kolejki przypomnień pokazanych systemowo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Do dziś okno trzymało nad wszystkimi ekranami własny pasek z przypomnieniami.
    /// Miał sens, dopóki dymki systemowe były niepewne: pasek zostawał, gdy dymek się
    /// rozpłynął. Dziś odzywają się obie platformy, więc pasek był drugą listą do
    /// odprawienia, mówiącą to samo — i stał nad kalendarzem, czyli w jedynym miejscu,
    /// gdzie liczy się każdy wiersz wysokości.
    /// </para>
    /// <para>
    /// Opróżnianie zostaje, choć nikt już tego nie czyta. Powiadamiacz odkłada pokazane
    /// przypomnienia na listę do odebrania; bez odebrania rosłaby ona przez cały czas
    /// działania aplikacji.
    /// </para>
    /// </remarks>
    private void CollectReminders() => _notifier.Drain();

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
    private void Safely(string what, Func<Task> work) => _ = Try(what, work);

    private async Task Try(string what, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await _activity.RecordAsync(
                what, "nie udało się", ActivityLevel.Problem, $"{e.GetType().Name}: {e.Message}");
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
    private Task SyncQuietlyAsync() => RunAsync("Synchronizacja przy starcie");

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
    private async Task RunAsync(string what, bool quiet = false)
    {
        if (!_drive.HasCredentials || !Directory.Exists(_drive.TokenFolder))
        {
            return;
        }

        _syncing = true;

        SyncOutcome result;

        try
        {
            result = await _drive.SyncAsync();
        }
        finally
        {
            // Także po wywrotce: inaczej jedna awaria zatrzymywałaby automat na zawsze.
            // Przerwa liczona od końca przebiegu, nie od początku — długi przebieg nie
            // ma się kończyć w chwili, w której należy się następny.
            _syncing = false;
            _lastSync = _clock.Now;
        }

        var silently = quiet && result is { Ok: true, Sent: 0, Applied: 0 };

        if (!silently)
        {
            await _activity.RecordAsync(
                what,
                result.Ok ? $"wysłane {result.Sent}, przyjęte {result.Applied}" : result.Message,
                result.Ok ? ActivityLevel.Ok : ActivityLevel.Problem);
        }

        // Przeliczenie tylko wtedy, gdy coś przyszło: przerysowanie ekranu pod ręką,
        // która właśnie coś na nim robi, jest kosztem bez pożytku.
        if (result is { Ok: true, Applied: > 0 })
        {
            // Przypomnienia sprawdzane od razu, nie dopiero za minutę. Przyniesione
            // przez synchronizację bywa już zaległe — zadanie zmienione na drugim
            // urządzeniu przychodzi tu z godziną, która zdążyła minąć.
            if (await _reminders.RunAsync() > 0)
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
    private Task ReloadAsync() => _screen.RunAsync(RecomputeAsync);

    /// <summary>
    /// Jeden przebieg naraz, na końcu zawsze najnowszy.
    /// </summary>
    /// <remarks>
    /// Odkąd synchronizacja rusza sama, przeliczenie ekranu przychodzi z kilku stron
    /// naraz: z minutnika, ze scalenia, z zapisu w szczegółach. Dwa przebiegi jeden
    /// w drugim to podwójne odczyty tego samego i podwójna praca na listach — a wyniku
    /// pośredniego i tak nikt nie widzi.
    /// </remarks>
    private readonly LatestOnly _screen = new();

    private async Task RecomputeAsync()
    {
        await Try("Ekran: przeliczenie skrzynki", RefreshInboxAsync);

        var task = Current switch
        {
            Screen.Today => ShowTodayAsync(),
            Screen.Now => Now.LoadAsync(),
            Screen.Next => ShowNextAsync(),
            Screen.Someday => ShowSomedayAsync(),
            Screen.Archive => ShowArchiveAsync(),
            Screen.Projects => ShowProjectsAsync(),
            Screen.Waiting => ShowWaitingAsync(),
            // Przeliczenie, a **nie** otwarcie. Otwarcie kalendarza kończy się
            // przewinięciem na bieżącą godzinę i tak ma być — wchodzi się na niego,
            // żeby zobaczyć, co teraz. Ale tędy przychodzi też każdy zapis w tle:
            // odhaczenie zadania podnosiło znak zapisu, ten po sekundzie prosił
            // o przeliczenie, a przeliczenie wołało otwarcie i odrzucało widok
            // z oglądanego wieczora na teraz. Przeliczenie ma odświeżyć treść
            // i niczego nie przewijać.
            Screen.Calendar => Calendar.RefreshCommand.ExecuteAsync(null),
            Screen.Notes => Notes.LoadAsync(),
            Screen.Journal => Journal.LoadAsync(),
            Screen.Filters => Filters.RunCommand.ExecuteAsync(null),
            _ => Task.CompletedTask,
        };

        await Try($"Ekran: przeliczenie ({Current})", () => task);
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
        Safely("Dzisiaj: kandydaci", RefreshFocusAsync);

    /// <summary>
    /// Piątka na dziś i kandydaci do niej. Kandydaci to „Następne" oraz zaplanowane
    /// na dziś albo wcześniej (spec 8.6) — nie wszystko, co ma dzisiejszą datę.
    /// </summary>
    private async Task RefreshFocusAsync()
    {
        var today = Today;
        var forToday = await _focus.TodayAsync();

        FocusItems.Clear();
        FocusOffGrid.Clear();

        foreach (var task in forToday)
        {
            FocusItems.Add(task);

            // Bez godziny nie ma bloku na siatce, więc pasek nad kalendarzem jest
            // jedynym miejscem, gdzie to zadanie w ogóle widać.
            if (task.DoTime is null && task.State != TaskState.Done)
            {
                FocusOffGrid.Add(task);
            }
        }

        var selected = FocusItems.Select(t => t.Id).ToHashSet();
        // Zaplanowane **na dziś** nie są kandydatami do wzięcia na dziś. Stoją już na
        // liście dnia wyżej, a wybór na dziś jest obietnicą daną sobie co do rzeczy,
        // której dzień nie narzuca — branie na dziś czegoś, co i tak jest na dziś,
        // niczego nie zmienia i zajmuje jedno z pięciu miejsc.
        //
        // Zaległe zostają: te mają dzień wcześniejszy i wzięcie ich na dziś jest
        // prawdziwą decyzją, a nie powtórzeniem tego, co już wiadomo.
        var candidates = (await _tasks.ByStateAsync(TaskState.Next))
            .Concat(await _tasks.ByStateAsync(TaskState.Scheduled))
            .Where(t => t.State == TaskState.Next || t.DoDate < today)
            .ToList();

        // „Kiedyś-może" tylko na wyraźne życzenie. Wzięcie stamtąd czegoś na dziś
        // wyjmuje to z tego stanu — więc lista kandydatów jest właściwym miejscem
        // na taką decyzję, ale nie na stałe: inaczej wszystko, co się kiedykolwiek
        // odłożyło, wracałoby tu codziennie i odkładanie przestałoby cokolwiek dawać.
        if (AlsoSomeday)
        {
            candidates.AddRange((await _tasks.ByStateAsync(TaskState.Someday))
                .Where(t => t.DeferUntil is null || t.DeferUntil <= today));
        }

        candidates = candidates.Where(t => !selected.Contains(t.Id)).ToList();

        FocusCandidates.Clear();
        foreach (var task in candidates)
        {
            FocusCandidates.Add(TaskRow.From(task, today));
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

        var result = await _focus.TryFocusAsync(row.Task.Id);

        if (!result.Accepted)
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

        if (PendingFocus is { } pending)
        {
            await _focus.TryFocusAsync(pending.Id);
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

        var blocked = (await _queries.BlockedProjectsAsync())
            .Select(p => p.ProjectId)
            .ToHashSet();

        var rows = ProjectTree.Build(
            await _areas.ActiveAsync(), await _projects.ActiveAsync(), blocked);

        // Równowaga obszarów wpisana w te same wiersze, a nie na osobnym ekranie.
        // Dwa ekrany na te same obiekty dawały różne możliwości w każdym z nich:
        // tu barwa i usunięcie, tam zakładanie, a nazwa tylko tam. Jeden ekran, jeden
        // zestaw czynności — a liczby są cechą obszaru, więc stoją przy nim.
        var balance = (await _queries.BalanceAsync(Today))
            .ToDictionary(w => w.AreaId);

        ProjectRows.Clear();
        foreach (var row in rows)
        {
            ProjectRows.Add(new ProjectTreeRow(
                row,
                row.IsArea && balance.TryGetValue(row.Id, out var w) ? w : null));
        }
    }

    [RelayCommand]
    private async Task ShowWaitingAsync()
    {
        Current = Screen.Waiting;

        var pending = await _queries.WaitingAsync(Today);

        WaitingItems.Clear();
        foreach (var item in pending)
        {
            WaitingItems.Add(item);
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

        // Wejście na kalendarz jest chwilą, w której patrzy się na siatkę — więc jest
        // też najlepszą chwilą, żeby zapytać o świeże wydarzenia. Bez czekania: siatka
        // jest już narysowana z bazy, a odpowiedź z sieci dorysuje się sama, gdy przyjdzie.
        // Bez wymuszania, więc przy wejściu tuż po poprzednim pobraniu jest to sprawdzenie
        // odstępu, a nie drugie zapytanie do Google.
        Safely("Kalendarz: pobranie przy wejściu", FetchCalendarsAsync);
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
        var today = Today;
        await Fill(TodayItems, _tasks.TodayAsync(today));
        OnPropertyChanged(nameof(HasToday));
        await RefreshFocusAsync();

        // Ponaglenia (N3) i projekty zablokowane (N1) idą na „Dzisiaj", bo są sprawami
        // na dziś. Cisza obszarów (N10) **nigdy tu nie trafia** — to nie jest sprawa na
        // dziś, a codzienne przypominanie o niej zamieniłoby ją w szum (spec 6).
        var nudges = (await _queries.WaitingAsync(today)).Where(w => w.NeedsNudge).ToList();
        var blocked = await _queries.BlockedProjectsAsync();

        Nudges.Clear();
        foreach (var item in nudges)
        {
            Nudges.Add(item);
        }

        BlockedProjects.Clear();
        foreach (var project in blocked)
        {
            BlockedProjects.Add(project);
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

    /// <summary>Dzisiaj w strefie z ustawień — dla menu, które samo zegara nie ma.</summary>
    public DateOnly Today => _clock.Today;

    private async Task Fill(ObservableCollection<TaskRow> target, Task<IReadOnlyList<TaskItem>> source)
    {
        var items = await source;
        var today = Today;

        target.Clear();
        foreach (var item in items)
        {
            target.Add(TaskRow.From(item, today));
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

        var result = await _focus.TryFocusAsync(task.Id);

        PendingFocus = result.Accepted ? null : task;

        // Odmowa musi być słychać z każdego ekranu. Ramka z pytaniem „co schodzi"
        // stoi tylko na „Dzisiaj", więc wzięcie na dziś z „Kiedyś" przy pełnej piątce
        // nie robiło nic i nie mówiło dlaczego.
        Notice = result.Accepted
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

        var from = task.DoDate ?? Today;

        await _edit.RescheduleAsync(task.Id, from.AddDays(1), task.DoTime);
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
        if (day is { } value)
        {
            await _edit.RescheduleAsync(task.Id, value, task.DoTime);
        }
        else
        {
            await _edit.ApplyAsync(task.Id, Without(task));
        }

        await ReloadAsync();
    }

    /// <summary>Ten sam zestaw pól, tylko bez dnia wykonania — reszta ma zostać.</summary>
    private static TaskEdit Without(TaskItem task) =>
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
        (await _calendars.SourcesAsync())
            .Where(_calendars.CanWrite)
            .ToList();

    /// <summary>Kalendarz, w którym zadania lądują domyślnie.</summary>
    public Guid? MainCalendarId => _mirror.MainCalendarId;

    /// <summary>Kalendarz przypisany do obszaru — na potrzeby ptaszka w menu.</summary>
    public async Task<Guid?> AreaCalendarAsync(Guid areaId) =>
        (await _areas.FindAsync(areaId))?.CalendarId;

    /// <summary>
    /// Przypisanie obszaru do kalendarza Google.
    /// </summary>
    /// <remarks>
    /// Stąd, a nie z ustawień: obszar wybiera się patrząc na listę obszarów, a nie
    /// na listę kalendarzy. Kalendarz jest tu cechą obszaru — „gdzie to widać na
    /// zewnątrz" — a nie osobną rzeczą do skonfigurowania.
    /// </remarks>
    public async Task SetAreaCalendarAsync(ProjectTreeRow row, Guid? calendarId)
    {
        ArgumentNullException.ThrowIfNull(row);

        await _shell.SetAreaCalendarAsync(row.Id, calendarId);

        Notice = string.Empty;
        await ShowProjectsAsync();
    }

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

        Notice = await _mirror.ShareAsync(task.Id, calendarId) ?? string.Empty;
        await ReloadAsync();
    }

    public async Task UnshareTaskAsync(TaskItem task)
    {
        ArgumentNullException.ThrowIfNull(task);

        await _mirror.UnshareAsync(task.Id);
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
        var name = NewAreaName.Trim();

        if (name.Length == 0)
        {
            return;
        }

        await _shell.AddAreaAsync(name);
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

        await _shell.AddProjectAsync(parent.Id, outcome.Trim());
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
            await _shell.RenameAreaAsync(row.Id, name.Trim());
        }
        else
        {
            await _shell.RenameProjectAsync(row.Id, name.Trim());
        }

        await ShowProjectsAsync();
    }

    /// <summary>Usunięcie wiersza — obszaru albo projektu, i tylko pustego.</summary>
    public async Task DeleteRowAsync(ProjectTreeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        Notice = (row.IsArea
            ? await _shell.DeleteAreaAsync(row.Id)
            : await _shell.DeleteProjectAsync(row.Id)) ?? string.Empty;

        await ShowProjectsAsync();
    }

    /// <summary>Czy ten wiersz da się usunąć — menu ma nie proponować rzeczy bez skutku.</summary>
    public Task<string?> WhyCannotDeleteRowAsync(ProjectTreeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return row.IsArea
            ? _shell.WhyCannotDeleteAreaAsync(row.Id)
            : _shell.WhyCannotDeleteAsync(row.Id);
    }

    /// <summary>Barwa wiersza z ekranu „Projekty" — obszaru albo projektu.</summary>
    public async Task SetRowColorAsync(ProjectTreeRow row, string? color)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.IsArea)
        {
            await _shell.SetAreaColorAsync(row.Id, color);
        }
        else
        {
            await _shell.SetProjectColorAsync(row.Id, color);
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
        if (task.DoDate is { } day)
        {
            Calendar.Anchor = day;
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
        var inbox = await _inbox.ListAsync();

        InboxItems.Clear();
        foreach (var item in inbox)
        {
            InboxItems.Add(item);
        }

        InboxCount = InboxItems.Count;
    }
}

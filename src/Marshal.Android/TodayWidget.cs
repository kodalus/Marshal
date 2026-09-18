using System.Globalization;
using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Views;
using Android.Widget;
using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Application.UseCases;
using Marshal.Domain.Areas;
using Marshal.Domain.Projects;
using Marshal.Domain.Tasks;
using Marshal.UI;
using Microsoft.Extensions.DependencyInjection;

namespace Marshal.Android;

/// <summary>
/// Widget na ekranie domowym: plan dzisiejszego dnia (spec 4.2, 8.6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Plan dnia, nie sama piątka na dziś.</b> Do dziś widget pokazywał wyłącznie to,
/// co zostało wzięte „na dziś" w widoku „Teraz" — czyli obietnice dane sobie, bez
/// rzeczy umówionych z kimś na godzinę. Na ekranie domowym daje to obraz dnia,
/// w którym nie ma spotkania o szesnastej, i jest gorsze od braku widgetu: wygląda
/// na pełną odpowiedź.
/// </para>
/// <para>
/// Plan to zadania z dzisiejszym dniem wykonania i zaległe (ten sam zbiór, co ekran
/// „Dzisiaj") **plus** wzięte na dziś. Kolejność jest chronologiczna: najpierw to, co
/// ma godzinę, potem reszta. Godzina jest jedyną rzeczą, która narzuca porządek
/// z zewnątrz; wszystko inne porządkuje się samo w trakcie dnia.
/// </para>
/// <para>
/// <b>Wybór na dziś, nie widok „Teraz".</b> Spec wymieniała „Teraz", ale ta lista
/// powstaje z punktacji zależnej od zadeklarowanego czasu i energii (8.1) — a widget
/// nie ma jak o nie zapytać i musiałby je zgadywać. Wybór na dziś jest już wybrany,
/// więc odpowiada na to samo pytanie bez zgadywania czegokolwiek. Spec poprawiona.
/// </para>
/// <para>
/// <b>Te same usługi co aplikacja, nie osobny skrócony odczyt.</b> Spec proponowała
/// własny minimalny <c>DbContext</c> tylko do odczytu i dla samego czytania byłoby to
/// prostsze. Rzecz w przycisku odhaczenia: zapis z pominięciem dziennika zmian
/// i zegara logicznego zmieniłby wiersz lokalnie i **nigdy nie dotarłby na drugie
/// urządzenie** — bez błędu, bez śladu. Drugie, uproszczone wejście do danych jest
/// tu dokładnie tym rodzajem skrótu, który rozjeżdża bazy po cichu.
/// </para>
/// </remarks>
[BroadcastReceiver(Label = "Marshal — na dziś", Exported = true)]
[IntentFilter(new[] { "android.appwidget.action.APPWIDGET_UPDATE" })]
[MetaData("android.appwidget.provider", Resource = "@xml/marshal_widget")]
public sealed class TodayWidget : AppWidgetProvider
{
    /// <summary>Odhaczenie z widgetu. Własna akcja, bo system nie ma na to swojej.</summary>
    public const string CompleteAction = "com.kodalus.marshal.ZROBIONE";

    public const string TaskIdExtra = "zadanie";

    /// <summary>
    /// Ile wierszy ma układ.
    /// </summary>
    /// <remarks>
    /// Nie jest to już limit z N14 — ten dotyczy wybierania na dziś, a plan dnia bierze
    /// też rzeczy umówione i zaległe, których nikt nie limitował. Osiem to tyle, ile
    /// widać na ekranie domowym bez przewijania; widget, po który trzeba sięgnąć palcem,
    /// przestaje być spojrzeniem. Co się nie mieści, liczy stopka.
    /// </remarks>
    private const int Slots = 8;

    private static readonly int[] Rows =
    [
        Resource.Id.wiersz_1, Resource.Id.wiersz_2, Resource.Id.wiersz_3, Resource.Id.wiersz_4,
        Resource.Id.wiersz_5, Resource.Id.wiersz_6, Resource.Id.wiersz_7, Resource.Id.wiersz_8,
    ];

    private static readonly int[] Titles =
    [
        Resource.Id.tytul_1, Resource.Id.tytul_2, Resource.Id.tytul_3, Resource.Id.tytul_4,
        Resource.Id.tytul_5, Resource.Id.tytul_6, Resource.Id.tytul_7, Resource.Id.tytul_8,
    ];

    private static readonly int[] Captions =
    [
        Resource.Id.podpis_1, Resource.Id.podpis_2, Resource.Id.podpis_3, Resource.Id.podpis_4,
        Resource.Id.podpis_5, Resource.Id.podpis_6, Resource.Id.podpis_7, Resource.Id.podpis_8,
    ];

    private static readonly int[] Stripes =
    [
        Resource.Id.pasek_1, Resource.Id.pasek_2, Resource.Id.pasek_3, Resource.Id.pasek_4,
        Resource.Id.pasek_5, Resource.Id.pasek_6, Resource.Id.pasek_7, Resource.Id.pasek_8,
    ];

    private static readonly int[] Buttons =
    [
        Resource.Id.zrobione_1, Resource.Id.zrobione_2, Resource.Id.zrobione_3, Resource.Id.zrobione_4,
        Resource.Id.zrobione_5, Resource.Id.zrobione_6, Resource.Id.zrobione_7, Resource.Id.zrobione_8,
    ];

    /// <summary>Każe systemowi przerysować wszystkie osadzone widgety.</summary>
    public static void Refresh(Context context)
    {
        if (AppWidgetManager.GetInstance(context) is not { } manager)
        {
            return;
        }

        var ids = manager.GetAppWidgetIds(
            new ComponentName(context, Java.Lang.Class.FromType(typeof(TodayWidget))));

        if (ids is not { Length: > 0 })
        {
            return;
        }

        var zamiar = new Intent(context, typeof(TodayWidget));
        zamiar.SetAction(AppWidgetManager.ActionAppwidgetUpdate);
        zamiar.PutExtra(AppWidgetManager.ExtraAppwidgetIds, ids);

        context.SendBroadcast(zamiar);
    }

    public override void OnUpdate(
        Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is null || appWidgetManager is null || appWidgetIds is null)
        {
            return;
        }

        // Przepisane do zmiennych lokalnych: sprawdzenie na parametrze nie przenosi
        // się do domknięcia, bo kompilator nie ma jak zagwarantować, że parametr
        // nie zmieni się do chwili wywołania.
        var okno = context;
        var menedzer = appWidgetManager;
        var identyfikatory = appWidgetIds;

        // Odbiornik rozgłoszeń ma kilka sekund i działa na wątku głównym, a tu jest
        // odczyt z bazy. GoAsync przedłuża życie odbiornika na czas pracy w tle —
        // bez tego system potrafi ubić proces w środku zapytania.
        var oczekiwanie = GoAsync();

        _ = Task.Run(async () =>
        {
            try
            {
                var widok = await BuildAsync(okno);

                foreach (var id in identyfikatory)
                {
                    menedzer.UpdateAppWidget(id, widok);
                }
            }
            catch (Exception e)
            {
                // Widget, który się wywali, zostaje na ekranie jako „problem
                // z ładowaniem" — i tak wygląda gorzej niż pusta lista.
                global::Android.Util.Log.Warn("Marshal", e.ToString());
            }
            finally
            {
                // GoAsync zwraca wartość pustą, gdy odbiornik nie działa w tle —
                // wtedy nie ma czego kończyć.
                oczekiwanie?.Finish();
            }
        });
    }

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action != CompleteAction)
        {
            base.OnReceive(context, intent);
            return;
        }

        var id = intent.GetStringExtra(TaskIdExtra);
        var okno = context;
        var oczekiwanie = GoAsync();

        _ = Task.Run(async () =>
        {
            try
            {
                if (Guid.TryParse(id, out var zadanie))
                {
                    var services = await ServicesAsync(okno.ApplicationContext ?? okno);
                    await services.GetRequiredService<TaskEditService>().CompleteAsync(zadanie);
                }

                Refresh(okno);
            }
            catch (Exception e)
            {
                global::Android.Util.Log.Warn("Marshal", e.ToString());
            }
            finally
            {
                // GoAsync zwraca wartość pustą, gdy odbiornik nie działa w tle —
                // wtedy nie ma czego kończyć.
                oczekiwanie?.Finish();
            }
        });
    }

    private static async Task<RemoteViews> BuildAsync(Context context)
    {
        var services = await ServicesAsync(context.ApplicationContext ?? context);
        var clock = services.GetRequiredService<IClock>();
        var dzis = clock.Today;

        var zadania = services.GetRequiredService<ITaskRepository>();

        // Dwa zapytania, bo to dwie różne rzeczy: umówione na dziś (i zaległe) oraz
        // wzięte na dziś. Zadanie potrafi być jednym i drugim naraz, stąd odsiew po
        // identyfikatorze — inaczej stałoby w planie dwa razy.
        var plan = await zadania.TodayAsync(dzis);
        var wybrane = await zadania.ByFocusDateAsync(dzis);

        var razem = plan
            .Concat(wybrane.Where(w => plan.All(p => p.Id != w.Id)))
            .OrderBy(z => Pora(z, dzis) is null)
            .ThenBy(z => Pora(z, dzis))
            .ThenBy(z => z.Title, StringComparer.CurrentCulture)
            .ToList();

        var projekty = (await services.GetRequiredService<IProjectRepository>().AllAsync())
            .ToDictionary(p => p.Id);
        var obszary = (await services.GetRequiredService<IAreaRepository>().AllAsync())
            .ToDictionary(o => o.Id);

        var widok = new RemoteViews(context.PackageName, Resource.Layout.widget_marshal);

        widok.SetTextViewText(Resource.Id.naglowek, $"Na dziś — {dzis:d.MM}");
        widok.SetViewVisibility(
            Resource.Id.pusto, razem.Count == 0 ? ViewStates.Visible : ViewStates.Gone);

        // Wrzut otwiera aplikację, a nie pole tekstowe w widgecie: RemoteViews nie
        // zna pola do wpisywania, a wszystko inne znaczy drugi ekran do utrzymywania —
        // czyli dokładnie to, przed czym ostrzega spec 4.2.
        widok.SetOnClickPendingIntent(Resource.Id.wrzut, LaunchIntent(context));

        for (var i = 0; i < Slots; i++)
        {
            if (i >= razem.Count)
            {
                widok.SetViewVisibility(Rows[i], ViewStates.Gone);
                continue;
            }

            var zadanie = razem[i];
            var zrobione = zadanie.State == TaskState.Done;

            widok.SetViewVisibility(Rows[i], ViewStates.Visible);
            widok.SetTextViewText(Titles[i], zrobione ? $"✓ {zadanie.Title}" : zadanie.Title);
            widok.SetTextViewText(Captions[i], Podpis(zadanie, dzis, projekty, obszary));
            widok.SetInt(Stripes[i], "setBackgroundColor", Barwa(zadanie, projekty, obszary));

            // Przycisk tylko przy tym, co jeszcze nie zrobione: odhaczanie odhaczonego
            // nic nie znaczy, a przycisk bez skutku uczy, że przyciski bywają bez skutku.
            widok.SetViewVisibility(
                Buttons[i], zrobione ? ViewStates.Invisible : ViewStates.Visible);
            widok.SetOnClickPendingIntent(Buttons[i], CompleteIntent(context, zadanie.Id, i));
        }

        // Ile dnia nie widać. Bez tego widget pełny po brzegi wygląda tak samo jak
        // widget pokazujący wszystko — a to dwie różne wiadomości.
        var reszta = razem.Count - Slots;

        widok.SetViewVisibility(
            Resource.Id.reszta, reszta > 0 ? ViewStates.Visible : ViewStates.Gone);

        if (reszta > 0)
        {
            widok.SetTextViewText(Resource.Id.reszta, $"…i jeszcze {reszta} w aplikacji");
        }

        return widok;
    }

    /// <summary>Godzina, o której to stoi w dzisiejszym planie. Pusta, gdy bez godziny.</summary>
    /// <remarks>
    /// Wyłącznie dla dnia dzisiejszego. Zadanie zaległe ma godzinę sprzed paru dni
    /// i wstawiona między dzisiejsze udawałaby, że jest na nią umówione dziś.
    /// </remarks>
    private static TimeOnly? Pora(TaskItem zadanie, DateOnly dzis) =>
        zadanie.DoDate == dzis ? zadanie.DoTime : null;

    /// <summary>Druga linijka wiersza: kiedy i do czego to należy.</summary>
    private static string Podpis(
        TaskItem zadanie,
        DateOnly dzis,
        IReadOnlyDictionary<Guid, Project> projekty,
        IReadOnlyDictionary<Guid, Area> obszary)
    {
        var czesci = new List<string>();

        if (Pora(zadanie, dzis) is { } pora)
        {
            // Koniec liczony z oszacowania, gdy jest. „16:00 – 16:30" mówi, ile dnia
            // to zajmie; samo „16:00" zostawia to do policzenia w głowie.
            czesci.Add(zadanie.EstimatedMinutes is { } minut && minut > 0
                ? $"{Godzina(pora)} – {Godzina(pora.AddMinutes(minut))}"
                : Godzina(pora));
        }
        else if (zadanie.DoDate is { } dzien && dzien < dzis)
        {
            czesci.Add($"zaległe z {dzien:d.MM}");
        }
        else if (zadanie.FocusDate == dzis)
        {
            czesci.Add("wzięte na dziś");
        }

        if (Nalezy(zadanie, projekty, obszary) is { } gdzie)
        {
            czesci.Add(gdzie);
        }

        return string.Join(" / ", czesci);
    }

    /// <summary>Godzina jako „16:00".</summary>
    /// <remarks>
    /// Niezmiennicza, nie lokalna: dwukropek jest tu **znakiem**, a nie separatorem
    /// do podmiany. Kultura systemowa potrafi wstawić w to miejsce kropkę albo
    /// dwunastkę z „PM", a widget ma wyglądać tak samo jak siatka kalendarza obok.
    /// </remarks>
    private static string Godzina(TimeOnly pora) =>
        pora.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string? Nalezy(
        TaskItem zadanie,
        IReadOnlyDictionary<Guid, Project> projekty,
        IReadOnlyDictionary<Guid, Area> obszary)
    {
        if (zadanie.ProjectId is { } projekt && projekty.TryGetValue(projekt, out var p))
        {
            return p.Outcome;
        }

        return zadanie.AreaId is { } obszar && obszary.TryGetValue(obszar, out var o)
            ? o.Name
            : null;
    }

    /// <summary>
    /// Barwa paska przy wierszu: zadania, a gdy go nie ma — projektu, a gdy i tego nie
    /// ma — obszaru. Ta sama zasada, co na siatce kalendarza.
    /// </summary>
    /// <remarks>
    /// Zapis barwy jest tekstem wpisanym przez człowieka, więc może być czymkolwiek.
    /// Wywrotka przy rysowaniu widgetu nie daje żadnego objawu poza pustym prostokątem
    /// na ekranie domowym, więc zły zapis schodzi na barwę domyślną.
    /// </remarks>
    private static int Barwa(
        TaskItem zadanie,
        IReadOnlyDictionary<Guid, Project> projekty,
        IReadOnlyDictionary<Guid, Area> obszary)
    {
        var zapis = zadanie.Color;

        if (string.IsNullOrWhiteSpace(zapis)
            && zadanie.ProjectId is { } projekt
            && projekty.TryGetValue(projekt, out var p))
        {
            zapis = p.Color;
        }

        if (string.IsNullOrWhiteSpace(zapis)
            && zadanie.AreaId is { } obszar
            && obszary.TryGetValue(obszar, out var o))
        {
            zapis = o.Color;
        }

        if (string.IsNullOrWhiteSpace(zapis))
        {
            return Akcent;
        }

        try
        {
            return global::Android.Graphics.Color.ParseColor(zapis).ToArgb();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Szeroko, bo rodzaj wyjątku zależy od tego, czy rozbiór barwy jest po
            // stronie zarządzanej, czy schodzi do Javy — a to nie jest wiedza, na
            // której wolno opierać działanie widgetu.
            return Akcent;
        }
    }

    /// <summary>Barwa domyślna paska — ta sama, co akcent aplikacji.</summary>
    private static readonly int Akcent =
        unchecked((int)0xFF7C6CF5);

    /// <summary>
    /// Zamiar odhaczenia konkretnego zadania.
    /// </summary>
    /// <remarks>
    /// Kod żądania różny dla każdego wiersza. Przy wspólnym system uznałby pięć
    /// zamiarów za jeden — porównuje je bez patrzenia na dodatkowe dane — i każdy
    /// przycisk odhaczałby zadanie z wiersza pierwszego.
    /// </remarks>
    private static PendingIntent? CompleteIntent(Context context, Guid id, int slot)
    {
        var zamiar = new Intent(context, typeof(TodayWidget));
        zamiar.SetAction(CompleteAction);
        zamiar.PutExtra(TaskIdExtra, id.ToString());

        return PendingIntent.GetBroadcast(
            context, slot, zamiar, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }

    private static PendingIntent? LaunchIntent(Context context)
    {
        var zamiar = new Intent(context, typeof(MainActivity));
        zamiar.SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);

        return PendingIntent.GetActivity(
            context, Slots, zamiar, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }

    /// <summary>
    /// Zależności aplikacji razem z gotową bazą.
    /// </summary>
    /// <remarks>
    /// Przez <see cref="AppServices"/>, nie własnym złożeniem: widget i okno mieszkają
    /// w tym samym procesie, a dwa złożenia dałyby dwa konteksty nad tym samym plikiem.
    /// Gdy system wybudzi sam odbiornik, migracja przechodzi tą samą drogą — inaczej
    /// widget na świeżo zainstalowanej aplikacji sięgałby do bazy, której nikt jeszcze
    /// nie założył.
    /// </remarks>
    private static async Task<IServiceProvider> ServicesAsync(Context kontekst)
    {
        // Haczyk na dymki **przed** składaniem: składanie nadrabia zaległe przypomnienia,
        // a widget potrafi być pierwszy w procesie i jedyny. Bez tego odświeżenie widgetu
        // o siódmej rano zjadało przypomnienie ustawione na siódmą — zapisywało je jako
        // pokazane i nie pokazywało.
        Powiadomienia.Podepnij(kontekst);

        await AppServices.ReadyAsync();
        return AppServices.Provider;
    }
}

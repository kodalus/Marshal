using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Views;
using Android.Widget;
using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Application.UseCases;
using Marshal.UI;
using Microsoft.Extensions.DependencyInjection;

namespace Marshal.Android;

/// <summary>
/// Widget na ekranie domowym: piątka wybrana na dziś (spec 4.2, 8.6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Wybór na dziś, nie widok „Teraz".</b> Spec wymieniała „Teraz", ale ta lista
/// powstaje z punktacji zależnej od zadeklarowanego czasu i energii (8.1) — a widget
/// nie ma jak o nie zapytać i musiałby je zgadywać. Piątka na dziś jest już wybrana,
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

    /// <summary>Limit z N14 — tyle wierszy ma układ i tyle wolno wybrać na dobę.</summary>
    private const int Slots = 5;

    private static readonly int[] Rows =
        [Resource.Id.wiersz_1, Resource.Id.wiersz_2, Resource.Id.wiersz_3, Resource.Id.wiersz_4, Resource.Id.wiersz_5];

    private static readonly int[] Titles =
        [Resource.Id.tytul_1, Resource.Id.tytul_2, Resource.Id.tytul_3, Resource.Id.tytul_4, Resource.Id.tytul_5];

    private static readonly int[] Buttons =
        [Resource.Id.zrobione_1, Resource.Id.zrobione_2, Resource.Id.zrobione_3, Resource.Id.zrobione_4, Resource.Id.zrobione_5];

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

        var wybrane = await services.GetRequiredService<ITaskRepository>()
            .ByFocusDateAsync(clock.Today);

        var widok = new RemoteViews(context.PackageName, Resource.Layout.widget_marshal);

        widok.SetTextViewText(Resource.Id.naglowek, $"Na dziś — {clock.Today:d.MM}");
        widok.SetViewVisibility(Resource.Id.pusto, wybrane.Count == 0 ? ViewStates.Visible : ViewStates.Gone);

        // Wrzut otwiera aplikację, a nie pole tekstowe w widgecie: RemoteViews nie
        // zna pola do wpisywania, a wszystko inne znaczy drugi ekran do utrzymywania —
        // czyli dokładnie to, przed czym ostrzega spec 4.2.
        widok.SetOnClickPendingIntent(Resource.Id.wrzut, LaunchIntent(context));

        for (var i = 0; i < Slots; i++)
        {
            if (i >= wybrane.Count)
            {
                widok.SetViewVisibility(Rows[i], ViewStates.Gone);
                continue;
            }

            var zadanie = wybrane[i];

            widok.SetViewVisibility(Rows[i], ViewStates.Visible);
            widok.SetTextViewText(Titles[i], zadanie.Title);
            widok.SetOnClickPendingIntent(Buttons[i], CompleteIntent(context, zadanie.Id, i));
        }

        return widok;
    }

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

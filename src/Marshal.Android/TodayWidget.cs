using System.Runtime.Versioning;
using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Widget;
using Marshal.Application.UseCases;
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
/// Treść planu liczy <see cref="PlanDniaService"/>, nie ta klasa. Tu zostaje samo
/// rysowanie: rama i wskazanie, skąd brać wiersze.
/// </para>
/// <para>
/// <b>Lista przewijana, nie wiersze wpisane na sztywno.</b> Osiem wierszy wystarczało,
/// dopóki widget pokazywał wybór na dziś, bo tego jest najwyżej pięć. Plan całego dnia
/// nie ma takiego limitu — a wiersz, którego nie widać, jest w planie dnia tym samym,
/// co wiersz, którego nie ma. Kosztem jest osobna usługa i osobna fabryka widoków:
/// system rozwija wiersze w procesie ekranu domowego, nie w naszym.
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
    /// Kody żądań dla zamiarów oczekujących.
    /// </summary>
    /// <remarks>
    /// Różne, bo system porównuje zamiary **bez patrzenia na dodatkowe dane**. Przy
    /// wspólnym kodzie wzorzec odhaczenia i otwarcie aplikacji byłyby dla niego jednym
    /// zamiarem, a drugie zastępowałoby pierwsze.
    /// </remarks>
    private const int KodOdhaczenia = 0;

    private const int KodOtwarcia = 1;

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

        // Dwie różne rzeczy, obie potrzebne. Przerysowanie odświeża nagłówek i samą
        // ramę widgetu, ale **nie** pyta listy o nowe wiersze — ta trzyma swoje
        // w fabryce i oddaje je, dopóki nikt jej nie powie, że są nieaktualne.
        // Bez tego drugiego wywołania odhaczone zadanie zostawało na liście.
#pragma warning disable CA1422
        manager.NotifyAppWidgetViewDataChanged(ids, Resource.Id.lista);
#pragma warning restore CA1422

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

        // Bez GoAsync i bez wątku w tle: od czasu, gdy treść niesie lista, składanie
        // ramy nie dotyka bazy. Rama to nagłówek, przycisk wrzutu i wskazanie, skąd
        // brać wiersze — a wiersze wczyta usługa, na swoim wątku i we własnym czasie.
        foreach (var id in appWidgetIds)
        {
            appWidgetManager.UpdateAppWidget(id, Rama(context, id));
        }
    }

    /// <summary>
    /// Rama widgetu: nagłówek, wrzut i podpięcie listy.
    /// </summary>
    /// <remarks>
    /// Osobny egzemplarz na każdy osadzony widget, a nie jeden wspólny. Zamiar do
    /// usługi niesie identyfikator widgetu, a system rozróżnia zamiary bez patrzenia
    /// na dodatkowe dane — dwa widgety z jednym zamiarem dostałyby jedną fabrykę
    /// i jedną listę na spółkę. Stąd też adres w zamiarze: jest po to, żeby dwa
    /// zamiary do tej samej usługi różniły się czymś, co system porównuje.
    /// </remarks>
    private static RemoteViews Rama(Context context, int widgetId)
    {
        var widok = new RemoteViews(context.PackageName, Resource.Layout.widget_marshal);

        // Sam napis, bez daty. Data musiałaby iść z zegara aplikacji, bo ten liczy dzień
        // w strefie z ustawień — czyli czyta bazę, a rama ma się składać bez niej.
        // Data z zegara systemowego bywałaby o dzień inna niż plan pod nią, a dzień
        // jest i tak na ekranie domowym obok.
        widok.SetTextViewText(Resource.Id.naglowek, "Na dziś");

        // Wrzut otwiera aplikację, a nie pole tekstowe w widgecie: RemoteViews nie
        // zna pola do wpisywania, a wszystko inne znaczy drugi ekran do utrzymywania —
        // czyli dokładnie to, przed czym ostrzega spec 4.2.
        widok.SetOnClickPendingIntent(Resource.Id.wrzut, LaunchIntent(context));

        var doUslugi = new Intent(context, typeof(TodayWidgetService));
        doUslugi.PutExtra(AppWidgetManager.ExtraAppwidgetId, widgetId);
        doUslugi.SetData(global::Android.Net.Uri.Parse(doUslugi.ToUri(IntentUriType.Scheme)));

        // Wskazanie usługi zamiarem jest od Androida 35 oznaczone jako przestarzałe na
        // rzecz podawania wierszy wprost w RemoteViews. Tamta droga nie zna usługi
        // dostarczającej wiersze, więc nie jest zamiennikiem dla listy, która czyta
        // bazę — jest zamiennikiem dla listy krótkiej i znanej z góry. Nadal działa
        // i nadal jest jedyną drogą dla listy budowanej po stronie aplikacji.
#pragma warning disable CA1422
        widok.SetRemoteAdapter(Resource.Id.lista, doUslugi);
#pragma warning restore CA1422

        // Napis zamiast pustej listy — system podmienia je sam, więc nie trzeba
        // zgadywać, czy plan jest pusty, zanim lista go wczyta.
        widok.SetEmptyView(Resource.Id.lista, Resource.Id.pusto);

        widok.SetPendingIntentTemplate(Resource.Id.lista, CompleteTemplate(context));

        return widok;
    }

    /// <summary>
    /// Wzorzec zamiaru odhaczenia, wspólny dla całej listy.
    /// </summary>
    /// <remarks>
    /// <b>Zmienny</b>, w odróżnieniu od pozostałych zamiarów w tej aplikacji — i to nie
    /// jest niedopatrzenie. Wiersz listy nie ma własnego zamiaru oczekującego: system
    /// bierze ten wzorzec i dokłada do niego dane wiersza. Zamiar niezmienny odmówiłby
    /// przyjęcia tych danych, a od Androida 12 takie połączenie jest wprost zabronione.
    /// Nie ma tu czego nadużyć: wzorzec nie wskazuje niczego poza naszym odbiornikiem,
    /// a dokładany jest wyłącznie identyfikator zadania.
    /// </remarks>
    private static PendingIntent? CompleteTemplate(Context context)
    {
        var zamiar = new Intent(context, typeof(TodayWidget));
        zamiar.SetAction(CompleteAction);

        return PendingIntent.GetBroadcast(
            context, KodOdhaczenia, zamiar, ZnacznikiWzorca());
    }

    /// <summary>
    /// Znaczniki wzorca: zmienny tam, gdzie system w ogóle zna to pojęcie.
    /// </summary>
    /// <remarks>
    /// Pojawiło się w Androidzie 12, a aplikacja sięga do 10. Starsze wydania nie mają
    /// czego oznaczać, bo zamiar oczekujący był tam zmienny z natury — dopisanie flagi,
    /// której nie znają, byłoby tylko liczbą bez znaczenia, a analizator zgodności
    /// słusznie tego nie przepuszcza.
    /// </remarks>
    private static PendingIntentFlags ZnacznikiWzorca() =>
        OperatingSystem.IsAndroidVersionAtLeast(31)
            ? PendingIntentFlags.UpdateCurrent | Zmienny()
            : PendingIntentFlags.UpdateCurrent;

    /// <summary>Osobno i z adnotacją wydania — inaczej analizator czyta to jako zwykłą liczbę.</summary>
    [SupportedOSPlatform("android31.0")]
    private static PendingIntentFlags Zmienny() => PendingIntentFlags.Mutable;

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

    private static PendingIntent? LaunchIntent(Context context)
    {
        var zamiar = new Intent(context, typeof(MainActivity));
        zamiar.SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);

        return PendingIntent.GetActivity(
            context, KodOtwarcia, zamiar,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
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

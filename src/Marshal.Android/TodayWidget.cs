using System.Runtime.Versioning;
using Android.App;
using Marshal.Application.Abstractions;
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
    /// <summary>
    /// Wszystko, co widget zgłasza. Jedna akcja, bo lista ma jeden wzorzec zamiaru.
    /// </summary>
    /// <remarks>
    /// Wierszowi listy nie da się dać osobnego zamiaru: system trzyma jeden wzorzec na
    /// całą listę i dokłada do niego to, co wiersz wpisze. Skoro wzorzec ma być jeden,
    /// to i akcja jest jedna, a rozstrzyga dodatek <see cref="CoExtra"/>.
    /// </remarks>
    public const string CompleteAction = "com.kodalus.marshal.WIDGET";

    public const string TaskIdExtra = "zadanie";

    /// <summary>Co widget zgłasza: odhaczenie, otwarcie albo przesunięcie dnia.</summary>
    private const string CoExtra = "co";

    /// <summary>Nazwa dodatku — jawna, bo wypełnia ją fabryka wierszy z drugiego pliku.</summary>
    public const string CoOtworzExtra = CoExtra;

    private const string CoZrobione = "zrobione";

    public const string CoOtworz = "otworz";

    private const string CoDzien = "dzien";

    private const string DeltaExtra = "delta";

    /// <summary>Który dzień ogląda ten widget, licząc od dzisiejszego.</summary>
    /// <remarks>
    /// <para>
    /// W ustawieniach systemu, a nie w naszej bazie, i to jest rozstrzygnięcie. To nie
    /// jest dana aplikacji: to stan <b>jednego kafelka na jednym ekranie domowym</b>.
    /// Zapisany w bazie pojechałby synchronizacją na komputer, gdzie nie znaczy nic,
    /// i wracałby stamtąd przy każdym przebiegu.
    /// </para>
    /// <para>
    /// Osobno dla każdego osadzonego widgetu, bo dwa kafelki obok siebie mają prawo
    /// pokazywać dwa różne dni — po to się je stawia dwa.
    /// </para>
    /// </remarks>
    private const string Ustawienia = "widget";

    internal static int Przesuniecie(Context kontekst, int widgetId) =>
        kontekst.GetSharedPreferences(Ustawienia, FileCreationMode.Private)
            ?.GetInt($"dzien-{widgetId}", 0) ?? 0;

    private static void Przesun(Context kontekst, int widgetId, int delta)
    {
        if (kontekst.GetSharedPreferences(Ustawienia, FileCreationMode.Private) is not { } zapis)
        {
            return;
        }

        var nowe = Przesuniecie(kontekst, widgetId) + delta;

        // Granica po obu stronach: kilkanaście dotknięć strzałki w jedną stronę nie ma
        // wyprowadzać widgetu w miejsce, z którego nie widać, jak wrócić.
        nowe = Math.Clamp(nowe, -14, 14);

        zapis.Edit()?.PutInt($"dzien-{widgetId}", nowe)?.Apply();
    }

    /// <summary>Zapomnienie ustawienia widgetu, który zdjęto z ekranu.</summary>
    public override void OnDeleted(Context? context, int[]? appWidgetIds)
    {
        base.OnDeleted(context, appWidgetIds);

        if (context?.GetSharedPreferences(Ustawienia, FileCreationMode.Private) is not { } zapis
            || appWidgetIds is null)
        {
            return;
        }

        var edycja = zapis.Edit();

        foreach (var id in appWidgetIds)
        {
            edycja?.Remove($"dzien-{id}");
        }

        edycja?.Apply();
    }

    /// <summary>
    /// Kody żądań dla zamiarów oczekujących.
    /// </summary>
    /// <remarks>
    /// Różne, bo system porównuje zamiary **bez patrzenia na dodatkowe dane**. Przy
    /// wspólnym kodzie wzorzec odhaczenia i otwarcie aplikacji byłyby dla niego jednym
    /// zamiarem, a drugie zastępowałoby pierwsze.
    /// </remarks>
    private const int KodWiersza = 0;

    private const int KodOtwarcia = 1;

    private const int KodWrzutu = 2;

    /// <summary>Od tego numeru w górę idą strzałki dni — po dwie na każdy kafelek.</summary>
    private const int KodDnia = 1000;

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

        Przerysuj(context, appWidgetManager, appWidgetIds);
    }

    /// <summary>
    /// Złożenie i podanie ramy systemowi.
    /// </summary>
    /// <remarks>
    /// <para>
    /// W tle, bo nagłówek niesie datę, a dzisiejszy dzień liczy zegar aplikacji
    /// w strefie z ustawień — czyli czyta bazę. Zegar systemowy byłby o dzień inny od
    /// planu pod nagłówkiem u kogoś, kto ma ustawioną inną strefę, i właśnie przy
    /// przełączaniu dni byłoby to widać najbardziej.
    /// </para>
    /// <para>
    /// Składowa odbiornika, nie metoda statyczna: <c>GoAsync</c> przedłuża życie
    /// <b>tego</b> odbiornika i nie ma znaczenia w oderwaniu od niego.
    /// </para>
    /// </remarks>
    private void Przerysuj(Context context, AppWidgetManager menedzer, int[] identyfikatory)
    {
        var okno = context;
        var oczekiwanie = GoAsync();

        _ = Task.Run(async () =>
        {
            try
            {
                var services = await ServicesAsync(okno.ApplicationContext ?? okno);
                var dzis = services.GetRequiredService<IClock>().Today;

                foreach (var id in identyfikatory)
                {
                    menedzer.UpdateAppWidget(id, Rama(okno, id, dzis));
                }
            }
            catch (Exception e)
            {
                // Widget, który się wywali, zostaje na ekranie jako „problem
                // z ładowaniem" — i to wygląda gorzej niż pusta lista.
                global::Android.Util.Log.Warn("Marshal", e.ToString());
            }
            finally
            {
                oczekiwanie?.Finish();
            }
        });
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
    private static RemoteViews Rama(Context context, int widgetId, DateOnly dzis)
    {
        var widok = new RemoteViews(context.PackageName, Resource.Layout.widget_marshal);
        var przesuniecie = Przesuniecie(context, widgetId);

        widok.SetTextViewText(Resource.Id.naglowek, Nazwa(przesuniecie, dzis));

        widok.SetOnClickPendingIntent(
            Resource.Id.wstecz, DzienIntent(context, widgetId, -1));
        widok.SetOnClickPendingIntent(
            Resource.Id.naprzod, DzienIntent(context, widgetId, +1));

        // Dotknięcie samego kafelka otwiera kalendarz: widget odpowiada na pytanie
        // „co dziś", a kalendarz jest tym samym pytaniem zadanym szerzej. Na pustym
        // planie kafelek zakrywa napis „nic nie zaplanowane", więc i on prowadzi tam samo.
        widok.SetOnClickPendingIntent(Resource.Id.korzen, OtworzIntent(context));
        widok.SetOnClickPendingIntent(Resource.Id.pusto, OtworzIntent(context));

        // Wrzut otwiera aplikację, a nie pole tekstowe w widgecie: RemoteViews nie zna
        // pola do wpisywania, a wszystko inne znaczy drugi ekran do utrzymywania.
        //
        // Wypadło przy przebudowie na listę i przez to przycisk nie robił nic — a nic
        // nie robi też przycisk, którego zapomniano podpiąć, i przycisk zasłonięty przez
        // cudze dotknięcie. Z zewnątrz wyglądają identycznie.
        widok.SetOnClickPendingIntent(Resource.Id.wrzut, LaunchIntent(context));

        var doUslugi = new Intent(context, typeof(TodayWidgetService));
        doUslugi.PutExtra(AppWidgetManager.ExtraAppwidgetId, widgetId);
        doUslugi.SetData(global::Android.Net.Uri.Parse(doUslugi.ToUri(IntentUriType.Scheme)));

        // Wskazanie usługi zamiarem jest od Androida 15 oznaczone jako przestarzałe na
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

        widok.SetPendingIntentTemplate(Resource.Id.lista, WzorzecWiersza(context));

        return widok;
    }

    /// <summary>Nazwa oglądanego dnia. Słowo, gdy jest; data, gdy słowa nie ma.</summary>
    /// <remarks>
    /// „Na dziś" i „Jutro" czyta się bez liczenia, a o to chodzi przy kafelku, na który
    /// się zerka. Dalej niż o dzień słowo przestaje pomagać i lepsza jest data.
    /// </remarks>
    private static string Nazwa(int przesuniecie, DateOnly dzis) => przesuniecie switch
    {
        0 => "Na dziś",
        1 => "Jutro",
        -1 => "Wczoraj",
        _ => $"{dzis.AddDays(przesuniecie):ddd d.MM}",
    };

    /// <summary>
    /// Wzorzec zamiaru dla wierszy listy.
    /// </summary>
    /// <remarks>
    /// <b>Zmienny</b>, w odróżnieniu od pozostałych zamiarów w tej aplikacji — i to nie
    /// jest niedopatrzenie. Wiersz listy nie ma własnego zamiaru oczekującego: system
    /// bierze ten wzorzec i dokłada do niego dane wiersza. Zamiar niezmienny odmówiłby
    /// przyjęcia tych danych, a od Androida 12 takie połączenie jest wprost zabronione.
    /// Nie ma tu czego nadużyć: wzorzec nie wskazuje niczego poza naszym odbiornikiem,
    /// a dokładane jest wyłącznie to, co wiersz zgłasza.
    /// </remarks>
    private static PendingIntent? WzorzecWiersza(Context context)
    {
        var zamiar = new Intent(context, typeof(TodayWidget));
        zamiar.SetAction(CompleteAction);

        return PendingIntent.GetBroadcast(
            context, KodWiersza, zamiar, ZnacznikiWzorca());
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

    /// <summary>
    /// Strzałka przesuwająca dzień.
    /// </summary>
    /// <remarks>
    /// Kod żądania niesie identyfikator widgetu i kierunek, bo system porównuje zamiary
    /// <b>bez patrzenia na dodatkowe dane</b>. Przy wspólnym kodzie obie strzałki
    /// wszystkich kafelków byłyby dla niego jednym zamiarem i każda przesuwałaby to samo.
    /// </remarks>
    private static PendingIntent? DzienIntent(Context context, int widgetId, int delta)
    {
        var zamiar = new Intent(context, typeof(TodayWidget));
        zamiar.SetAction(CompleteAction);
        zamiar.PutExtra(CoExtra, CoDzien);
        zamiar.PutExtra(DeltaExtra, delta);
        zamiar.PutExtra(AppWidgetManager.ExtraAppwidgetId, widgetId);

        return PendingIntent.GetBroadcast(
            context,
            KodDnia + (widgetId * 2) + (delta > 0 ? 1 : 0),
            zamiar,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }

    private static PendingIntent? OtworzIntent(Context context)
    {
        var zamiar = new Intent(context, typeof(MainActivity));
        zamiar.SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        zamiar.PutExtra(MainActivity.KalendarzExtra, true);

        return PendingIntent.GetActivity(
            context, KodOtwarcia, zamiar,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action != CompleteAction)
        {
            base.OnReceive(context, intent);
            return;
        }

        // Jedna akcja, trzy drogi. Lista ma jeden wzorzec zamiaru na wszystkie wiersze,
        // więc to, co wiersz chce zgłosić, może przyjechać wyłącznie jako dodatek.
        // Skoro tak, to i strzałki dni idą tą samą drogą — dwie drogi do jednego
        // odbiornika znaczyłyby dwa miejsca, w których trzeba pamiętać o tej regule.
        var co = intent.GetStringExtra(CoExtra) ?? CoZrobione;
        var okno = context.ApplicationContext ?? context;

        if (co == CoOtworz)
        {
            // Z odbiornika, a nie zamiarem oczekującym: wiersz listy nie ma własnego
            // zamiaru, a wzorzec jest rozgłoszeniem i okna nie otworzy.
            var doOkna = new Intent(okno, typeof(MainActivity));
            doOkna.SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
            doOkna.PutExtra(MainActivity.KalendarzExtra, true);
            okno.StartActivity(doOkna);
            return;
        }

        if (co == CoDzien)
        {
            var widgetId = intent.GetIntExtra(
                AppWidgetManager.ExtraAppwidgetId, AppWidgetManager.InvalidAppwidgetId);

            if (widgetId == AppWidgetManager.InvalidAppwidgetId
                || AppWidgetManager.GetInstance(okno) is not { } menedzer)
            {
                return;
            }

            Przesun(okno, widgetId, intent.GetIntExtra(DeltaExtra, 0));

            // Rama **i** dane listy: rama niesie nazwę dnia, lista jego zawartość.
            // Jedno bez drugiego pokazałoby nagłówek „Jutro" nad planem na dziś.
            Przerysuj(okno, menedzer, [widgetId]);

#pragma warning disable CA1422
            menedzer.NotifyAppWidgetViewDataChanged(new[] { widgetId }, Resource.Id.lista);
#pragma warning restore CA1422
            return;
        }

        var id = intent.GetStringExtra(TaskIdExtra);
        var oczekiwanie = GoAsync();

        _ = Task.Run(async () =>
        {
            try
            {
                if (Guid.TryParse(id, out var zadanie))
                {
                    var services = await ServicesAsync(okno);
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
            context, KodWrzutu, zamiar,
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

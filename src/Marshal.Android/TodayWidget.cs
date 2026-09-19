using System.Globalization;
using System.Runtime.Versioning;
using Android.App;
using Marshal.Application.Abstractions;
using Android.Appwidget;
using Android.Content;
using Android.Widget;
using Marshal.Application.UseCases;
using Marshal.Domain.Diagnostics;
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
/// Treść planu liczy <see cref="DayPlanService"/>, nie ta klasa. Tu zostaje samo
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
    public const string OpenWhatExtra = CoExtra;

    private const string WhatDone = "zrobione";

    public const string OpenWhat = "otworz";

    private const string Daily = "dzien";

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
    private const string Settings = "widget";

    /// <summary>Ta sama składnica dla wierszy zapamiętanych przez listę kafelka.</summary>
    /// <remarks>
    /// Wspólna nazwa, bo to ten sam kafelek i ten sam rodzaj stanu: nie dane aplikacji,
    /// tylko to, co jeden prostokąt na jednym ekranie domowym ma o sobie pamiętać.
    /// Zapomina się o tym razem z nim, w jednym miejscu.
    /// </remarks>
    internal const string Memory = Settings;

    internal static int Offset(Context context, int widgetId) =>
        context.GetSharedPreferences(Settings, FileCreationMode.Private)
            ?.GetInt($"dzien-{widgetId}", 0) ?? 0;

    private static void Move(Context context, int widgetId, int delta)
    {
        if (context.GetSharedPreferences(Settings, FileCreationMode.Private) is not { } patch)
        {
            return;
        }

        var fresh = Offset(context, widgetId) + delta;

        // Granica po obu stronach: kilkadziesiąt dotknięć strzałki w jedną stronę nie ma
        // wyprowadzać widgetu w miejsce, z którego nie widać, jak wrócić. Rok w każdą
        // stronę, bo strzałka przesuwa teraz o tydzień, a nie o dzień — przy dawnych
        // dwóch tygodniach druga strzałka już nic by nie robiła.
        fresh = Math.Clamp(fresh, -366, 366);

        patch.Edit()?.PutInt($"dzien-{widgetId}", fresh)?.Apply();
    }

    /// <summary>Zapomnienie ustawienia widgetu, który zdjęto z ekranu.</summary>
    public override void OnDeleted(Context? context, int[]? appWidgetIds)
    {
        base.OnDeleted(context, appWidgetIds);

        if (context?.GetSharedPreferences(Settings, FileCreationMode.Private) is not { } patch
            || appWidgetIds is null)
        {
            return;
        }

        var edit = patch.Edit();

        foreach (var id in appWidgetIds)
        {
            edit?.Remove($"dzien-{id}");
            edit?.Remove($"wiersze-{id}");
        }

        edit?.Apply();
    }

    /// <summary>
    /// Kody żądań dla zamiarów oczekujących.
    /// </summary>
    /// <remarks>
    /// Różne, bo system porównuje zamiary **bez patrzenia na dodatkowe dane**. Przy
    /// wspólnym kodzie wzorzec odhaczenia i otwarcie aplikacji byłyby dla niego jednym
    /// zamiarem, a drugie zastępowałoby pierwsze.
    /// </remarks>
    private const int RowCode = 0;

    private const int OpenCode = 1;

    private const int CaptureCode = 2;

    /// <summary>Od tego numeru w górę idą dotknięcia dni — po dziewięć na każdy kafelek.</summary>
    /// <remarks>
    /// Dziewięć: siedem kolumn paska tygodnia i dwie strzałki. Numer musi być różny dla
    /// każdego z nich i dla każdego kafelka osobno, bo system porównuje zamiary
    /// <b>bez patrzenia na dodatkowe dane</b> — przy wspólnym numerze wszystkie
    /// dotknięcia byłyby dla niego jednym zamiarem i każde przesuwałoby to samo.
    /// </remarks>
    private const int DayCode = 1000;

    /// <summary>Ile numerów żądania przypada na jeden kafelek.</summary>
    private const int CodesPerTile = 16;

    /// <summary>Ile dni pokazuje pasek. Tydzień, bo tydzień jest jednostką planowania.</summary>
    private const int WeekDays = 7;

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
        manager.NotifyAppWidgetViewDataChanged(ids, Resource.Id.list);
#pragma warning restore CA1422

        var intent = new Intent(context, typeof(TodayWidget));
        intent.SetAction(AppWidgetManager.ActionAppwidgetUpdate);
        intent.PutExtra(AppWidgetManager.ExtraAppwidgetIds, ids);

        context.SendBroadcast(intent);
    }

    public override void OnUpdate(
        Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is null || appWidgetManager is null || appWidgetIds is null)
        {
            return;
        }

        Redraw(context, appWidgetManager, appWidgetIds);
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
    /// <summary>
    /// Ile rozgłoszenie czeka na przygotowanie, zanim odda swój budżet czasu.
    /// </summary>
    /// <remarks>
    /// Odbiornik rozgłoszenia ma na wszystko około dziesięciu sekund i po ich przekroczeniu
    /// system pokazuje „Marshal nie odpowiada". Przygotowanie potrafi trwać dłużej —
    /// w dzienniku stoi przypadek, w którym trwało blisko czternaście sekund — więc
    /// czekanie na nie do skutku jest proszeniem się o to okienko. Trzy sekundy starczają
    /// na proces, który już stoi; zimny i tak nie zmieści się w budżecie, więc lepiej
    /// oddać rozgłoszenie i dokończyć bez niego.
    /// </remarks>
    private static readonly TimeSpan BroadcastBudget = TimeSpan.FromSeconds(3);

    private void Redraw(Context context, AppWidgetManager manager, int[] ids)
    {
        var window = context;
        var waiting = GoAsync();

        _ = Task.Run(async () =>
        {
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();

                // Rozgłoszenie oddawane po budżecie, a nie po dojściu do końca. Praca
                // leci dalej: „UpdateAppWidget" nie wymaga trwającego rozgłoszenia,
                // wymaga tylko żywego procesu — a ten stoi, bo właśnie się przygotowuje.
                var prepare = AppServices.ReadyAsync();

                if (await Task.WhenAny(prepare, Task.Delay(BroadcastBudget)) != prepare)
                {
                    waiting?.Finish();
                    waiting = null;
                }

                var services = await ServicesAsync(window.ApplicationContext ?? window);
                var ready = clock.ElapsedMilliseconds;

                var today = services.GetRequiredService<IClock>().Today;
                var plan = services.GetRequiredService<DayPlanService>();

                foreach (var id in ids)
                {
                    // Kropki liczone na kafelek, nie raz na wszystkie: dwa kafelki obok
                    // siebie mają prawo oglądać dwa różne tygodnie — po to się je stawia
                    // dwa — a wspólny tydzień oznaczyłby kropki jednego z nich na drugim.
                    var shown = today.AddDays(Offset(window, id));
                    var monday = shown.AddDays(-(((int)shown.DayOfWeek + 6) % 7));

                    var busy = await plan.BusyAsync(monday, WeekDays);

                    manager.UpdateAppWidget(id, Frame(window, id, today, busy));
                }

                // Świeże wydarzenia dla procesu bez okna: widget budzony budzikiem jest
                // wtedy jedynym, który je pokaże. Bez wymuszania, więc gdy okno odświeżyło
                // swoją drogą, jest to sprawdzenie odstępu, a nie drugie pobranie —
                // i bez czekania, bo kafelek jest już narysowany z tego, co w bazie.
                _ = Marshal.Infrastructure.DependencyInjection.RefreshCalendarsAsync(services);

                // Przerysowanie kafelka idzie w odbiorniku rozgłoszenia, czyli z budżetem
                // czasu, którego nie widać. Kropki w pasku tygodnia liczą siedem planów
                // dnia na kafelek — a plan dnia sięga do zadań, projektów, obszarów
                // i kalendarza. Dopisywane tylko wtedy, gdy trwa **długo**: wpis przy
                // każdym przerysowaniu byłby szumem, a tu chodzi o jedną liczbę, której
                // nie da się zmierzyć inaczej niż stąd.
                if (clock.ElapsedMilliseconds > 1000)
                {
                    await services.GetRequiredService<IActivityLog>().RecordAsync(
                        "Widget: przerysowanie",
                        $"{clock.ElapsedMilliseconds} ms na {ids.Length}, "
                            + $"w tym czekanie na bazę {ready} ms",
                        ActivityLevel.Problem);
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
                waiting?.Finish();
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
    private static RemoteViews Frame(
        Context context, int widgetId, DateOnly today, IReadOnlySet<DateOnly> busy)
    {
        var view = new RemoteViews(context.PackageName, Resource.Layout.widget_marshal);
        var offset = Offset(context, widgetId);
        var shown = today.AddDays(offset);

        view.SetTextViewText(Resource.Id.heading, Month(shown, today));

        // Strzałki po tygodniu, bo pasek pokazuje tydzień. Przesuwanie o dzień przy
        // widocznym tygodniu znaczyłoby, że pierwsze dotknięcie prawie nic nie zmienia
        // — a druga strzałka przesuwa to, co już widać.
        view.SetOnClickPendingIntent(
            Resource.Id.back, DayIntent(context, widgetId, -WeekDays, 7));
        view.SetOnClickPendingIntent(
            Resource.Id.forward, DayIntent(context, widgetId, +WeekDays, 8));

        WeekStrip(context, view, widgetId, shown, today, busy);

        // Dotknięcie samego kafelka otwiera kalendarz: widget odpowiada na pytanie
        // „co dziś", a kalendarz jest tym samym pytaniem zadanym szerzej. Na pustym
        // planie kafelek zakrywa napis „nic nie zaplanowane", więc i on prowadzi tam samo.
        view.SetOnClickPendingIntent(Resource.Id.root, OpenIntent(context));
        view.SetOnClickPendingIntent(Resource.Id.empty, OpenIntent(context));

        // Wrzut otwiera aplikację, a nie pole tekstowe w widgecie: RemoteViews nie zna
        // pola do wpisywania, a wszystko inne znaczy drugi ekran do utrzymywania.
        //
        // Wypadło przy przebudowie na listę i przez to przycisk nie robił nic — a nic
        // nie robi też przycisk, którego zapomniano podpiąć, i przycisk zasłonięty przez
        // cudze dotknięcie. Z zewnątrz wyglądają identycznie.
        view.SetOnClickPendingIntent(Resource.Id.capture, LaunchIntent(context));

        var toService = new Intent(context, typeof(TodayWidgetService));
        toService.PutExtra(AppWidgetManager.ExtraAppwidgetId, widgetId);
        toService.SetData(global::Android.Net.Uri.Parse(toService.ToUri(IntentUriType.Scheme)));

        // Wskazanie usługi zamiarem jest od Androida 15 oznaczone jako przestarzałe na
        // rzecz podawania wierszy wprost w RemoteViews. Tamta droga nie zna usługi
        // dostarczającej wiersze, więc nie jest zamiennikiem dla listy, która czyta
        // bazę — jest zamiennikiem dla listy krótkiej i znanej z góry. Nadal działa
        // i nadal jest jedyną drogą dla listy budowanej po stronie aplikacji.
#pragma warning disable CA1422
        view.SetRemoteAdapter(Resource.Id.list, toService);
#pragma warning restore CA1422

        // Napis zamiast pustej listy — system podmienia je sam, więc nie trzeba
        // zgadywać, czy plan jest pusty, zanim lista go wczyta.
        view.SetEmptyView(Resource.Id.list, Resource.Id.empty);

        view.SetPendingIntentTemplate(Resource.Id.list, RowTemplate(context));

        return view;
    }

    /// <summary>
    /// Nagłówek: miesiąc oglądanego dnia, z rokiem tylko wtedy, gdy nie jest bieżący.
    /// </summary>
    /// <remarks>
    /// Był tu dzień — „Na dziś", „Jutro", data. Odkąd pod nagłówkiem stoi pasek tygodnia
    /// z numerami dni, powtarzanie w nim dnia byłoby drugą odpowiedzią na to samo,
    /// a miesiąca nie mówiło nic: same numery od 14 do 20 nie mówią, czy to wrzesień,
    /// czy październik, akurat wtedy, gdy tydzień wypada na przełomie.
    ///
    /// Rok tylko nie-bieżący, bo dopisany zawsze byłby stałą, którą się przestaje czytać
    /// — a wtedy przestaje się ją czytać także wtedy, gdy naprawdę coś mówi.
    /// </remarks>
    private static string Month(DateOnly shown, DateOnly today)
    {
        var name = shown.ToString("MMMM", CultureInfo.CurrentCulture);

        var capitalized = name.Length > 0
            ? char.ToUpper(name[0], CultureInfo.CurrentCulture) + name[1..]
            : name;

        return shown.Year == today.Year ? capitalized : $"{capitalized} {shown.Year}";
    }

    /// <summary>
    /// Pasek tygodnia: siedem kolumn, oglądany dzień podkreślony, zajęte z kropką.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tydzień zaczyna się w poniedziałek niezależnie od ustawień systemu. To nie jest
    /// przeoczenie: plan tygodnia w tej aplikacji jest tygodniem roboczym z weekendem na
    /// końcu, a pasek ma stać tak samo jak siatka tygodnia w samej aplikacji. Dwa różne
    /// początki tygodnia w jednym programie to dwa różne tygodnie.
    /// </para>
    /// <para>
    /// Kropka i kreska znikają przez podmianę tła na przezroczyste, a nie przez ukrycie.
    /// Ukryty widok oddaje swoje miejsce, a oddane miejsce przesuwa numery w kolumnach
    /// obok — pasek przestałby być siatką i numery skakałyby przy każdej zmianie planu.
    /// </para>
    /// </remarks>
    private static void WeekStrip(
        Context context,
        RemoteViews view,
        int widgetId,
        DateOnly shown,
        DateOnly today,
        IReadOnlySet<DateOnly> busy)
    {
        var monday = shown.AddDays(-(((int)shown.DayOfWeek + 6) % 7));

        for (var i = 0; i < WeekDays; i++)
        {
            var day = monday.AddDays(i);
            var selected = day == shown;

            view.SetTextViewText(Names[i], day.ToString("ddd", CultureInfo.CurrentCulture));
            view.SetTextViewText(Numbers[i], day.Day.ToString(CultureInfo.CurrentCulture));

            // Dzisiejszy dzień w barwie wyróżnienia nawet wtedy, gdy ogląda się inny:
            // kafelek stoi na ekranie domowym i pierwsze pytanie do niego brzmi
            // „gdzie jestem", a dopiero drugie „na co patrzę".
            //
            // Przez SetInt na setTextColor, a nie przez SetTextColor: to drugie chce
            // typu barwy Androida, a zasób oddaje liczbę. Jedna droga mniej do pomylenia.
            view.SetInt(
                Numbers[i],
                "setTextColor",
                context.GetColor(day == today ? Resource.Color.accent : Resource.Color.text));

            view.SetInt(
                Dots[i],
                "setBackgroundResource",
                busy.Contains(day) ? Resource.Drawable.widget_dot : Resource.Drawable.transparent);

            view.SetInt(
                Dashes[i],
                "setBackgroundResource",
                selected ? Resource.Drawable.widget_dash : Resource.Drawable.transparent);

            // Dotknięcie kolumny przestawia widget na ten dzień. Przez różnicę, bo
            // przesunięcie liczone jest od dzisiejszego dnia i tak je zapisujemy.
            view.SetOnClickPendingIntent(
                Columns[i],
                DayIntent(context, widgetId, day.DayNumber - shown.DayNumber, i));
        }
    }

    private static readonly int[] Columns =
    [
        Resource.Id.day0, Resource.Id.day1, Resource.Id.day2, Resource.Id.day3,
        Resource.Id.day4, Resource.Id.day5, Resource.Id.day6,
    ];

    private static readonly int[] Names =
    [
        Resource.Id.name0, Resource.Id.name1, Resource.Id.name2, Resource.Id.name3,
        Resource.Id.name4, Resource.Id.name5, Resource.Id.name6,
    ];

    private static readonly int[] Numbers =
    [
        Resource.Id.number0, Resource.Id.number1, Resource.Id.number2, Resource.Id.number3,
        Resource.Id.number4, Resource.Id.number5, Resource.Id.number6,
    ];

    private static readonly int[] Dots =
    [
        Resource.Id.dot0, Resource.Id.dot1, Resource.Id.dot2, Resource.Id.dot3,
        Resource.Id.dot4, Resource.Id.dot5, Resource.Id.dot6,
    ];

    private static readonly int[] Dashes =
    [
        Resource.Id.dash0, Resource.Id.dash1, Resource.Id.dash2, Resource.Id.dash3,
        Resource.Id.dash4, Resource.Id.dash5, Resource.Id.dash6,
    ];

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
    private static PendingIntent? RowTemplate(Context context)
    {
        var intent = new Intent(context, typeof(TodayWidget));
        intent.SetAction(CompleteAction);

        return PendingIntent.GetBroadcast(
            context, RowCode, intent, TemplateMarks());
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
    private static PendingIntentFlags TemplateMarks() =>
        OperatingSystem.IsAndroidVersionAtLeast(31)
            ? PendingIntentFlags.UpdateCurrent | Inexact()
            : PendingIntentFlags.UpdateCurrent;

    /// <summary>Osobno i z adnotacją wydania — inaczej analizator czyta to jako zwykłą liczbę.</summary>
    [SupportedOSPlatform("android31.0")]
    private static PendingIntentFlags Inexact() => PendingIntentFlags.Mutable;

    /// <summary>
    /// Strzałka przesuwająca dzień.
    /// </summary>
    /// <remarks>
    /// Kod żądania niesie identyfikator widgetu i kierunek, bo system porównuje zamiary
    /// <b>bez patrzenia na dodatkowe dane</b>. Przy wspólnym kodzie obie strzałki
    /// wszystkich kafelków byłyby dla niego jednym zamiarem i każda przesuwałaby to samo.
    /// </remarks>
    private static PendingIntent? DayIntent(Context context, int widgetId, int delta, int socket)
    {
        var intent = new Intent(context, typeof(TodayWidget));
        intent.SetAction(CompleteAction);
        intent.PutExtra(CoExtra, Daily);
        intent.PutExtra(DeltaExtra, delta);
        intent.PutExtra(AppWidgetManager.ExtraAppwidgetId, widgetId);

        return PendingIntent.GetBroadcast(
            context,
            DayCode + (widgetId * CodesPerTile) + socket,
            intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }

    private static PendingIntent? OpenIntent(Context context)
    {
        var intent = new Intent(context, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        intent.PutExtra(MainActivity.CalendarExtra, true);

        return PendingIntent.GetActivity(
            context, OpenCode, intent,
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
        var what = intent.GetStringExtra(CoExtra) ?? WhatDone;
        var window = context.ApplicationContext ?? context;

        if (what == OpenWhat)
        {
            // Z odbiornika, a nie zamiarem oczekującym: wiersz listy nie ma własnego
            // zamiaru, a wzorzec jest rozgłoszeniem i okna nie otworzy.
            var toWindow = new Intent(window, typeof(MainActivity));
            toWindow.SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
            toWindow.PutExtra(MainActivity.CalendarExtra, true);

            // Zadanie, jeśli dotknięto pozycji, a nie samego kafelka. Wejście „gdzieś
            // w okolice" znaczyło szukanie wzrokiem po siatce tego, co przed chwilą
            // stało pod palcem.
            if (intent.GetStringExtra(TaskIdExtra) is { Length: > 0 } task)
            {
                toWindow.PutExtra(MainActivity.TaskExtra, task);
            }

            window.StartActivity(toWindow);
            return;
        }

        if (what == Daily)
        {
            var widgetId = intent.GetIntExtra(
                AppWidgetManager.ExtraAppwidgetId, AppWidgetManager.InvalidAppwidgetId);

            if (widgetId == AppWidgetManager.InvalidAppwidgetId
                || AppWidgetManager.GetInstance(window) is not { } manager)
            {
                return;
            }

            Move(window, widgetId, intent.GetIntExtra(DeltaExtra, 0));

            // Rama **i** dane listy: rama niesie nazwę dnia, lista jego zawartość.
            // Jedno bez drugiego pokazałoby nagłówek „Jutro" nad planem na dziś.
            Redraw(window, manager, [widgetId]);

#pragma warning disable CA1422
            manager.NotifyAppWidgetViewDataChanged(new[] { widgetId }, Resource.Id.list);
#pragma warning restore CA1422
            return;
        }

        var id = intent.GetStringExtra(TaskIdExtra);
        var waiting = GoAsync();

        _ = Task.Run(async () =>
        {
            try
            {
                if (Guid.TryParse(id, out var task))
                {
                    var services = await ServicesAsync(window);
                    await services.GetRequiredService<TaskEditService>().CompleteAsync(task);
                }

                Refresh(window);
            }
            catch (Exception e)
            {
                global::Android.Util.Log.Warn("Marshal", e.ToString());
            }
            finally
            {
                // GoAsync zwraca wartość pustą, gdy odbiornik nie działa w tle —
                // wtedy nie ma czego kończyć.
                waiting?.Finish();
            }
        });
    }

    private static PendingIntent? LaunchIntent(Context context)
    {
        var intent = new Intent(context, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);

        return PendingIntent.GetActivity(
            context, CaptureCode, intent,
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
    private static async Task<IServiceProvider> ServicesAsync(Context context)
    {
        // Haczyk na dymki **przed** składaniem: składanie nadrabia zaległe przypomnienia,
        // a widget potrafi być pierwszy w procesie i jedyny. Bez tego odświeżenie widgetu
        // o siódmej rano zjadało przypomnienie ustawione na siódmą — zapisywało je jako
        // pokazane i nie pokazywało.
        Notifications.Hook(context);

        await AppServices.ReadyAsync();
        return AppServices.Provider;
    }
}

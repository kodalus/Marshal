using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Views;
using Marshal.Application.Abstractions;
using Android.Widget;
using Marshal.Application.UseCases;
using Marshal.Domain.Diagnostics;
using Marshal.UI;
using Microsoft.Extensions.DependencyInjection;

namespace Marshal.Android;

/// <summary>
/// Dostawca wierszy dla listy w widgecie.
/// </summary>
/// <remarks>
/// <para>
/// Lista w widgecie nie rysuje się w naszym procesie: system pyta tę usługę o wiersze
/// i rozwija je u siebie, w procesie ekranu domowego. Stąd cała ta machina zamiast
/// zwykłej pętli — i stąd uprawnienie, którym system sam się przedstawia.
/// </para>
/// <para>
/// Niewystawiona i zamknięta uprawnieniem <c>BIND_REMOTEVIEWS</c>: wołać ją ma
/// wyłącznie system, a bez tego zamknięcia dowolna aplikacja mogłaby odpytać ją
/// o plan dnia.
/// </para>
/// </remarks>
[Service(Permission = "android.permission.BIND_REMOTEVIEWS", Exported = false)]
public sealed class TodayWidgetService : RemoteViewsService
{
    public override IRemoteViewsFactory OnGetViewFactory(Intent? intent) =>
        new Factory(
            ApplicationContext!,
            intent?.GetIntExtra(
                AppWidgetManager.ExtraAppwidgetId, AppWidgetManager.InvalidAppwidgetId)
                ?? AppWidgetManager.InvalidAppwidgetId);

    /// <summary>
    /// Wiersze planu dnia dla listy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dane wczytuje <see cref="OnDataSetChanged"/>, nie <see cref="GetViewAt"/>.
    /// System woła je w tej kolejności i na wątku, na którym wolno czekać — a
    /// <c>GetViewAt</c> ma już tylko przepisać gotowe do widoku. Odczyt z bazy przy
    /// każdym wierszu byłby tyloma zapytaniami, ile wierszy, i za każdym przewinięciem.
    /// </para>
    /// <para>
    /// Czekanie na zadanie asynchroniczne jest tu w porządku, choć zwykle nie jest:
    /// wątek, na którym system to woła, nie ma kontekstu synchronizacji, więc nie ma
    /// się o co zakleszczyć. Robota i tak musi się skończyć, zanim ta metoda wróci.
    /// </para>
    /// </remarks>
    private sealed class Factory(Context context, int widgetId)
        : Java.Lang.Object, IRemoteViewsFactory
    {
        private IReadOnlyList<PlanRow> _rows = [];

        public int Count => _rows.Count;

        public bool HasStableIds => true;

        public int ViewTypeCount => 1;

        public RemoteViews? LoadingView => null;

        public long GetItemId(int position) => position;

        public void OnCreate()
        {
        }

        /// <summary>
        /// Wczytanie wierszy na żądanie ekranu domowego.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Nigdy nie czekamy tu na przygotowanie bazy.</b> Ta metoda wykonuje się
        /// na wątku, którym ekran domowy pyta nasz proces — a pytanie zadaje wtedy,
        /// gdy proces dopiero wstaje, bo wcześniej go nie było i połączenie z usługą
        /// trzeba nawiązać od nowa. Czekanie na migracje znaczyło więc trzymanie
        /// cudzego wątku przez kilka sekund, w chwili, w której nasz własny start
        /// i tak zajmuje wszystko inne.
        /// </para>
        /// <para>
        /// Zamiast czekać, zostawiamy to, co widget ma, i <b>zamawiamy odświeżenie</b>
        /// na potem. Do tego czasu na kafelku stoi poprzednia lista albo napis o pustym
        /// dniu — czyli coś, co wygląda na odpowiedź, a nie na zawieszenie.
        /// </para>
        /// <para>
        /// Sam odczyt też z ogranicznikiem. Baza jest lokalna i mała, więc przekroczenie
        /// go znaczy, że coś ją akurat trzyma — a wtedy poprzednie wiersze są lepsze
        /// od pustych: pusty kafelek mówi „nic nie masz", a to jest zdanie nieprawdziwe.
        /// </para>
        /// </remarks>
        public void OnDataSetChanged()
        {
            var prepare = AppServices.ReadyAsync();

            if (!prepare.IsCompleted)
            {
                // Zapamiętane wiersze zamiast pustki. „Zostawiamy widgetowi to, co ma"
                // brzmiało rozsądnie i było nieprawdą w jedynym przypadku, w którym
                // to naprawdę boli: zamknięcie aplikacji zabija proces razem z tą
                // fabryką, więc nowa nie ma **nic**. Kafelek gasł wtedy na dwie sekundy
                // i wracał — czyli przez dwie sekundy mówił „nic dziś nie masz".
                if (_rows.Count == 0)
                {
                    _rows = Remembered(context, widgetId);
                }

                _ = AfterPrepareAsync(context);
                return;
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                _rows = LoadAsync(context, widgetId)
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .GetAwaiter()
                    .GetResult();

                Remember(context, widgetId, _rows);

                // Ta metoda wykonuje się na wątku, którym ekran domowy pyta nasz proces,
                // więc każda jej sekunda jest sekundą cudzego czekania. Dopisywane tylko
                // wtedy, gdy trwa długo — to jedyne miejsce, z którego da się tę liczbę
                // zobaczyć bez kabla.
                if (clock.ElapsedMilliseconds > 1000)
                {
                    // Bez czekania: ta metoda i tak trwała już za długo, a wpis
                    // o tym nie ma prawa jej przedłużać.
                    _ = AppServices.Provider.GetRequiredService<IActivityLog>()
                        .RecordAsync(
                            "Widget: wiersze",
                            $"{clock.ElapsedMilliseconds} ms",
                            ActivityLevel.Problem);
                }
            }
            catch (TimeoutException)
            {
                // Zostaje to, co było. Następne odświeżenie i tak przyjdzie — po zapisie
                // albo przy wyjściu z aplikacji.
            }
            catch (Exception e)
            {
                // Pusta lista zamiast wywrotki: usługa, która rzuci, zostawia na
                // ekranie domowym komunikat systemu o zepsutym widgecie.
                _rows = [];
                global::Android.Util.Log.Warn("Marshal", e.ToString());
            }
        }

        /// <summary>
        /// Ostatnio pokazane wiersze, trzymane poza bazą.
        /// </summary>
        /// <remarks>
        /// <para>
        /// W ustawieniach systemu, nie w bazie, i to jest cały sens: kafelek musi umieć
        /// narysować się <b>zanim</b> baza będzie gotowa, bo ekran domowy pyta o wiersze
        /// dokładnie wtedy, gdy proces dopiero wstaje. Odczyt ustawień systemu trwa
        /// ułamek milisekundy i nie zależy od niczego, co się właśnie przygotowuje.
        /// </para>
        /// <para>
        /// To nie są dane aplikacji, tylko ostatni obrazek jednego kafelka — dlatego
        /// leżą tam, gdzie jego przesunięcie dnia, i giną razem z nim.
        /// </para>
        /// <para>
        /// Własny zapis, nie JSON: wydanie na Androida jest przycinane, a serializacja
        /// przez odbicie typów potrafi się wtedy wywrócić dopiero na urządzeniu.
        /// Cztery pola rozdzielone znakami, których w tekście nie ma, wywrócić się
        /// nie mają jak.
        /// </para>
        /// </remarks>
        private const char Between = '\u001f';

        private const char Row = '\u001e';

        private static string Key(int widgetId) => $"wiersze-{widgetId}";

        private static IReadOnlyList<PlanRow> Remembered(Context context, int widgetId)
        {
            try
            {
                var patch = context
                    .GetSharedPreferences(TodayWidget.Memory, FileCreationMode.Private)
                    ?.GetString(Key(widgetId), null);

                if (string.IsNullOrEmpty(patch))
                {
                    return [];
                }

                return patch
                    .Split(Row, StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => w.Split(Between))
                    .Where(p => p.Length == 4)
                    .Select(p => new PlanRow(
                        Guid.TryParse(p[0], out var task) ? task : null,
                        p[1],
                        p[2],
                        p[3].Length == 0 ? null : p[3]))
                    .ToList();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Zapamiętany obrazek jest wygodą, nie prawdą. Jego brak znaczy pusty
                // kafelek na dwie sekundy, a nie awarię.
                global::Android.Util.Log.Warn("Marshal", e.ToString());

                return [];
            }
        }

        private static void Remember(
            Context context, int widgetId, IReadOnlyList<PlanRow> rows)
        {
            try
            {
                var patch = string.Join(
                    Row,
                    rows.Select(w => string.Join(
                        Between,
                        w.TaskId?.ToString() ?? string.Empty,
                        Clear(w.Title),
                        Clear(w.Caption),
                        Clear(w.Color ?? string.Empty))));

                context.GetSharedPreferences(TodayWidget.Memory, FileCreationMode.Private)
                    ?.Edit()?.PutString(Key(widgetId), patch)?.Apply();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                global::Android.Util.Log.Warn("Marshal", e.ToString());
            }
        }

        /// <summary>Bez znaków rozdzielających — tytuł jest cudzym tekstem.</summary>
        private static string Clear(string text) =>
            text.Replace(Between, ' ').Replace(Row, ' ');

        /// <summary>Ponowna prośba o wiersze, gdy baza będzie już gotowa.</summary>
        /// <remarks>
        /// Przez zwykłe odświeżenie widgetu, a nie przez zapisanie wierszy tutaj:
        /// ta fabryka może już wtedy nie istnieć, a system i tak pyta o wiersze
        /// wyłącznie tę, którą sam trzyma.
        /// </remarks>
        private static async Task AfterPrepareAsync(Context context)
        {
            try
            {
                await AppServices.ReadyAsync();

                TodayWidget.Refresh(context);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                global::Android.Util.Log.Warn("Marshal", e.ToString());
            }
        }

        public void OnDestroy() => _rows = [];

        public RemoteViews? GetViewAt(int position)
        {
            if (position < 0 || position >= _rows.Count)
            {
                return null;
            }

            var item = _rows[position];
            var view = new RemoteViews(context.PackageName, Resource.Layout.widget_wiersz);

            view.SetTextViewText(Resource.Id.title, item.Title);
            view.SetTextViewText(Resource.Id.caption, item.Caption);
            view.SetInt(Resource.Id.strip, "setBackgroundColor", Color(item.Color));

            // Uzupełnienie wzorca, nie własny zamiar: wierszowi listy nie da się dać
            // osobnego zamiaru oczekującego — system trzyma jeden wzorzec na całą listę
            // i dokłada do niego to, co wiersz tu wpisze.
            //
            // Wydarzenie z cudzego kalendarza nie ma czego odhaczyć jednym dotknięciem:
            // ptaszek idzie tam przez sieć, a widget nie ma jak poczekać ani pokazać,
            // że czeka. Kwadracik zostaje wtedy schowany — niewidoczny, a nie wyłączony,
            // bo wyłączony wyglądałby na zepsuty. Miejsce po nim zostaje, żeby wiersze
            // miały wspólną krawędź tekstu.
            if (item.TaskId is { } task)
            {
                view.SetViewVisibility(Resource.Id.done, ViewStates.Visible);

                var completion = new Intent();
                completion.PutExtra(TodayWidget.TaskIdExtra, task.ToString());

                view.SetOnClickFillInIntent(Resource.Id.done, completion);
            }
            else
            {
                view.SetViewVisibility(Resource.Id.done, ViewStates.Invisible);
            }

            // Treść wiersza otwiera aplikację. **Rodzeństwo kwadracika, nie jego rodzic:**
            // pierwsza wersja dawała ten zamiar korzeniowi wiersza, czyli czemuś, co
            // zawiera w sobie kwadracik — a wtedy o to, które dotknięcie wygrywa,
            // rozstrzyga launcher. Odhaczenie po prostu nie działało: dotknięcie szło
            // do wiersza i otwierało aplikację.
            //
            // Przy wydarzeniu bez identyfikatora zadania otwiera się sam kalendarz —
            // czyli to samo miejsce, tyle że bez wskazania na konkretny wpis.
            var opening = new Intent();
            opening.PutExtra(TodayWidget.OpenWhatExtra, TodayWidget.OpenWhat);

            if (item.TaskId is { } otwierane)
            {
                opening.PutExtra(TodayWidget.TaskIdExtra, otwierane.ToString());
            }

            view.SetOnClickFillInIntent(Resource.Id.content, opening);

            return view;
        }

        /// <summary>
        /// Wiersze na dzień, który ogląda **ten** widget.
        /// </summary>
        /// <remarks>
        /// Przesunięcie dnia czytane tutaj, a nie podane przy tworzeniu fabryki. Fabryka
        /// powstaje raz i żyje dłużej niż jedno dotknięcie strzałki; wartość zapamiętana
        /// przy jej powstaniu byłaby tą sprzed wszystkich przesunięć.
        ///
        /// Brak tego odczytu był całą usterką „przełączam na jutro, a widzę dzisiejsze":
        /// nagłówek brał przesunięcie z ramy i zmieniał się poprawnie, a lista ładowała
        /// zawsze dzisiaj. Wyglądało to na nieodświeżoną listę, a było listą, która
        /// nigdy nie wiedziała, o który dzień pytać.
        /// </remarks>
        private static async Task<IReadOnlyList<PlanRow>> LoadAsync(
            Context context, int widgetId)
        {
            await AppServices.ReadyAsync();

            var services = AppServices.Provider;
            var day = services.GetRequiredService<IClock>().Today
                .AddDays(TodayWidget.Offset(context, widgetId));

            return await services.GetRequiredService<DayPlanService>().ForDayAsync(day);
        }

        /// <summary>
        /// Zapis barwy na liczbę.
        /// </summary>
        /// <remarks>
        /// Zapis jest tekstem wpisanym przez człowieka, więc może być czymkolwiek.
        /// Wywrotka przy rysowaniu wiersza nie daje żadnego objawu poza zepsutą listą
        /// na ekranie domowym, więc zły zapis schodzi na barwę domyślną.
        /// </remarks>
        private static int Color(string? patch)
        {
            if (string.IsNullOrWhiteSpace(patch))
            {
                return Accent;
            }

            try
            {
                return global::Android.Graphics.Color.ParseColor(patch).ToArgb();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return Accent;
            }
        }

        /// <summary>Barwa domyślna paska — ta sama, co akcent aplikacji.</summary>
        private const int Accent = unchecked((int)0xFF7C6CF5);
    }
}

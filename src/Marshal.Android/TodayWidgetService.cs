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
        new Fabryka(
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
    private sealed class Fabryka(Context kontekst, int widgetId)
        : Java.Lang.Object, IRemoteViewsFactory
    {
        private IReadOnlyList<PlanRow> _wiersze = [];

        public int Count => _wiersze.Count;

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
            var przygotowanie = AppServices.ReadyAsync();

            if (!przygotowanie.IsCompleted)
            {
                // Zapamiętane wiersze zamiast pustki. „Zostawiamy widgetowi to, co ma"
                // brzmiało rozsądnie i było nieprawdą w jedynym przypadku, w którym
                // to naprawdę boli: zamknięcie aplikacji zabija proces razem z tą
                // fabryką, więc nowa nie ma **nic**. Kafelek gasł wtedy na dwie sekundy
                // i wracał — czyli przez dwie sekundy mówił „nic dziś nie masz".
                if (_wiersze.Count == 0)
                {
                    _wiersze = Zapamietane(kontekst, widgetId);
                }

                _ = PoPrzygotowaniuAsync(kontekst);
                return;
            }

            var zegar = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                _wiersze = WczytajAsync(kontekst, widgetId)
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .GetAwaiter()
                    .GetResult();

                Zapamietaj(kontekst, widgetId, _wiersze);

                // Ta metoda wykonuje się na wątku, którym ekran domowy pyta nasz proces,
                // więc każda jej sekunda jest sekundą cudzego czekania. Dopisywane tylko
                // wtedy, gdy trwa długo — to jedyne miejsce, z którego da się tę liczbę
                // zobaczyć bez kabla.
                if (zegar.ElapsedMilliseconds > 1000)
                {
                    // Bez czekania: ta metoda i tak trwała już za długo, a wpis
                    // o tym nie ma prawa jej przedłużać.
                    _ = AppServices.Provider.GetRequiredService<IActivityLog>()
                        .RecordAsync(
                            "Widget: wiersze",
                            $"{zegar.ElapsedMilliseconds} ms",
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
                _wiersze = [];
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
        private const char Miedzy = '\u001f';

        private const char Wiersz = '\u001e';

        private static string Klucz(int widgetId) => $"wiersze-{widgetId}";

        private static IReadOnlyList<PlanRow> Zapamietane(Context kontekst, int widgetId)
        {
            try
            {
                var zapis = kontekst
                    .GetSharedPreferences(TodayWidget.Pamiec, FileCreationMode.Private)
                    ?.GetString(Klucz(widgetId), null);

                if (string.IsNullOrEmpty(zapis))
                {
                    return [];
                }

                return zapis
                    .Split(Wiersz, StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => w.Split(Miedzy))
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

        private static void Zapamietaj(
            Context kontekst, int widgetId, IReadOnlyList<PlanRow> wiersze)
        {
            try
            {
                var zapis = string.Join(
                    Wiersz,
                    wiersze.Select(w => string.Join(
                        Miedzy,
                        w.TaskId?.ToString() ?? string.Empty,
                        Czysto(w.Title),
                        Czysto(w.Caption),
                        Czysto(w.Color ?? string.Empty))));

                kontekst.GetSharedPreferences(TodayWidget.Pamiec, FileCreationMode.Private)
                    ?.Edit()?.PutString(Klucz(widgetId), zapis)?.Apply();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                global::Android.Util.Log.Warn("Marshal", e.ToString());
            }
        }

        /// <summary>Bez znaków rozdzielających — tytuł jest cudzym tekstem.</summary>
        private static string Czysto(string tekst) =>
            tekst.Replace(Miedzy, ' ').Replace(Wiersz, ' ');

        /// <summary>Ponowna prośba o wiersze, gdy baza będzie już gotowa.</summary>
        /// <remarks>
        /// Przez zwykłe odświeżenie widgetu, a nie przez zapisanie wierszy tutaj:
        /// ta fabryka może już wtedy nie istnieć, a system i tak pyta o wiersze
        /// wyłącznie tę, którą sam trzyma.
        /// </remarks>
        private static async Task PoPrzygotowaniuAsync(Context kontekst)
        {
            try
            {
                await AppServices.ReadyAsync();

                TodayWidget.Refresh(kontekst);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                global::Android.Util.Log.Warn("Marshal", e.ToString());
            }
        }

        public void OnDestroy() => _wiersze = [];

        public RemoteViews? GetViewAt(int position)
        {
            if (position < 0 || position >= _wiersze.Count)
            {
                return null;
            }

            var pozycja = _wiersze[position];
            var widok = new RemoteViews(kontekst.PackageName, Resource.Layout.widget_wiersz);

            widok.SetTextViewText(Resource.Id.title, pozycja.Title);
            widok.SetTextViewText(Resource.Id.podpis, pozycja.Caption);
            widok.SetInt(Resource.Id.pasek, "setBackgroundColor", Color(pozycja.Color));

            // Uzupełnienie wzorca, nie własny zamiar: wierszowi listy nie da się dać
            // osobnego zamiaru oczekującego — system trzyma jeden wzorzec na całą listę
            // i dokłada do niego to, co wiersz tu wpisze.
            //
            // Wydarzenie z cudzego kalendarza nie ma czego odhaczyć jednym dotknięciem:
            // ptaszek idzie tam przez sieć, a widget nie ma jak poczekać ani pokazać,
            // że czeka. Kwadracik zostaje wtedy schowany — niewidoczny, a nie wyłączony,
            // bo wyłączony wyglądałby na zepsuty. Miejsce po nim zostaje, żeby wiersze
            // miały wspólną krawędź tekstu.
            if (pozycja.TaskId is { } task)
            {
                widok.SetViewVisibility(Resource.Id.zrobione, ViewStates.Visible);

                var odhaczenie = new Intent();
                odhaczenie.PutExtra(TodayWidget.TaskIdExtra, task.ToString());

                widok.SetOnClickFillInIntent(Resource.Id.zrobione, odhaczenie);
            }
            else
            {
                widok.SetViewVisibility(Resource.Id.zrobione, ViewStates.Invisible);
            }

            // Treść wiersza otwiera aplikację. **Rodzeństwo kwadracika, nie jego rodzic:**
            // pierwsza wersja dawała ten zamiar korzeniowi wiersza, czyli czemuś, co
            // zawiera w sobie kwadracik — a wtedy o to, które dotknięcie wygrywa,
            // rozstrzyga launcher. Odhaczenie po prostu nie działało: dotknięcie szło
            // do wiersza i otwierało aplikację.
            //
            // Przy wydarzeniu bez identyfikatora zadania otwiera się sam kalendarz —
            // czyli to samo miejsce, tyle że bez wskazania na konkretny wpis.
            var otwarcie = new Intent();
            otwarcie.PutExtra(TodayWidget.CoOtworzExtra, TodayWidget.CoOtworz);

            if (pozycja.TaskId is { } otwierane)
            {
                otwarcie.PutExtra(TodayWidget.TaskIdExtra, otwierane.ToString());
            }

            widok.SetOnClickFillInIntent(Resource.Id.content, otwarcie);

            return widok;
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
        private static async Task<IReadOnlyList<PlanRow>> WczytajAsync(
            Context kontekst, int widgetId)
        {
            await AppServices.ReadyAsync();

            var uslugi = AppServices.Provider;
            var day = uslugi.GetRequiredService<IClock>().Today
                .AddDays(TodayWidget.Przesuniecie(kontekst, widgetId));

            return await uslugi.GetRequiredService<DayPlanService>().ForDayAsync(day);
        }

        /// <summary>
        /// Zapis barwy na liczbę.
        /// </summary>
        /// <remarks>
        /// Zapis jest tekstem wpisanym przez człowieka, więc może być czymkolwiek.
        /// Wywrotka przy rysowaniu wiersza nie daje żadnego objawu poza zepsutą listą
        /// na ekranie domowym, więc zły zapis schodzi na barwę domyślną.
        /// </remarks>
        private static int Color(string? zapis)
        {
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
                return Akcent;
            }
        }

        /// <summary>Barwa domyślna paska — ta sama, co akcent aplikacji.</summary>
        private const int Akcent = unchecked((int)0xFF7C6CF5);
    }
}

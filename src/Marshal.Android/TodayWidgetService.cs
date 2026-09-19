using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Views;
using Marshal.Application.Abstractions;
using Android.Widget;
using Marshal.Application.UseCases;
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
        private IReadOnlyList<PozycjaPlanu> _wiersze = [];

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
                _ = PoPrzygotowaniuAsync(kontekst);
                return;
            }

            try
            {
                _wiersze = WczytajAsync(kontekst, widgetId)
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .GetAwaiter()
                    .GetResult();
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

            widok.SetTextViewText(Resource.Id.tytul, pozycja.Tytul);
            widok.SetTextViewText(Resource.Id.podpis, pozycja.Podpis);
            widok.SetInt(Resource.Id.pasek, "setBackgroundColor", Barwa(pozycja.Barwa));

            // Uzupełnienie wzorca, nie własny zamiar: wierszowi listy nie da się dać
            // osobnego zamiaru oczekującego — system trzyma jeden wzorzec na całą listę
            // i dokłada do niego to, co wiersz tu wpisze.
            //
            // Wydarzenie z cudzego kalendarza nie ma czego odhaczyć jednym dotknięciem:
            // ptaszek idzie tam przez sieć, a widget nie ma jak poczekać ani pokazać,
            // że czeka. Kwadracik zostaje wtedy schowany — niewidoczny, a nie wyłączony,
            // bo wyłączony wyglądałby na zepsuty. Miejsce po nim zostaje, żeby wiersze
            // miały wspólną krawędź tekstu.
            if (pozycja.Zadanie is { } zadanie)
            {
                widok.SetViewVisibility(Resource.Id.zrobione, ViewStates.Visible);

                var odhaczenie = new Intent();
                odhaczenie.PutExtra(TodayWidget.TaskIdExtra, zadanie.ToString());

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

            if (pozycja.Zadanie is { } otwierane)
            {
                otwarcie.PutExtra(TodayWidget.TaskIdExtra, otwierane.ToString());
            }

            widok.SetOnClickFillInIntent(Resource.Id.tresc, otwarcie);

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
        private static async Task<IReadOnlyList<PozycjaPlanu>> WczytajAsync(
            Context kontekst, int widgetId)
        {
            await AppServices.ReadyAsync();

            var uslugi = AppServices.Provider;
            var dzien = uslugi.GetRequiredService<IClock>().Today
                .AddDays(TodayWidget.Przesuniecie(kontekst, widgetId));

            return await uslugi.GetRequiredService<PlanDniaService>().DlaDniaAsync(dzien);
        }

        /// <summary>
        /// Zapis barwy na liczbę.
        /// </summary>
        /// <remarks>
        /// Zapis jest tekstem wpisanym przez człowieka, więc może być czymkolwiek.
        /// Wywrotka przy rysowaniu wiersza nie daje żadnego objawu poza zepsutą listą
        /// na ekranie domowym, więc zły zapis schodzi na barwę domyślną.
        /// </remarks>
        private static int Barwa(string? zapis)
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

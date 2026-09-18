using Android.App;
using Android.Appwidget;
using Android.Content;
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

        public void OnDataSetChanged()
        {
            try
            {
                _wiersze = WczytajAsync(kontekst, widgetId).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                // Pusta lista zamiast wywrotki: usługa, która rzuci, zostawia na
                // ekranie domowym komunikat systemu o zepsutym widgecie.
                _wiersze = [];
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
            var odhaczenie = new Intent();
            odhaczenie.PutExtra(TodayWidget.TaskIdExtra, pozycja.Id.ToString());

            widok.SetOnClickFillInIntent(Resource.Id.zrobione, odhaczenie);

            // Dotknięcie wiersza poza kwadracikiem otwiera aplikację. Bez tego klik
            // w treść wiersza nie robi nic, a lista zajmuje prawie cały kafelek —
            // czyli „dotknięcie widgetu" trafiałoby w martwe pole.
            var otwarcie = new Intent();
            otwarcie.PutExtra(TodayWidget.CoOtworzExtra, TodayWidget.CoOtworz);

            widok.SetOnClickFillInIntent(Resource.Id.wiersz, otwarcie);

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

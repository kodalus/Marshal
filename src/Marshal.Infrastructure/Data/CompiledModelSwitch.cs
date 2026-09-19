using System.Runtime.CompilerServices;

namespace Marshal.Infrastructure.Data;

/// <summary>
/// Przełącznik, bez którego skompilowany model wiesza start na telefonie.
/// </summary>
/// <remarks>
/// <para>
/// Wygenerowany model buduje się w swoim konstruktorze statycznym, a ten domyślnie
/// robi to na <b>osobnym wątku ze stosem dziesięciu megabajtów</b> i czeka na jego
/// koniec. EF robi tak dlatego, że bardzo duże modele przepełniały stos zwykłego
/// wątku. Na telefonie kosztowało to całą aplikację: wątek nie wstawał, konstruktor
/// statyczny nie wracał, a konstruktor statyczny, który nie wraca, zatrzymuje bez
/// wyjątku każdego, kto dotknie tej klasy. Objawem był ekran „Chwileczkę — otwieram
/// bazę" i cisza: ani błędu, ani wpisu w dzienniku.
/// </para>
/// <para>
/// Ten przełącznik każe budować model na wątku wołającego. Nasz model ma dziewiętnaście
/// encji — o rzędy wielkości mniej niż te, dla których tamto obejście powstało.
/// </para>
/// <para>
/// Inicjalizator modułu, a nie wywołanie ze startu aplikacji: środowisko gwarantuje,
/// że wykona się przed pierwszym dotknięciem czegokolwiek z tego zestawu. Bazę potrafi
/// dotknąć pierwszy widget albo odbiornik budzika, czyli droga bez okna — a przełącznik
/// odczytywany jest raz, przy pierwszym sięgnięciu po model, i później nie ma już czego
/// przestawiać.
/// </para>
/// </remarks>
internal static class CompiledModelSwitch
{
    [ModuleInitializer]
    internal static void Set() =>
        AppContext.SetSwitch("Microsoft.EntityFrameworkCore.Issue31751", true);
}

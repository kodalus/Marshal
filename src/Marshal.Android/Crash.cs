using Android.Content;
using Android.Runtime;
using Android.Widget;

// Nazwa własna: sama „App" w tej przestrzeni mogłaby oznaczać przestrzeń Android.App,
// a „Application" — przestrzeń Marshal.Application. Chodzi o naszą aplikację Avalonii.
using AvaloniaApp = Marshal.UI.App;

namespace Marshal.Android;

/// <summary>
/// Ślad po awarii, która zabiła proces.
/// </summary>
/// <remarks>
/// <para>
/// Na pulpicie nieudany start kończy się ekranem z wyjątkiem — wystarczy spojrzeć.
/// Na telefonie proces po prostu znika, a system pokazuje własne okienko „aplikacja
/// przestaje działać", w którym treść wyjątku jest schowana pod odnośnikiem i nie da
/// się jej stamtąd wyjąć. Bez kabla i bez narzędzi deweloperskich nie zostaje nic.
/// </para>
/// <para>
/// Stąd ta klasa: wyjątek idzie do pliku w katalogu aplikacji, a przy następnym
/// uruchomieniu trafia do dziennika „Co się działo" jako wpis „Start" z poziomem
/// problemu. Awaria opowiada więc o sobie sama, przy pierwszym udanym starcie po niej.
/// </para>
/// <para>
/// Wyjątek nie jest przechwytywany — <c>Handled</c> zostaje nieruszone. Aplikacja
/// utrzymana przy życiu po nieobsłużonym błędzie jest w stanie, o którym nic nie
/// wiadomo, i kolejne objawy prowadzą już donikąd. Lepiej, żeby padła i powiedziała.
/// </para>
/// </remarks>
internal static class Crash
{
    private const string FileName = "ostatnia-awaria.txt";

    /// <summary>Podpięcie pod nieobsłużone wyjątki. Wołane przed stawianiem Avalonii.</summary>
    public static void Watch(Context context)
    {
        // Dwa źródła, bo to dwie różne drogi: jedna z wątków Javy, druga z zarządzanych.
        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => Save(context, e.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Save(context, e.ExceptionObject as Exception);
    }

    /// <summary>Odczyt i skasowanie śladu po poprzednim uruchomieniu.</summary>
    /// <remarks>
    /// Kasowanie od razu, a nie po zapisaniu do dziennika: gdyby ten sam błąd wracał
    /// przy każdym starcie, plik odtworzy się sam. Zostawiony opowiadałby w kółko
    /// o awarii sprzed tygodnia.
    /// </remarks>
    public static void Read(Context context)
    {
        try
        {
            if (PathOf(context) is not { } path || !File.Exists(path))
            {
                return;
            }

            var trace = File.ReadAllText(path);
            AvaloniaApp.PlatformTrace = $"Poprzednie uruchomienie padło.\n\n{trace}";
            File.Delete(path);

            // Dymek systemowy, a nie tylko wpis w dzienniku. Dziennik wymaga udanego
            // startu i bazy — a przy awarii, która wraca za każdym razem, żadnego
            // udanego startu nie będzie i ślad nie miałby jak dojść do oczu.
            Toast.MakeText(context, First(trace), ToastLength.Long)?.Show();
        }
        catch (Exception e)
        {
            AvaloniaApp.PlatformTrace = $"Nie udało się odczytać śladu awarii: {e.Message}";
        }
    }

    /// <summary>Rodzaj i treść błędu — pierwsze dwa wiersze śladu, bo dymek nie zmieści więcej.</summary>
    private static string First(string trace) =>
        string.Join("\n", trace.Split('\n').Skip(1).Take(2)).Trim();

    private static void Save(Context context, Exception? error)
    {
        try
        {
            if (PathOf(context) is { } path)
            {
                File.WriteAllText(
                    path,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\n{error?.ToString() ?? "bez wyjątku"}");
            }
        }
        catch
        {
            // Nic. Obsługa awarii, która sama się wywraca, zabiera ze sobą jedyny ślad
            // po tej pierwszej — a i tak nie ma dokąd tego zgłosić.
        }
    }

    private static string? PathOf(Context context) =>
        context.FilesDir is { AbsolutePath: { } folder } ? Path.Combine(folder, FileName) : null;
}

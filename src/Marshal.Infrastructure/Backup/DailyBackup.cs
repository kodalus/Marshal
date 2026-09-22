using Marshal.Application.Abstractions;
using Marshal.Domain.Diagnostics;

namespace Marshal.Infrastructure.Backup;

/// <summary>
/// Codzienna kopia zapasowa, robiona sama.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bez zadania w tle.</b> Ten sam wzorzec, co przejście dnia: nie ma nic, co musi
/// zadziałać o północy, jest tylko różnica między dniem ostatniej kopii a dzisiejszym,
/// zastawana przy otwarciu aplikacji. Zadanie w tle trzeba by utrzymać przy życiu na
/// dwóch platformach, z których jedna zabija je, kiedy zechce — a wtedy kopia
/// przestawałaby powstawać bez żadnego objawu.
/// </para>
/// <para>
/// <b>Dzień ostatniej kopii czytany z plików, nie z ustawień.</b> Druga prawda o tej
/// samej rzeczy rozjeżdża się przy pierwszej niezgodności: zapisane „ostatnia kopia
/// wczoraj" przy pustym folderze znaczyłoby spokój, którego nie ma. Nazwa pliku niesie
/// dzień, więc folder odpowiada na to pytanie sam.
/// </para>
/// <para>
/// <b>Najpierw plik tymczasowy, potem przeniesienie.</b> Zapis przerwany w połowie —
/// zamknięciem laptopa, brakiem miejsca — zostawiłby plik, który wygląda na kopię
/// i nią nie jest. Urwana kopia jest gorsza od jej braku, bo brak widać.
/// </para>
/// </remarks>
public sealed class DailyBackup(
    BackupService backup, ISettings settings, IClock clock, IActivityLog journal)
{
    /// <summary>Ile kopii zostaje. Najstarsze ponad tę liczbę są kasowane.</summary>
    /// <remarks>
    /// Jedna reguła zamiast dwóch poziomów („tydzień co dzień, potem co tydzień"):
    /// poziomy trzeba umieć wytłumaczyć, a przy pliku wielkości notatki nie kupują nic.
    /// Trzydzieści otwarć aplikacji wstecz to przy codziennym używaniu miesiąc.
    /// </remarks>
    public const int Keep = 30;

    private const string Prefix = "marshal-";

    private const string Suffix = ".json";

    /// <summary>
    /// Dzień, w którym próbowaliśmy ostatnio — **w tym uruchomieniu**, nie w bazie.
    /// </summary>
    /// <remarks>
    /// Nadrabianie wołane jest także przy każdym powrocie z tła. Folder nieosiągalny
    /// (odłączony dysk, ścieżka po przeniesionym katalogu) dawałby wtedy odmowę
    /// w dzienniku za każdym powrotem. Pamiętane w pamięci, nie zapisywane: po
    /// ponownym uruchomieniu aplikacja ma spróbować jeszcze raz, bo dysk mógł wrócić.
    /// </remarks>
    private DateOnly _tried;

    /// <summary>Folder, w którym leżą kopie — wskazany albo domyślny dla platformy.</summary>
    public string Folder =>
        settings.BackupFolder is { Length: > 0 } chosen ? chosen : DefaultFolder();

    /// <summary>
    /// Domyślny folder kopii: „Dokumenty/Marshal/kopie".
    /// </summary>
    /// <remarks>
    /// Jedno wyrażenie na obie platformy. Na Windowsie wskazuje folder Dokumentów —
    /// czyli tam, gdzie sięga kopia zapasowa systemu albo OneDrive, i gdzie da się do
    /// tych plików dojść bez szukania. Na Androidzie ten sam symbol wskazuje prywatny
    /// katalog aplikacji, który jest tam jedynym miejscem zapisywalnym bez proszenia
    /// o zgodę — a zgoda na cały magazyn byłaby ceną nieproporcjonalną do jednego pliku
    /// dziennie.
    /// </remarks>
    public static string DefaultFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Marshal",
        "kopie");

    /// <summary>Dzień najnowszej kopii w folderze albo <c>null</c>, gdy nie ma żadnej.</summary>
    public DateOnly? Last()
    {
        try
        {
            return Existing(Folder).Select(f => f.Day).Cast<DateOnly?>().FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Folder nieosiągalny znaczy „nie wiem", a nie „nie ma kopii". Wiersz
            // w ustawieniach ma o tym milczeć, a nie twierdzić, że kopii nie było.
            return null;
        }
    }

    /// <summary>
    /// Kopia na dziś, jeśli jeszcze jej nie ma. Wołanie jest powtarzalne bez skutków.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        if (!settings.DailyBackup)
        {
            return;
        }

        var today = clock.Today;

        if (_tried == today)
        {
            return;
        }

        var folder = Folder;

        try
        {
            Directory.CreateDirectory(folder);

            var ready = Existing(folder);

            if (ready.Count > 0 && ready[0].Day >= today)
            {
                // Dzisiejsza kopia już jest. Znacznik dopiero tutaj, nie wyżej: dopóki
                // nie wiemy, co jest w folderze, nie ma czego uznawać za zrobione.
                _tried = today;
                return;
            }

            var name = $"{Prefix}{today:yyyy-MM-dd}{Suffix}";
            var target = Path.Combine(folder, name);
            var half = target + ".czesciowy";

            await using (var stream = File.Create(half))
            {
                await backup.ExportAsync(stream, ct);
            }

            File.Move(half, target, overwrite: true);
            _tried = today;

            Sweep(folder);

            await journal.RecordAsync("Kopia: codzienna", name);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Łapane szeroko i **celowo**: kopia zapasowa nie ma prawa przewrócić
            // aplikacji, a wołana jest bez oczekiwania na wynik, więc wyjątek stąd
            // nie miałby dokąd trafić. Treść wyjątku, nie „coś poszło nie tak":
            // przy kopii cicha porażka jest gorsza niż jej brak, bo zostawia
            // przekonanie, że kopia jest.
            _tried = today;

            await journal.RecordAsync(
                "Kopia: codzienna", $"nie udało się — {folder}",
                ActivityLevel.Problem, e.Message);
        }
    }

    /// <summary>Kopie w folderze, od najnowszej. Pliki o obcych nazwach pomijane.</summary>
    private static List<(DateOnly Day, string Path)> Existing(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(folder, $"{Prefix}*{Suffix}")
            .Select(path => (Day: DayOf(path), Path: path))
            .Where(f => f.Day is not null)
            .Select(f => (Day: f.Day!.Value, f.Path))
            .OrderByDescending(f => f.Day)];
    }

    private static DateOnly? DayOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);

        return name.Length == Prefix.Length + 10
            && DateOnly.TryParse(name[Prefix.Length..], out var day)
            ? day
            : null;
    }

    /// <summary>
    /// Kasowanie najstarszych ponad <see cref="Keep"/>.
    /// </summary>
    /// <remarks>
    /// Po udanym zapisie, nie przed: sprzątanie przed zapisem zwalniałoby miejsce pod
    /// kopię, która może nie powstać — i zostawiałoby o jedną mniej niż obiecano.
    /// Nieudane skasowanie jednego pliku nie psuje niczego, bo kopia już leży.
    /// </remarks>
    private static void Sweep(string folder)
    {
        foreach (var (_, path) in Existing(folder).Skip(Keep))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Plik otwarty w podglądzie albo tylko do odczytu. Zostaje do następnego
                // razu; zatrzymywanie sprzątania na pierwszym oporze zostawiłoby
                // wszystkie starsze.
            }
        }
    }
}

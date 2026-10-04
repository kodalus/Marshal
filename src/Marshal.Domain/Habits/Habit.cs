using Marshal.Domain.Primitives;

namespace Marshal.Domain.Habits;

/// <summary>
/// Nawyk — rzecz, której się nie odhacza, tylko się ją ciągnie.
/// </summary>
/// <remarks>
/// <para>
/// <b>Osobno od zadań i osobno od rytmów</b>, choć na pierwszy rzut oka to samo.
/// Zadanie z rytmem odpowiada na pytanie „czy dzisiejsze jest zrobione" i znika z listy,
/// kiedy jest — cała jego wartość mieści się w jednym dniu. Nawyk odpowiada na pytanie
/// „od ilu dni to ciągnę" i cała jego wartość mieści się w <b>historii</b>: w siatce,
/// na którą się patrzy i której nie chce się przerwać. To są dwie różne rzeczy, które
/// tylko przypadkiem powtarzają się z tą samą częstotliwością.
/// </para>
/// <para>
/// Gdyby zrobić z nawyku zadanie, każde odhaczenie wchodziłoby do skrzynki, do liczników
/// zaległości i do przeglądu — a pięć nawyków dziennie to pięć pozycji, których nikt
/// nie chce widzieć na liście rzeczy do zrobienia. Marshal pilnuje tego wprost
/// zasadą 1.2: lista ma mówić, co jest do zrobienia, a nie czym się jest.
/// </para>
/// <para>
/// Nawyk jest <b>dzienny</b> i nie ma innego rytmu. „Co drugi dzień" zabiera siatce
/// jej jedyną zaletę — to, że od razu widać dziurę — bo wtedy dziura bywa zgodna
/// z planem i trzeba ją odczytywać, zamiast zobaczyć.
/// </para>
/// </remarks>
public sealed class Habit : Entity
{
    private Habit()
    {
        Title = string.Empty;
    }

    private Habit(Guid id, DateTimeOffset createdAt, Hlc updatedAt, string title, int sortOrder)
        : base(id, createdAt, updatedAt)
    {
        Title = Normalize(title);
        SortOrder = sortOrder;
    }

    public static Habit Create(string title, int sortOrder, DateTimeOffset now, Hlc stamp) =>
        new(Guid.CreateVersion7(), now, stamp, title, sortOrder);

    public string Title { get; private set; }

    /// <summary>Barwa kafelka. Pusta znaczy „barwa domyślna".</summary>
    /// <remarks>
    /// Nawyki rozpoznaje się wzrokiem, nie czytaniem — siatka jest tu treścią, a nazwa
    /// podpisem. Przy sześciu kafelkach barwa odpowiada na „który to" szybciej niż napis.
    /// </remarks>
    public string? Color { get; private set; }

    /// <summary>
    /// Ile trzeba zrobić w ciągu dnia, żeby dzień się liczył. Puste znaczy „raz".
    /// </summary>
    /// <remarks>
    /// Dwa rodzaje nawyków jednym polem. Puste to nawyk na ptaszek — „medytacja",
    /// „ćwiczenia": było albo nie było. Wpisana liczba to nawyk na ilość — „dwadzieścia
    /// stron", „osiem szklanek": dzień liczy się od progu w górę, a poniżej jest
    /// zaczęty, nie zrobiony.
    /// </remarks>
    public int? Target { get; private set; }

    /// <summary>Czego dotyczy liczba — „stron", „szklanek". Puste przy nawyku na ptaszek.</summary>
    public string? Unit { get; private set; }

    public int SortOrder { get; private set; }

    /// <summary>
    /// Odłożony: nie liczy się już seria i nie stoi na liście, ale historia zostaje.
    /// </summary>
    /// <remarks>
    /// Osobno od nagrobka, bo to jest coś innego. Nagrobek znaczy „tego nie było";
    /// odłożenie znaczy „to się działo i skończyło". Kasowanie nawyku, którego się
    /// przestało ciągnąć, kasowałoby też dowód, że się go ciągnęło — a ten dowód jest
    /// w nawyku jedyną rzeczą, która ma wartość po fakcie.
    /// </remarks>
    public bool Archived { get; private set; }

    public void Rename(string title, Hlc stamp)
    {
        Title = Normalize(title);
        Touch(stamp);
    }

    public void SetColor(string? color, Hlc stamp)
    {
        Color = string.IsNullOrWhiteSpace(color) ? null : color.Trim();
        Touch(stamp);
    }

    /// <summary>Próg dnia razem z jednostką — jedno bez drugiego nic nie znaczy.</summary>
    public void SetTarget(int? target, string? unit, Hlc stamp)
    {
        Target = target is > 1 ? target : null;
        Unit = Target is null || string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();
        Touch(stamp);
    }

    public void MoveTo(int sortOrder, Hlc stamp)
    {
        SortOrder = sortOrder;
        Touch(stamp);
    }

    public void Archive(Hlc stamp)
    {
        Archived = true;
        Touch(stamp);
    }

    public void Revive(Hlc stamp)
    {
        Archived = false;
        Touch(stamp);
    }

    private static string Normalize(string title)
    {
        var patch = title?.Trim() ?? string.Empty;

        return patch.Length == 0
            ? throw new ArgumentException("Nawyk potrzebuje nazwy.", nameof(title))
            : patch;
    }
}

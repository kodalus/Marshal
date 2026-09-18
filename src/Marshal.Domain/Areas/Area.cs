using Marshal.Domain.Primitives;

namespace Marshal.Domain.Areas;

/// <summary>
/// Obszar odpowiedzialności — drugi horyzont GTD (spec 5.2). Coś, co się nigdy
/// nie kończy i co się utrzymuje na jakimś poziomie.
/// </summary>
/// <remarks>
/// Obszar to nie tag. Tag jest opcjonalny i wiele-do-wielu; obszar jest wymagany
/// i jednokrotny, bo inaczej nie jest podziałem — projekt w trzech obszarach albo
/// w żadnym psuje każdą liczbę w tabeli równowagi (spec 8.5).
/// </remarks>
public sealed class Area : Entity
{
    public const int DefaultQuietDays = 60;
    public const int DefaultNudgeDaysValue = 7;

    private Area()
    {
        Name = string.Empty;
    }

    public Area(
        Guid id,
        DateTimeOffset createdAt,
        Hlc updatedAt,
        string name,
        double sortOrder,
        int quietDays = DefaultQuietDays,
        int defaultNudgeDays = DefaultNudgeDaysValue,
        string? color = null)
        : base(id, createdAt, updatedAt)
    {
        Name = Normalize(name);
        SortOrder = sortOrder;
        QuietDays = Positive(quietDays, nameof(quietDays));
        DefaultNudgeDays = Positive(defaultNudgeDays, nameof(defaultNudgeDays));
        Color = color;
        IsActive = true;
    }

    public string Name { get; private set; }

    /// <summary>Dziedziczony przez projekty bez własnego koloru.</summary>
    public string? Color { get; private set; }

    public double SortOrder { get; private set; }

    /// <summary>Nieaktywny znika z podziału i z analizy równowagi.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Próg ciszy dla niezmiennika N10 — po ilu dniach bez ruchu zgłosić obszar.</summary>
    public int QuietDays { get; private set; }

    /// <summary>
    /// Domyślny próg ponaglenia „Oczekiwanych" dla zadań tego obszaru. Per obszar,
    /// nie globalnie: siedem dni dla urzędu jest absurdalnie agresywne, dla osoby
    /// w domu za wolne.
    /// </summary>
    public int DefaultNudgeDays { get; private set; }

    /// <summary>
    /// Kalendarz Google, który jest tym obszarem. Pusty znaczy „żaden".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Odwraca pytanie, z którym nie było co zrobić. Wydarzenie z cudzego kalendarza
    /// nie ma gdzie trzymać obszaru — Google nie ma na to pola, a zakładanie po naszej
    /// stronie wiersza na każde wydarzenie tylko po to, żeby było gdzie zapisać jedno
    /// słowo, jest przebudową bazy w celu, który da się osiągnąć bez niej. Skoro jednak
    /// kalendarz <b>jest</b> obszarem — „Dzieci" to kalendarz rodzinny, „Praca" to
    /// firmowy — to obszaru nie trzeba nigdzie zapisywać: wynika z tego, w którym
    /// kalendarzu wydarzenie stoi.
    /// </para>
    /// <para>
    /// Powiązanie siedzi na obszarze, nie na podłączeniu, i to jest rozstrzygnięcie.
    /// Wiersz podłączenia jest odbiciem tego, co jest u Google: bywa zakładany
    /// dwukrotnie przy pierwszej synchronizacji między urządzeniami i bywa odrzucany
    /// przy składaniu duplikatów. Stan położony na czymś, co ginie, ginie razem z tym.
    /// Obszar jest nasz, synchronizuje się dziennikiem i nie znika.
    /// </para>
    /// <para>
    /// Jeden obszar na jeden kalendarz. Dwa obszary wskazujące na ten sam kalendarz
    /// znaczyłyby, że wydarzenie należy do obu — a obszar, który nie dzieli, nie jest
    /// obszarem (ta sama przyczyna, dla której zadanie ma dokładnie jeden).
    /// </para>
    /// </remarks>
    public Guid? CalendarId { get; private set; }

    /// <summary>Przypisanie kalendarza do obszaru albo zdjęcie przypisania.</summary>
    public void SetCalendar(Guid? calendarId, Hlc now)
    {
        CalendarId = calendarId;
        Touch(now);
    }

    public void Rename(string name, Hlc now)
    {
        Name = Normalize(name);
        Touch(now);
    }

    public void SetThresholds(int quietDays, int defaultNudgeDays, Hlc now)
    {
        QuietDays = Positive(quietDays, nameof(quietDays));
        DefaultNudgeDays = Positive(defaultNudgeDays, nameof(defaultNudgeDays));
        Touch(now);
    }

    /// <summary>
    /// Barwa obszaru — dziedziczona przez projekty i zadania, które nie mają własnej.
    /// </summary>
    /// <remarks>
    /// Kolor jest tu po to, żeby na siatce dało się jednym spojrzeniem odróżnić pracę
    /// od dzieci, a nie żeby ozdobić listę. Dlatego dziedziczy w dół: obszar nadaje
    /// ton, projekt może go doprecyzować, zadanie może się wyłamać.
    /// </remarks>
    public void SetColor(string? color, Hlc now)
    {
        Color = color;
        Touch(now);
    }

    public void SetActive(bool isActive, Hlc now)
    {
        IsActive = isActive;
        Touch(now);
    }

    private static string Normalize(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Trim();
    }

    private static int Positive(int value, string paramName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, paramName);
        return value;
    }
}

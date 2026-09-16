namespace Marshal.Domain.Tasks;

/// <summary>
/// Stan zadania w metodzie GTD (spec 5.3). Nazwa <c>TaskStatus</c> jest zajęta przez
/// <see cref="System.Threading.Tasks.TaskStatus"/>, stąd <c>TaskState</c>.
/// </summary>
public enum TaskState
{
    /// <summary>Skrzynka. Wrzucone, jeszcze nieprzetworzone — ma tylko tytuł.</summary>
    Inbox = 0,

    /// <summary>Następna akcja. Konkretna, wykonalna, bez wyznaczonego dnia.</summary>
    Next = 1,

    /// <summary>Czekam na kogoś. Wymaga „na kogo" i „od kiedy".</summary>
    Waiting = 2,

    /// <summary>Zaplanowane na konkretny dzień. Wymaga <c>DoDate</c>.</summary>
    Scheduled = 3,

    /// <summary>Kiedyś-może. Poza polem widzenia do czasu <c>DeferUntil</c>.</summary>
    Someday = 4,

    Done = 5,

    Trashed = 6,
}

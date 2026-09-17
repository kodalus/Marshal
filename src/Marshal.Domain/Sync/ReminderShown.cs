namespace Marshal.Domain.Sync;

/// <summary>
/// Że **to urządzenie** pokazało już to jedno przypomnienie.
/// </summary>
/// <remarks>
/// <para>
/// Lokalne, niesynchronizowane — i to jest rozstrzygnięcie, nie oszczędność. Chwila
/// przypomnienia jest decyzją i się synchronizuje; „czy już pokazałam" jest faktem
/// o tym urządzeniu i zostaje przy nim.
/// </para>
/// <para>
/// Cena: przypomnienie potrafi odezwać się i na telefonie, i na komputerze. Cena
/// odwrotnego rozwiązania: telefon odgrywa je w torbie, zapisuje „pokazane", i nie
/// dowiadujesz się nigdy. Dwa razy usłyszeć jest gorzej niż raz, ale nieporównanie
/// lepiej niż nie usłyszeć wcale.
/// </para>
/// </remarks>
public sealed class ReminderShown
{
    private ReminderShown()
    {
    }

    public ReminderShown(Guid taskId, DateTimeOffset reminderAt, DateTimeOffset shownAt)
    {
        TaskId = taskId;
        ReminderAt = reminderAt;
        ShownAt = shownAt;
    }

    public Guid TaskId { get; private set; }

    /// <summary>
    /// Na którą chwilę było ustawione.
    /// </summary>
    /// <remarks>
    /// Razem z zadaniem tworzy klucz: jeden wiersz na **chwilę**, nie na zadanie.
    /// Wiersz na zadanie wystarczał, dopóki przypomnienie było jedno; przy kilku
    /// wyprzedzeniach pamiętał wyłącznie ostatnie i wszystkie wcześniejsze robiły się
    /// znowu niepokazane — zadanie z trzema przypomnieniami odzywało się w kółko.
    ///
    /// Przy okazji rozwiązuje to przesunięcie przypomnienia: nowa chwila to nowy wiersz,
    /// więc „przypomnij mi jednak o godzinę później" odzywa się, choć o tym zadaniu
    /// już raz było.
    /// </remarks>
    public DateTimeOffset ReminderAt { get; private set; }

    public DateTimeOffset ShownAt { get; private set; }
}

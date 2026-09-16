using System.Text.Json.Serialization;
using Marshal.Domain.Sync;

namespace Marshal.Infrastructure.Backup;

/// <summary>
/// Zawartość pliku kopii zapasowej (spec 12).
/// </summary>
/// <remarks>
/// <para>
/// Treść to **te same wiersze zmian, którymi mówi synchronizacja** (spec 9.4), tylko
/// zebrane w jeden plik zamiast dopisywane do porcji. To nie jest oszczędność kodu,
/// tylko jedyny sposób, żeby wgrywanie kopii scalało się dokładnie tak, jak scala się
/// drugie urządzenie: gdyby kopia miała własny format i własne scalanie, byłyby dwie
/// implementacje reguły „nowsze pole wygrywa", a rozjechałyby się przy pierwszej
/// zmianie modelu — po cichu i tylko u tego, kto akurat odtwarzał kopię.
/// </para>
/// <para>
/// Format jest jawny i czytelny bez aplikacji: to zwykły JSON z nazwami tabel i pól.
/// Kopia, której nie da się obejrzeć notatnikiem, jest obietnicą, nie zabezpieczeniem.
/// </para>
/// </remarks>
public sealed class BackupFile
{
    /// <summary>Wersja formatu. Nowszą odczytujemy najlepiej jak umiemy, starszej nie porzucamy.</summary>
    [JsonPropertyName("wersja")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("utworzono")]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Urządzenie, które zrobiło kopię — do rozpoznania pliku, nie do scalania.</summary>
    [JsonPropertyName("urzadzenie")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("wiersze")]
    public List<ChangeLine> Lines { get; set; } = [];
}

/// <summary>Co zrobić z tym, co już jest w bazie (spec 12).</summary>
public enum ImportMode
{
    /// <summary>
    /// Scalenie po zegarze logicznym: pole po polu wygrywa nowszy znacznik.
    /// </summary>
    /// <remarks>
    /// Właściwy tryb wszędzie tam, gdzie urządzenie żyje dalej — bo to dokładnie ta
    /// sama reguła, którą stosuje synchronizacja, więc kopia nie może wprowadzić stanu,
    /// którego drugie urządzenie by nie zaakceptowało.
    /// </remarks>
    Merge,

    /// <summary>
    /// Podmiana całości: baza czyszczona, plik wgrywany od zera.
    /// </summary>
    /// <remarks>
    /// <b>Tylko na urządzeniu, które zaczyna od nowa</b> — po awarii, po przesiadce na
    /// nowy sprzęt. Na urządzeniu podpiętym do synchronizacji podmiana jest pozorna:
    /// czyszczenie nie zostawia śladu w dzienniku (bo dziennik niesie zmiany, a nie
    /// usunięcia tabel), więc drugie urządzenie o niczym się nie dowie i przy
    /// najbliższym scaleniu odda swój stan z powrotem. To nie jest usterka do
    /// naprawienia — to wynika z tego, czym jest synchronizacja plikowa (spec 9.8).
    /// </remarks>
    Replace,
}

/// <summary>Ile wierszy plik niósł i ile z nich faktycznie coś zmieniło.</summary>
public sealed record ImportReport(int Read, int Applied, int Skipped);

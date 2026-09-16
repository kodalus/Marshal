using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Marshal.Domain.Sync;

/// <summary>
/// Jeden wiersz pliku zmian (spec 9.3): wszystkie pola jednej encji zmienione
/// jednym zapisem, pod wspólnym znacznikiem zegara.
/// </summary>
/// <remarks>
/// Grupowanie po zapisie, a nie po polu, bo jeden zapis to jedna decyzja
/// użytkownika i ma jeden znacznik. Rozbicie na wiersz per pole powtarzałoby
/// identyfikator i znacznik przy każdym z nich.
/// </remarks>
public sealed class ChangeLine
{
    /// <summary>Nazwa tabeli.</summary>
    [JsonPropertyName("e")]
    public string Entity { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("hlc")]
    public string Hlc { get; set; } = string.Empty;

    /// <summary>Pola i ich wartości w postaci bazodanowej. Brak klucza znaczy „nie zmieniono".</summary>
    [JsonPropertyName("f")]
    public Dictionary<string, JsonNode?> Fields { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        // Jeden wiersz to jedna linia pliku. Wcięcia rozbiłyby format.
        WriteIndented = false,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    /// <summary>Zwraca null dla wiersza, którego nie da się odczytać.</summary>
    /// <remarks>
    /// Uszkodzony wiersz jest pomijany, a nie przerywa synchronizacji. Plik może
    /// zostać ucięty w pół linii przez przerwane pobieranie albo pochodzić z nowszej
    /// wersji aplikacji. Przerwanie całości oznaczałoby, że jedna zła linia blokuje
    /// wszystkie zmiany, które przyszły po niej.
    /// </remarks>
    public static ChangeLine? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ChangeLine>(line, Options);

            return parsed is null
                || string.IsNullOrEmpty(parsed.Entity)
                || string.IsNullOrEmpty(parsed.Id)
                || string.IsNullOrEmpty(parsed.Hlc)
                ? null
                : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

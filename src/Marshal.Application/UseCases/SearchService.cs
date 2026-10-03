using Marshal.Application.Repositories;
using Marshal.Domain.Notes;
using Marshal.Domain.Tasks;

namespace Marshal.Application.UseCases;

/// <summary>Co znalazło szukanie — osobno zadania, osobno notatki.</summary>
/// <remarks>
/// Dwie listy, nie jedna wspólna. Zadanie i notatka otwierają się czym innym i znaczą
/// co innego: jedno jest do zrobienia, drugie do przeczytania. Zlane w jedną listę
/// kazałyby przy każdym wierszu rozstrzygać, na co się właśnie patrzy.
/// </remarks>
public sealed record SearchHits(IReadOnlyList<TaskItem> Tasks, IReadOnlyList<Note> Notes)
{
    public static readonly SearchHits None = new([], []);

    public int Count => Tasks.Count + Notes.Count;
}

/// <summary>
/// Szukanie po całej aplikacji: nazwy i treści zadań oraz notatek.
/// </summary>
/// <remarks>
/// <para>
/// Osobno od filtrów, choć jedno i drugie zawęża listę. Filtr jest <b>pytaniem
/// zapisanym</b> — „zadania obszaru Dom z terminem w tym tygodniu" — i buduje się go
/// z warunków. Szukanie jest pytaniem jednorazowym i zadaje się je słowem, które się
/// pamięta. Wciśnięcie szukania w filtry kazałoby budować warunek po to, żeby
/// przypomnieć sobie, gdzie coś zapisano.
/// </para>
/// <para>
/// Bez obsługi wielu słów i bez wagi trafień: szuka się tu rzeczy, które się napisało,
/// a nie dokumentów, których się nie zna. „Przychodnia" ma znaleźć to jedno zadanie,
/// a nie ustawić dwadzieścia w kolejności zgadniętej trafności.
/// </para>
/// </remarks>
public sealed class SearchService(ITaskRepository tasks, INoteRepository notes)
{
    public async Task<SearchHits> FindAsync(string? query, CancellationToken ct = default)
    {
        // Puste pole nie jest prośbą o całą bazę. Notatki bez tego warunku oddałyby
        // wszystko, bo ich szukanie bez tekstu znaczy „pokaż listę" — i słusznie,
        // tyle że tam pole szukania stoi nad listą, która i tak jest.
        if (string.IsNullOrWhiteSpace(query))
        {
            return SearchHits.None;
        }

        return new SearchHits(
            await tasks.SearchAsync(query, ct),
            await notes.SearchAsync(query, ct));
    }
}

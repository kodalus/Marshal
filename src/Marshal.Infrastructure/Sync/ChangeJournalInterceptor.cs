using System.Text.Encodings.Web;
using System.Text.Json;
using Marshal.Domain.Primitives;
using Marshal.Domain.Series;
using Marshal.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Marshal.Domain.Tasks;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Marshal.Infrastructure.Sync;

/// <summary>
/// Zapisuje każdą zmianę pola encji do dziennika, w tej samej transakcji co sama zmiana.
/// </summary>
/// <remarks>
/// Dziennik powstaje **przechwytywaniem zapisu**, a nie ręcznym wołaniem w każdym
/// miejscu, które coś zmienia. Ręczne wołanie działa dokładnie do pierwszego
/// przeoczenia, a przeoczenie w dzienniku nie daje żadnego objawu: zmiana zapisuje
/// się lokalnie, po prostu nigdy nie dociera na drugie urządzenie.
///
/// Wpis do dziennika idzie w tej samej transakcji co zmiana, więc nie da się mieć
/// zmiany bez wpisu ani wpisu bez zmiany.
/// </remarks>
public sealed class ChangeJournalInterceptor : SaveChangesInterceptor
{
    /// <summary>
    /// Bez uciekania znaków spoza ASCII. Domyślnie <c>JsonSerializer</c> zamieniłby
    /// „kupić mleko" na „kupi\u0107 mleko", a format tekstowy wybraliśmy właśnie po to,
    /// żeby dziennik dało się czytać przy diagnostyce. Nazwa kodera mówi o kontekście
    /// HTML, w którym te znaki bywają groźne — dziennik nigdzie nie trafia jako HTML.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            Journal(eventData.Context);
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is not null)
        {
            Journal(eventData.Context);
        }

        return base.SavingChanges(eventData, result);
    }

    /// <summary>
    /// Czy wiersz jest <b>wyliczalny z serii</b> — a więc nie musi jechać synchronizacją.
    /// </summary>
    /// <remarks>
    /// <para>
    /// To jest zdjęcie przyczyny, a nie kolejna łata na jej skutki. Zapisane okno serii
    /// stawia w bazie kilkadziesiąt wystąpień na każdy rytm, a dziennik zapisuje osobno
    /// każde pole i osobno jego znacznik — czyli około sześćdziesięciu wierszy na jedno
    /// wystąpienie. Przy kilkunastu seriach to dziesiątki tysięcy wierszy do wysłania
    /// i do nałożenia po drugiej stronie: stąd scalanie trwające minuty, zatkana brama,
    /// martwe przyciski i aplikacja, która po minucie przestaje odpowiadać.
    /// </para>
    /// <para>
    /// A wysyłać tego nie trzeba. Tożsamość wystąpienia liczy się z serii i dnia, więc
    /// <b>drugie urządzenie dojdzie do dokładnie tych samych wierszy samo</b> — po to
    /// była cała przebudowa. Synchronizacji wymaga seria i to, co od wyliczenia odbiega:
    /// dzień odhaczony, wyrzucony albo zmieniony z ręki.
    /// </para>
    /// <para>
    /// Pytanie zadane jest wąsko i po stanie, nie po intencji: wystąpienie serii, które
    /// jest zaplanowane, nietknięte i nieodhaczone. Każde odstępstwo wypada z tej
    /// definicji i jedzie dalej normalnie.
    /// </para>
    /// <para>
    /// <b>I tylko wiersz dopisany.</b> Pominięcie było dobre dla wiersza, który to
    /// urządzenie właśnie postawiło z okna — bo drugie postawi go sobie samo. Dla wiersza
    /// <b>zmienianego</b> nie jest dobre nigdy: zmiany nie policzy nikt, więc pominięta
    /// w dzienniku nie dochodzi nigdzie, a u siebie wygląda, jakby się udała.
    /// Tak znikało skrócenie jednego dnia pracy zrobione na siatce: u siebie krótsze,
    /// na telefonie pełnowymiarowe — a że skrócenie poszło przy tym do kalendarza
    /// Google, telefon rysował obok swojego wystąpienia cudze wydarzenie, bo wskazania
    /// na nie też nie dostał. Jedno zdarzenie widoczne dwa razy, raz nie do otwarcia
    /// jak własne.
    /// </para>
    /// <para>
    /// Wyjątkiem jest przepisanie wystąpień z szablonu po zmianie serii: tę zmianę niesie
    /// sama seria i drugie urządzenie przepisze z niej swoje dni samo — zob.
    /// <see cref="SeriesScope"/>.
    /// </para>
    /// </remarks>
    private static bool Derived(EntityEntry<Entity> entry) =>
        entry.Entity is TaskItem
        {
            SeriesId: not null,
            Overridden: false,
            State: TaskState.Scheduled,
            CompletedAt: null,
            Deleted: false,
        }
        && (entry.State != EntityState.Modified || SeriesScope.IsRestamping);

    /// <summary>
    /// Czy wiersz <b>właśnie przestał</b> być wyliczalny z serii.
    /// </summary>
    /// <remarks>
    /// Pytane o stan sprzed zapisu, bo po zapisie każdy już nie jest: oryginalne
    /// wartości pól mówią, czym wiersz był, zanim ktoś go dotknął.
    /// </remarks>
    private static bool Emerging(EntityEntry<Entity> entry)
    {
        if (entry.Entity is not TaskItem { SeriesId: not null })
        {
            return false;
        }

        var before = entry.Property(nameof(TaskItem.State));
        var pinned = entry.Property(nameof(TaskItem.Overridden));
        var done = entry.Property(nameof(TaskItem.CompletedAt));
        var gone = entry.Property(nameof(TaskItem.Deleted));

        return before.OriginalValue is TaskState.Scheduled
            && pinned.OriginalValue is false
            && done.OriginalValue is null
            && gone.OriginalValue is false;
    }

    private static void Journal(DbContext context)
    {
        // Zmiany przychodzące ze scalania nie są zmianami tego urządzenia.
        // Zapisanie ich do dziennika odesłałoby je z powrotem i dwa urządzenia
        // odbijałyby sobie te same wpisy bez końca — zob. SyncScope.
        if (SyncScope.IsApplyingRemote)
        {
            return;
        }

        // Migawka przed dopisaniem czegokolwiek: dopisywanie wierszy dziennika
        // zmienia śledzenie zmian, a modyfikowanie kolekcji w trakcie jej
        // przechodzenia kończy się wyjątkiem.
        var entries = context.ChangeTracker.Entries<Entity>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .Where(e => !Derived(e))
            .ToList();

        if (entries.Count == 0)
        {
            return;
        }

        // Jedno zapytanie na cały zapis, nie jedno na pole. Odpytywanie bazy
        // w pętli wewnątrz SavingChanges to i koszt, i proszenie się o kłopoty
        // z ponownym wejściem w potok zapisu.
        var ids = entries.Select(e => e.Entity.Id).ToHashSet();

        // **Najpierw śledzone, potem baza.** Znacznik dopisany wcześniej w tym samym
        // kontekście — a robi tak nakładanie zmian z synchronizacji — nie istnieje
        // jeszcze w bazie i zapytanie go nie widzi. Dopisanie drugiego z tym samym
        // kluczem wywraca **cały zapis** komunikatem o dwóch instancjach tego samego
        // wiersza, a wygląda to jak błąd w zadaniu, które się właśnie zapisywało.
        var stamps = context.ChangeTracker.Entries<FieldStamp>()
            .Where(e => e.State != EntityState.Deleted)
            .Select(e => e.Entity)
            .ToDictionary(s => (s.EntityType, s.EntityId, s.Field));

        // Zapytanie oddaje też te już śledzone — stąd TryAdd, a nie Add.
        foreach (var fromDb in context.Set<FieldStamp>().Where(s => ids.Contains(s.EntityId)))
        {
            stamps.TryAdd((fromDb.EntityType, fromDb.EntityId, fromDb.Field), fromDb);
        }

        foreach (var entry in entries)
        {
            var entityType = entry.Metadata.GetTableName() ?? entry.Metadata.ShortName();
            var id = entry.Entity.Id;
            var hlc = entry.Entity.UpdatedAt.ToString();

            // Znacznik zużyty przez tę zmianę musi przeżyć zamknięcie aplikacji,
            // inaczej zegar wystartuje od zera i cofnięty zegar ścienny sprawi,
            // że kolejne zmiany przegrają scalanie jako rzekomo starsze.
            LastHlcStore.Stage(context, entry.Entity.UpdatedAt);

            // **Całe wystąpienie, gdy przestaje być wyliczalne.** Dzień postawiony
            // z okna serii nie jedzie synchronizacją wcale — drugie urządzenie policzy
            // go sobie samo. Ale w chwili, w której przestaje być tym, co okno
            // wylicza — bo został odhaczony, wyrzucony albo zmieniony z ręki —
            // drugie urządzenie musi dostać go **w całości**. Same zmienione pola
            // opisywałyby wtedy wiersz, którego tamta strona może jeszcze nie mieć:
            // jej okno bywa krótsze, a wtedy z „zrobione" bez nazwy i bez dnia
            // powstałoby zadanie-widmo.
            var whole = entry.State == EntityState.Modified && Emerging(entry);

            foreach (var property in entry.Properties)
            {
                // Klucz główny pomijany: identyfikator jest w każdym wierszu dziennika
                // jako EntityId, więc jako pole byłby powtórzeniem.
                if (property.Metadata.IsPrimaryKey()
                    || (!whole && !Changed(entry.State, property)))
                {
                    continue;
                }

                var name = property.Metadata.Name;

                context.Add(new ChangeEntry(
                    Guid.CreateVersion7(), entityType, id, name, Encode(property), hlc));

                if (stamps.TryGetValue((entityType, id, name), out var stamp))
                {
                    stamp.Update(hlc);
                }
                else
                {
                    var fresh = new FieldStamp(entityType, id, name, hlc);
                    context.Add(fresh);
                    stamps[(entityType, id, name)] = fresh;
                }
            }
        }
    }

    private static bool Changed(EntityState state, PropertyEntry property) =>
        state == EntityState.Added ? property.CurrentValue is not null : property.IsModified;

    /// <summary>
    /// Wartość w postaci, w jakiej trafia do bazy — po konwerterze, jeśli jest.
    /// Dzięki temu dziennik niesie dokładnie to, co kolumna, i nie zależy od tego,
    /// jak dana wersja aplikacji odwzorowuje typ na bazę.
    /// </summary>
    private static string? Encode(PropertyEntry property)
    {
        var value = property.CurrentValue;
        if (value is null)
        {
            return null;
        }

        var converter = property.Metadata.GetValueConverter();
        var stored = converter is null ? value : converter.ConvertToProvider(value);

        return stored is null ? null : JsonSerializer.Serialize(stored, Json);
    }
}

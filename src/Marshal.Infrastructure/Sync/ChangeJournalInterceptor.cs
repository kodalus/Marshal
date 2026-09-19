using System.Text.Encodings.Web;
using System.Text.Json;
using Marshal.Domain.Primitives;
using Marshal.Domain.Sync;
using Microsoft.EntityFrameworkCore;
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

            foreach (var property in entry.Properties)
            {
                // Klucz główny pomijany: identyfikator jest w każdym wierszu dziennika
                // jako EntityId, więc jako pole byłby powtórzeniem.
                if (property.Metadata.IsPrimaryKey() || !Changed(entry.State, property))
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

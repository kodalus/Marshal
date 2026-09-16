using Marshal.Domain.Primitives;

namespace Marshal.Domain.Filters;

/// <summary>
/// Zapisany widok — filtr z nazwą, w Ulubionych (spec 11, 11.5).
/// </summary>
/// <remarks>
/// <para>
/// Sens zapisywania nie jest w oszczędzeniu kliknięć. Filtr ułożony raz i nazwany
/// („Telefony", „Kwadrans przed wyjściem", „Zaległe w domu") jest **decyzją podjętą na
/// spokojnie**, z której można potem skorzystać w chwili, w której na układanie
/// warunków nie ma ani cierpliwości, ani uwagi. Ulubione to zapas gotowych odpowiedzi
/// na pytanie „co teraz", zrobiony wtedy, kiedy dało się myśleć.
/// </para>
/// <para>
/// Synchronizowany: widok ułożony przy komputerze ma być na telefonie, bo to na
/// telefonie zwykle pada pytanie.
/// </para>
/// </remarks>
public sealed class SavedFilter : Entity
{
    private SavedFilter()
    {
        Name = string.Empty;
        DefinitionJson = string.Empty;
    }

    private SavedFilter(Guid id, DateTimeOffset createdAt, Hlc updatedAt, string name, FilterQuery query)
        : base(id, createdAt, updatedAt)
    {
        Name = Normalize(name);
        DefinitionJson = query.ToJson();
        _query = query;
        _parsed = true;
    }

    public static SavedFilter Create(string name, FilterQuery query, DateTimeOffset now, Hlc stamp)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.IsEmpty)
        {
            throw new ArgumentException(
                "Widok bez żadnego warunku nie pokazałby niczego, więc nie ma czego zapisywać.",
                nameof(query));
        }

        return new SavedFilter(Guid.CreateVersion7(), now, stamp, name, query);
    }

    public string Name { get; private set; }

    /// <summary>Warunki w postaci bazodanowej: jeden tekst JSON, jedna kolumna.</summary>
    public string DefinitionJson { get; private set; }

    /// <summary>Pozycja w Ulubionych. Wstawienie między sąsiadów to średnia ich wartości.</summary>
    public double SortOrder { get; private set; }

    /// <summary>
    /// Warunki albo <c>null</c>, gdy zapis okazał się nieczytelny.
    /// </summary>
    /// <remarks>
    /// Zapis nieczytelny nie kasuje widoku. Nazwa zostaje widoczna, więc wiadomo, co
    /// przepadło i co ułożyć na nowo — w odróżnieniu od pozycji, która znika bez słowa.
    /// </remarks>
    public FilterQuery? Query
    {
        get
        {
            if (!_parsed)
            {
                _query = FilterQuery.FromJson(DefinitionJson);
                _parsed = true;
            }

            return _query;
        }
    }

    private FilterQuery? _query;

    private bool _parsed;

    public void Rename(string name, Hlc stamp)
    {
        Name = Normalize(name);
        Touch(stamp);
    }

    public void SetQuery(FilterQuery query, Hlc stamp)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.IsEmpty)
        {
            throw new ArgumentException("Widok bez warunków nie pokazałby niczego.", nameof(query));
        }

        DefinitionJson = query.ToJson();
        _query = query;
        _parsed = true;
        Touch(stamp);
    }

    public void SetSortOrder(double sortOrder, Hlc stamp)
    {
        SortOrder = sortOrder;
        Touch(stamp);
    }

    private static string Normalize(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Trim();
    }
}

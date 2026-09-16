namespace Marshal.Domain.Filters;

/// <summary>
/// Pole, którego dotyczy warunek filtra (spec 11.5).
/// </summary>
/// <remarks>
/// Lista jest zamknięta i krótka celowo. Filtr po każdym polu modelu dałby konstruktor,
/// w którym trzeba szukać — a filtr, którego składanie trwa dłużej niż przejrzenie listy
/// ręcznie, nie zostanie użyty drugi raz.
/// </remarks>
public enum FilterField
{
    /// <summary>Stan zadania. Zbiór wartości — „następne albo zaplanowane".</summary>
    State,

    /// <summary>Obszar odpowiedzialności. <see cref="System.Guid.Empty"/> znaczy „bez obszaru".</summary>
    Area,

    /// <summary>Projekt. <see cref="System.Guid.Empty"/> znaczy „bez projektu" (spec 5.3).</summary>
    Project,

    /// <summary>Tag. Pasuje zadanie mające **którykolwiek** ze wskazanych.</summary>
    Tag,

    Priority,

    Energy,

    /// <summary>Termin zewnętrzny. Okno względne, nie data — zob. <see cref="DateWindow"/>.</summary>
    Deadline,

    /// <summary>Dzień wykonania. Okno względne.</summary>
    DoDate,

    /// <summary>„Mieści się w X minut". Nieoszacowane nie pasuje nigdy.</summary>
    Estimate,

    /// <summary>Szukany tekst w tytule albo w notatce zadania.</summary>
    Text,
}

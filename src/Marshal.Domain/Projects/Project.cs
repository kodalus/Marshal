using Marshal.Domain.Primitives;

namespace Marshal.Domain.Projects;

/// <summary>
/// Projekt — wszystko, co wymaga więcej niż jednego kroku (spec 5.4).
/// </summary>
/// <remarks>
/// Pole nazywa się <see cref="Outcome"/>, nie <c>Name</c>, i jest to decyzja celowa.
/// GTD wymaga opisania projektu **stanem docelowym** („Zimowe opony są na aucie"),
/// nie czynnością („wymiana opon"). Bez tego niezmiennik N1 — projekt bez następnej
/// akcji — traci sens, bo nie wiadomo, kiedy projekt jest skończony.
///
/// Projekt nadrzędny to trzeci horyzont GTD, czyli cel. Nie ma osobnej encji celu:
/// cel to projekt o dłuższym horyzoncie (spec 1.8).
/// </remarks>
public sealed class Project : Entity
{
    private Project()
    {
        Outcome = string.Empty;
    }

    public Project(
        Guid id,
        DateTimeOffset createdAt,
        Hlc updatedAt,
        string outcome,
        Guid areaId,
        double sortOrder,
        Guid? parentProjectId = null)
        : base(id, createdAt, updatedAt)
    {
        if (areaId == Guid.Empty)
        {
            throw new ArgumentException("Projekt musi należeć do obszaru (N11).", nameof(areaId));
        }

        Outcome = NormalizeOutcome(outcome);
        AreaId = areaId;
        SortOrder = sortOrder;
        ParentProjectId = parentProjectId;
        State = ProjectState.Active;
    }

    /// <summary>Stan docelowy, nie czynność. Formularz pyta „po czym poznasz, że to skończone?".</summary>
    public string Outcome { get; private set; }

    /// <summary>Markdown.</summary>
    public string? Note { get; private set; }

    public ProjectState State { get; private set; }

    /// <summary>Wymagany. Podprojekt dziedziczy obszar nadrzędnego i nie może go zmienić.</summary>
    public Guid AreaId { get; private set; }

    /// <summary>Zagnieżdżanie; projekt nadrzędny pełni rolę celu (H3).</summary>
    public Guid? ParentProjectId { get; private set; }

    public double SortOrder { get; private set; }

    public void Rename(string outcome, Hlc stamp)
    {
        Outcome = NormalizeOutcome(outcome);
        Touch(stamp);
    }

    public void SetNote(string? note, Hlc stamp)
    {
        Note = string.IsNullOrWhiteSpace(note) ? null : note;
        Touch(stamp);
    }

    public void SetState(ProjectState state, Hlc stamp)
    {
        State = state;
        Touch(stamp);
    }

    public void SetSortOrder(double sortOrder, Hlc stamp)
    {
        SortOrder = sortOrder;
        Touch(stamp);
    }

    /// <summary>
    /// Przypięcie pod projekt nadrzędny. Obszar jest przejmowany z nadrzędnego —
    /// inaczej cel rozjechałby się po kilku obszarach i przestał być policzalny (N11).
    /// </summary>
    public void AttachTo(Project parent, Hlc stamp)
    {
        ArgumentNullException.ThrowIfNull(parent);

        if (parent.Id == Id)
        {
            throw new InvalidOperationException("Projekt nie może być własnym nadrzędnym.");
        }

        ParentProjectId = parent.Id;
        AreaId = parent.AreaId;
        Touch(stamp);
    }

    public void Detach(Hlc stamp)
    {
        ParentProjectId = null;
        Touch(stamp);
    }

    private static string NormalizeOutcome(string outcome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        return outcome.Trim();
    }
}

namespace Marshal.Domain.Tasks;

/// <summary>
/// Waga zadania (spec 1.6). **Bez limitu** — przez rok sto zadań może zasłużyć na
/// <see cref="High"/> i nie jest to inflacja, tylko prawda. Limit dotyczy wyboru na
/// dany dzień (<c>FocusDate</c>, etap 6), nie wagi.
/// </summary>
public enum Priority
{
    None = 0,
    Low = 1,
    Normal = 2,
    High = 3,
}

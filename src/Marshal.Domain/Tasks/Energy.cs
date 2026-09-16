namespace Marshal.Domain.Tasks;

/// <summary>
/// Ile trzeba mieć w sobie, żeby to ruszyć (spec 5.3).
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> nie jest tym samym co <see cref="Low"/>. Zadanie
/// nieoszacowane trafia do „Teraz" przy **każdym** poziomie energii, bo nie ma podstaw,
/// żeby je odsiać; zadanie oznaczone jako lekkie zostało tak oznaczone świadomie.
/// Sklejenie tych dwóch wartości ukryłoby brak decyzji pod pozorem decyzji.
/// </remarks>
public enum Energy
{
    Unknown = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

namespace Marshal.Application.Abstractions;

/// <summary>Czas fizyczny. Wydzielony, żeby testy nie zależały od zegara maszyny.</summary>
public interface IClock
{
    DateTimeOffset Now { get; }
}

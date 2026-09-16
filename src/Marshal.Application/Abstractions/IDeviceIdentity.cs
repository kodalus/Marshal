namespace Marshal.Application.Abstractions;

/// <summary>
/// Identyfikator tego urządzenia: rozstrzyga remisy zegara logicznego (spec 3.5)
/// i nazywa dziennik w składnicy synchronizacji (spec 9.2). Trwały i różny na
/// każdym urządzeniu.
/// </summary>
public interface IDeviceIdentity
{
    string Id { get; }
}

using Marshal.Application.Abstractions;

namespace Marshal.Infrastructure.Time;

/// <summary>
/// Zegar systemowy przeliczony na strefę z ustawień (spec 3.4).
/// </summary>
/// <remarks>
/// Punktem wyjścia jest <see cref="DateTimeOffset.UtcNow"/>, nie <c>Now</c>: strefa
/// systemu nie ma tu nic do rzeczy, a przeliczanie z jednej lokalnej do drugiej
/// przechodziłoby przez wartość, która przy zmianie czasu bywa niejednoznaczna.
/// </remarks>
public sealed class SystemClock(ISettings settings) : IClock
{
    public DateTimeOffset Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, settings.Zone);
}

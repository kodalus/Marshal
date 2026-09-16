using Marshal.Domain.Primitives;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Marshal.Infrastructure.Data;

/// <summary>
/// Zegar logiczny w bazie jako tekst „czas.licznik.urządzenie" — czytelny przy
/// diagnostyce i identyczny z postacią w logu synchronizacji (spec 9.3).
/// </summary>
public sealed class HlcConverter : ValueConverter<Hlc, string>
{
    public HlcConverter()
        : base(hlc => hlc.ToString(), text => Hlc.Parse(text))
    {
    }
}

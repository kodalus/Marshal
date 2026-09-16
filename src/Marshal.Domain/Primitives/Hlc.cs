namespace Marshal.Domain.Primitives;

/// <summary>
/// Hybrydowy zegar logiczny — znacznik każdej zmiany podlegającej synchronizacji
/// (spec 3.5). Trójka: czas ścienny w milisekundach, licznik zdarzeń w tej samej
/// milisekundzie, identyfikator urządzenia.
/// </summary>
/// <remarks>
/// Zegar systemowy telefonu bywa przestawiony. Porównywanie po <c>DateTime.UtcNow</c>
/// daje niedeterministyczne scalanie — rekord „z przyszłości" wygrywa na zawsze.
/// HLC jest monotoniczny mimo cofnięcia zegara, a identyfikator urządzenia rozstrzyga
/// remisy deterministycznie: oba urządzenia dochodzą do tego samego wyniku niezależnie
/// od kolejności scalania.
/// </remarks>
public readonly struct Hlc : IComparable<Hlc>, IEquatable<Hlc>
{
    /// <summary>Separator w postaci tekstowej — stąd zakaz kropki w identyfikatorze.</summary>
    public const char Separator = '.';

    public long WallMs { get; }
    public int Counter { get; }
    public string DeviceId { get; }

    public Hlc(long wallMs, int counter, string deviceId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(wallMs);
        ArgumentOutOfRangeException.ThrowIfNegative(counter);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        if (deviceId.Contains(Separator))
        {
            throw new ArgumentException(
                $"Identyfikator urządzenia nie może zawierać '{Separator}' — to separator postaci tekstowej.",
                nameof(deviceId));
        }

        WallMs = wallMs;
        Counter = counter;
        DeviceId = deviceId;
    }

    /// <summary>Stan początkowy urządzenia, przed pierwszą zmianą.</summary>
    public static Hlc Zero(string deviceId) => new(0, 0, deviceId);

    /// <summary>
    /// Znacznik dla zdarzenia lokalnego. Monotoniczny: cofnięcie zegara systemowego
    /// nie cofa znacznika, tylko zwiększa licznik.
    /// </summary>
    public static Hlc Next(Hlc last, long physicalNowMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(physicalNowMs);

        var wall = Math.Max(last.WallMs, physicalNowMs);
        var counter = wall == last.WallMs ? last.Counter + 1 : 0;
        return new Hlc(wall, counter, last.DeviceId);
    }

    /// <summary>
    /// Znacznik po odebraniu zmiany z innego urządzenia. Podnosi zegar lokalny ponad
    /// zdalny, żeby kolejna zmiana lokalna była późniejsza od wszystkiego, co widziano.
    /// </summary>
    public static Hlc Merge(Hlc last, Hlc remote, long physicalNowMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(physicalNowMs);

        var wall = Math.Max(Math.Max(last.WallMs, remote.WallMs), physicalNowMs);

        var counter =
            wall == last.WallMs && wall == remote.WallMs ? Math.Max(last.Counter, remote.Counter) + 1
            : wall == last.WallMs ? last.Counter + 1
            : wall == remote.WallMs ? remote.Counter + 1
            : 0;

        return new Hlc(wall, counter, last.DeviceId);
    }

    /// <summary>
    /// Porządek: czas ścienny, potem licznik, na końcu identyfikator urządzenia.
    /// Ostatni człon istnieje po to, żeby porządek był pełny — dwa urządzenia
    /// rozstrzygają remis tak samo, bez uzgadniania.
    /// </summary>
    public int CompareTo(Hlc other)
    {
        var byWall = WallMs.CompareTo(other.WallMs);
        if (byWall != 0) return byWall;

        var byCounter = Counter.CompareTo(other.Counter);
        if (byCounter != 0) return byCounter;

        return string.CompareOrdinal(DeviceId, other.DeviceId);
    }

    public override string ToString() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{WallMs}{Separator}{Counter}{Separator}{DeviceId}");

    public static Hlc Parse(string value) =>
        TryParse(value, out var hlc)
            ? hlc
            : throw new FormatException($"Nieprawidłowa postać HLC: '{value}'.");

    public static bool TryParse(string? value, out Hlc result)
    {
        result = default;
        if (string.IsNullOrEmpty(value)) return false;

        var first = value.IndexOf(Separator);
        if (first <= 0) return false;

        var second = value.IndexOf(Separator, first + 1);
        if (second <= first + 1 || second == value.Length - 1) return false;

        if (!long.TryParse(value.AsSpan(0, first),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var wall))
        {
            return false;
        }

        if (!int.TryParse(value.AsSpan(first + 1, second - first - 1),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var counter))
        {
            return false;
        }

        var deviceId = value[(second + 1)..];
        if (deviceId.Contains(Separator)) return false;

        result = new Hlc(wall, counter, deviceId);
        return true;
    }

    public bool Equals(Hlc other) =>
        WallMs == other.WallMs && Counter == other.Counter && DeviceId == other.DeviceId;

    public override bool Equals(object? obj) => obj is Hlc other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(WallMs, Counter, DeviceId);

    public static bool operator ==(Hlc left, Hlc right) => left.Equals(right);
    public static bool operator !=(Hlc left, Hlc right) => !left.Equals(right);
    public static bool operator <(Hlc left, Hlc right) => left.CompareTo(right) < 0;
    public static bool operator >(Hlc left, Hlc right) => left.CompareTo(right) > 0;
    public static bool operator <=(Hlc left, Hlc right) => left.CompareTo(right) <= 0;
    public static bool operator >=(Hlc left, Hlc right) => left.CompareTo(right) >= 0;
}

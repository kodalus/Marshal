using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Marshal.Domain.Recurrence;

/// <summary>
/// Tożsamość wystąpienia — **wyliczana z właściciela i dnia, nie losowana**.
/// </summary>
/// <remarks>
/// <para>
/// Przejście dnia miało być powtarzalne bez skutków ubocznych, „bo dwa urządzenia robią
/// to samo, niezależnie i bez umawiania się, które ma". Na jednym urządzeniu było:
/// reguła schodzi z wystąpienia, które regułę niosło, więc drugie uruchomienie nie ma
/// już czego przetwarzać. <b>Między urządzeniami nie było.</b> Każde losowało
/// następnikowi własny identyfikator, więc telefon i pulpit, które ten sam dzień
/// przekroczyły osobno, tworzyły dwa różne zadania — a scalanie, które rozpoznaje
/// rzeczy po identyfikatorze, przyjmowało oba jako dwie różne rzeczy. Seria podwajała
/// się na siatce i podwajała każdą swoją zapowiedź.
/// </para>
/// <para>
/// Identyfikator liczony z <b>właściciela i dnia</b> sprawia, że oba urządzenia dochodzą
/// do tej samej rzeczy: scalanie widzi jedną rzecz zapisaną dwa razy i składa ją w jedną,
/// zamiast rozstawiać dwie obok siebie. To jest ta sama odpowiedź, co przy zapowiedziach
/// rytmu — wyliczać, a nie przechowywać — tyle że o identyfikator.
/// </para>
/// <para>
/// <b>Właścicielem jest seria albo nawyk, nigdy poprzednie wystąpienie.</b> Pierwsze
/// podejście liczyło z poprzednika: <c>f(poprzednik, dzień)</c>. Funkcja była
/// deterministyczna, ale jej wejścia nie były wspólne — dzień, na który wypada następnik,
/// zależał od tego, kiedy dane urządzenie zostało otwarte. Telefon otwarty w poniedziałek
/// i pulpit otwarty w środę liczyły różne daty, więc różne identyfikatory, a stąd dwa
/// rozłączne łańcuchy, które reguła sprowadzała z powrotem na te same dni. Seria i nawyk
/// są faktami o rzeczy, nie o historii urządzenia, i dlatego nadają się na wejście.
/// </para>
/// <para>
/// Wersja 8, czyli „identyfikator z nazwy": mówi wprost, że te bity nie są losowe
/// i nie niosą czasu. Skrót jest po to, żeby z identyfikatora poprzednika nie dało się
/// odczytać niczego innego — nie po to, żeby czegokolwiek pilnować.
/// </para>
/// </remarks>
public static class OccurrenceId
{
    /// <summary>Identyfikator dnia <paramref name="day"/> u właściciela <paramref name="owner"/>.</summary>
    /// <remarks>
    /// Nazwa mówi „w czym", nie „po czym": liczy się z rzeczy trwałej — serii albo
    /// nawyku — a nie z wystąpienia poprzedniego.
    /// </remarks>
    public static Guid For(Guid owner, DateOnly day)
    {
        Span<byte> seed = stackalloc byte[20];

        owner.TryWriteBytes(seed);
        BinaryPrimitives.WriteInt32LittleEndian(seed[16..], day.DayNumber);

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(seed, digest);

        var id = digest[..16];

        // Wersja i wariant wpisywane wprost: bez nich byłby to po prostu szesnaście
        // bajtów, a baza i synchronizacja mają prawo zakładać, że identyfikator jest
        // poprawnym UUID-em.
        id[6] = (byte)((id[6] & 0x0F) | 0x80);
        id[8] = (byte)((id[8] & 0x3F) | 0x80);

        // Kolejność sieciowa, nie pamięciowa. Zwykły konstruktor czyta pierwsze osiem
        // bajtów po swojemu i wtedy nibble wersji wypada w bajcie siódmym zamiast
        // szóstym — czyli wpisana wyżej wersja oznaczałaby coś innego, niż mówi.
        return new Guid(id, bigEndian: true);
    }
}

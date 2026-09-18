using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Contacts;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Repositories;

public sealed class ContactRepository(MarshalDbContext db, IKolejkaBazy? kolejka = null)
    : IContactRepository
{
    private readonly IKolejkaBazy _kolejka = kolejka ?? new KolejkaWprost();

    public async Task<Contact?> FindAsync(Guid id, CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(
            () => db.Contacts.FirstOrDefaultAsync(k => k.Id == id && !k.Deleted, ct), ct);

    /// <summary>
    /// Po adresie, porównanie po stronie klienta.
    /// </summary>
    /// <remarks>
    /// SQLite nie rozróżnia wielkości liter tylko w zakresie ASCII, a adresy bywają
    /// z ogonkami w części przed małpą. Osób jest kilka, nie tysiące — koszt bez znaczenia,
    /// a „Ewa@…" zapisana drugi raz jako „ewa@…" byłaby dwiema pozycjami na tę samą osobę.
    /// </remarks>
    public async Task<Contact?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        var szukany = (email ?? string.Empty).Trim();

        var wszystkie = await _kolejka.WykonajAsync(
            () => db.Contacts.Where(k => !k.Deleted).ToListAsync(ct), ct);

        return wszystkie.FirstOrDefault(
            k => string.Equals(k.Email, szukany, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<Contact>> AllAsync(CancellationToken ct = default) =>
        await _kolejka.WykonajAsync(() => db.Contacts
            .Where(k => !k.Deleted)
            .OrderBy(k => k.Name)
            .ToListAsync(ct), ct);

    public void Add(Contact contact) => db.Contacts.Add(contact);
}

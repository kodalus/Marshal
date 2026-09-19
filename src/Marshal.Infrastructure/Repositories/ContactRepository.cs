using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Contacts;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Repositories;

public sealed class ContactRepository(MarshalDbContext db, IDbQueue? queue = null)
    : IContactRepository
{
    private readonly IDbQueue _kolejka = queue ?? new KolejkaWprost();

    public async Task<Contact?> FindAsync(Guid id, CancellationToken ct = default) =>
        await _kolejka.RunAsync(
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
        var wanted = (email ?? string.Empty).Trim();

        var all = await _kolejka.RunAsync(
            () => db.Contacts.Where(k => !k.Deleted).ToListAsync(ct), ct);

        return all.FirstOrDefault(
            k => string.Equals(k.Email, wanted, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<Contact>> AllAsync(CancellationToken ct = default) =>
        await _kolejka.RunAsync(() => db.Contacts
            .Where(k => !k.Deleted)
            .OrderBy(k => k.Name)
            .ToListAsync(ct), ct);

    public void Add(Contact contact) => db.Contacts.Add(contact);
}

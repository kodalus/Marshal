using Marshal.Domain.Contacts;

namespace Marshal.Application.Repositories;

/// <summary>Osoby, którym pokazuje się pojedyncze wydarzenia.</summary>
public interface IContactRepository
{
    Task<Contact?> FindAsync(Guid id, CancellationToken ct = default);

    /// <summary>Po adresie, bez rozróżniania wielkości liter — do wykrycia powtórki.</summary>
    Task<Contact?> FindByEmailAsync(string email, CancellationToken ct = default);

    Task<IReadOnlyList<Contact>> AllAsync(CancellationToken ct = default);

    void Add(Contact contact);
}

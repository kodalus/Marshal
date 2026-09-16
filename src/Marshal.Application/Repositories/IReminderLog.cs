namespace Marshal.Application.Repositories;

/// <summary>Co to urządzenie już pokazało. Lokalne, poza synchronizacją.</summary>
public interface IReminderLog
{
    Task<bool> WasShownAsync(Guid taskId, DateTimeOffset reminderAt, CancellationToken ct = default);

    void Record(Guid taskId, DateTimeOffset reminderAt, DateTimeOffset shownAt);
}

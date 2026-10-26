using System.Collections.Concurrent;
using Ta9.AuditService.Application;
using Ta9.AuditService.Domain;

namespace Ta9.AuditService.Infrastructure;

/// <summary>Process-local storage with the same semantics as the Mongo implementation (first write wins, copies in/out).</summary>
public sealed class InMemoryAuditRepository : IAuditRepository
{
    private readonly ConcurrentDictionary<Guid, AuditEntry> _entries = new();

    public Task<bool> TryInsertAsync(AuditEntry entry, CancellationToken cancellationToken) =>
        Task.FromResult(_entries.TryAdd(entry.JobId, entry.Clone()));

    public Task<AuditEntry?> GetAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromResult(_entries.TryGetValue(jobId, out var entry) ? entry.Clone() : null);
}

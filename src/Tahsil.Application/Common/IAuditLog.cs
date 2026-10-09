using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;

namespace Tahsil.Application.Common;

public interface IAuditLog
{
    /// <summary>
    /// Adds an audit event to the current unit of work, so it is saved in the same transaction as the change it
    /// describes. The actor defaults to the current caller.
    /// </summary>
    void Record(
        string action,
        AuditSubject? subject = null,
        IReadOnlyDictionary<string, object?>? data = null,
        Actor? actor = null,
        Guid? customerId = null);
}

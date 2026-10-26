namespace Ta9.JobService.Application;

/// <summary>
/// The job store cannot be reached (timeout, connection refused). Storage implementations translate their own
/// driver exceptions into this one, so the API and application layers never depend on a specific database.
/// </summary>
public sealed class StorageUnavailableException(string message, Exception innerException) : Exception(message, innerException);

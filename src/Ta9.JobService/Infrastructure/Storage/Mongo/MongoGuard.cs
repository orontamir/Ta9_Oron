using MongoDB.Driver;
using Ta9.JobService.Application;

namespace Ta9.JobService.Infrastructure.Storage.Mongo;

/// <summary>Translates "database unreachable" driver exceptions into the storage-neutral <see cref="StorageUnavailableException"/>.</summary>
internal static class MongoGuard
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            throw new StorageUnavailableException("MongoDB is unreachable.", ex);
        }
    }

    public static async Task RunAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            throw new StorageUnavailableException("MongoDB is unreachable.", ex);
        }
    }

    // Server selection timeouts surface as TimeoutException; broken connections as MongoConnectionException.
    private static bool IsUnavailable(Exception ex) => ex is TimeoutException or MongoConnectionException;
}

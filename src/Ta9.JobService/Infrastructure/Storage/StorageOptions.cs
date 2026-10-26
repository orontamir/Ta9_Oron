using System.ComponentModel.DataAnnotations;

namespace Ta9.JobService.Infrastructure.Storage;

public enum StorageProvider
{
    Mongo,
    InMemory
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public StorageProvider Provider { get; set; } = StorageProvider.Mongo;

    public MongoStorageOptions Mongo { get; set; } = new();
}

public sealed class MongoStorageOptions
{
    [Required]
    public string ConnectionString { get; set; } = "mongodb://localhost:27017";

    [Required]
    public string Database { get; set; } = "ta9_jobs";
}

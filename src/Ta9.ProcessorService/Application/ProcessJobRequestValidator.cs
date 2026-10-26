using System.Text;
using Ta9.Contracts;
using Ta9.Contracts.Processing;

namespace Ta9.ProcessorService.Application;

/// <summary>Business validation of a processing request. Returns field → error messages (empty when valid).</summary>
public static class ProcessJobRequestValidator
{
    public static IReadOnlyDictionary<string, string[]> Validate(ProcessJobRequest? request)
    {
        var errors = new Dictionary<string, List<string>>();
        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list))
            {
                errors[field] = list = [];
            }
            list.Add(message);
        }

        if (request is null)
        {
            Add("body", "Request body is required.");
            return Flatten(errors);
        }

        if (request.JobId == Guid.Empty)
        {
            Add("jobId", "JobId must not be empty.");
        }

        if (request.Documents is null || request.Documents.Count == 0)
        {
            Add("documents", "At least one document is required.");
            return Flatten(errors);
        }

        if (request.Documents.Count > JobLimits.MaxDocumentsPerJob)
        {
            Add("documents", $"A job may contain at most {JobLimits.MaxDocumentsPerJob} documents.");
        }

        var seenIndexes = new HashSet<int>();
        for (var i = 0; i < request.Documents.Count; i++)
        {
            var document = request.Documents[i];
            var prefix = $"documents[{i}]";
            if (document is null)
            {
                Add(prefix, "Document must not be null.");
                continue;
            }

            if (document.Index < 0)
            {
                Add($"{prefix}.index", "Index must be zero or positive.");
            }
            else if (!seenIndexes.Add(document.Index))
            {
                Add($"{prefix}.index", $"Duplicate index {document.Index}.");
            }

            if (document.Name is { Length: > JobLimits.MaxDocumentNameLength })
            {
                Add($"{prefix}.name", $"Name must be at most {JobLimits.MaxDocumentNameLength} characters.");
            }

            if (document.Content is null)
            {
                Add($"{prefix}.content", "Content is required.");
            }
            else if (Encoding.UTF8.GetByteCount(document.Content) > JobLimits.MaxDocumentBytes)
            {
                Add($"{prefix}.content", $"Content must be at most {JobLimits.MaxDocumentBytes} bytes (UTF-8).");
            }
        }

        return Flatten(errors);
    }

    private static IReadOnlyDictionary<string, string[]> Flatten(Dictionary<string, List<string>> errors) =>
        errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
}

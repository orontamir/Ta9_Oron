using System.Text;
using Ta9.Contracts;

namespace Ta9.JobService.Api;

/// <summary>Validation of the public create-job request. Returns field → messages (empty when valid).</summary>
public static class CreateJobRequestValidator
{
    public static IReadOnlyDictionary<string, string[]> Validate(CreateJobRequest? request)
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

        if (request.Name is { Length: > JobLimits.MaxJobNameLength })
        {
            Add("name", $"Name must be at most {JobLimits.MaxJobNameLength} characters.");
        }

        if (request.Documents is null || request.Documents.Count == 0)
        {
            Add("documents", "At least one document is required.");
            return Flatten(errors);
        }

        if (request.Documents.Count > JobLimits.MaxDocumentsPerJob)
        {
            Add("documents", $"A job may contain at most {JobLimits.MaxDocumentsPerJob} documents.");
            return Flatten(errors); // don't produce thousands of per-document errors
        }

        for (var i = 0; i < request.Documents.Count; i++)
        {
            var document = request.Documents[i];
            var prefix = $"documents[{i}]";
            if (document is null)
            {
                Add(prefix, "Document must not be null.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(document.Name))
            {
                Add($"{prefix}.name", "Name is required.");
            }
            else if (document.Name.Length > JobLimits.MaxDocumentNameLength)
            {
                Add($"{prefix}.name", $"Name must be at most {JobLimits.MaxDocumentNameLength} characters.");
            }

            if (document.Content is null)
            {
                Add($"{prefix}.content", "Content is required (it may be empty).");
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

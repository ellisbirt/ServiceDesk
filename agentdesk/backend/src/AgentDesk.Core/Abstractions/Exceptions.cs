namespace AgentDesk.Core.Abstractions;

/// <summary>Base for errors that map to an RFC 9457 problem response.</summary>
public abstract class AgentDeskException(string message, Exception? inner = null) : Exception(message, inner)
{
    public abstract int StatusCode { get; }
    public abstract string Code { get; }
    public virtual string Title => Code.Replace('_', ' ');
}

public sealed class NotFoundException(string resource, string id)
    : AgentDeskException($"{resource} '{id}' was not found.")
{
    public override int StatusCode => 404;
    public override string Code => "not_found";
    public override string Title => "Not found";
}

/// <summary>The document changed since the caller read it (If-Match / ETag mismatch).</summary>
public sealed class ConcurrencyConflictException(string message = "The resource was modified by someone else. Reload and try again.")
    : AgentDeskException(message)
{
    public override int StatusCode => 412;
    public override string Code => "precondition_failed";
    public override string Title => "Precondition failed";
}

/// <summary>A document with the same id already exists.</summary>
public sealed class DuplicateException(string message) : AgentDeskException(message)
{
    public override int StatusCode => 409;
    public override string Code => "duplicate";
    public override string Title => "Duplicate";
}

public sealed class InvalidTransitionException(string message) : AgentDeskException(message)
{
    public override int StatusCode => 409;
    public override string Code => "invalid_transition";
    public override string Title => "Invalid state transition";
}

public sealed class ValidationException(IReadOnlyDictionary<string, string[]> errors)
    : AgentDeskException("One or more validation errors occurred.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
    public override int StatusCode => 400;
    public override string Code => "validation_failed";
    public override string Title => "Validation failed";

    public static ValidationException For(string field, string error) => new(new Dictionary<string, string[]> { [field] = [error] });
}

/// <summary>A feature needs configuration that is missing (for example no LLM role).</summary>
public sealed class FeatureUnavailableException(string message) : AgentDeskException(message)
{
    public override int StatusCode => 503;
    public override string Code => "feature_unavailable";
    public override string Title => "Feature unavailable";
}

/// <summary>Collects field errors and throws a single <see cref="ValidationException"/>.</summary>
public sealed class Validator
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public Validator Required(string field, string? value, int maxLength = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(value)) Add(field, "is required.");
        else if (value.Length > maxLength) Add(field, $"must be at most {maxLength} characters.");
        return this;
    }

    public Validator MaxLength(string field, string? value, int maxLength)
    {
        if (value is not null && value.Length > maxLength) Add(field, $"must be at most {maxLength} characters.");
        return this;
    }

    public Validator OneOf(string field, string? value, IReadOnlyCollection<string> allowed, bool optional = false)
    {
        if (value is null && optional) return this;
        if (value is null || !allowed.Contains(value)) Add(field, $"must be one of: {string.Join(", ", allowed)}.");
        return this;
    }

    public Validator Range(string field, int? value, int min, int max)
    {
        if (value is { } v && (v < min || v > max)) Add(field, $"must be between {min} and {max}.");
        return this;
    }

    public Validator When(bool condition, string field, string error)
    {
        if (condition) Add(field, error);
        return this;
    }

    public void Add(string field, string error)
    {
        if (!_errors.TryGetValue(field, out var list)) _errors[field] = list = [];
        list.Add(error);
    }

    public void ThrowIfInvalid()
    {
        if (_errors.Count > 0) throw new ValidationException(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
    }
}

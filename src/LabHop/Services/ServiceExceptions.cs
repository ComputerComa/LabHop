namespace LabHop.Services;

/// <summary>An expected business failure. Messages are written to be shown to users and agents.</summary>
public abstract class ServiceException(string message) : Exception(message);

/// <summary>The record does not exist, or the actor does not own it. The two cases are deliberately indistinguishable.</summary>
public sealed class NotFoundException(string entity, Guid id)
    : ServiceException($"{entity} {id} was not found.");

public sealed class ConcurrencyConflictException(string entity, Guid id, uint expectedVersion, uint currentVersion)
    : ServiceException($"{entity} {id} was changed by someone else: you have version {expectedVersion}, the current version is {currentVersion}. Nothing was saved.")
{
    public uint CurrentVersion { get; } = currentVersion;
}

public sealed class ValidationFailedException(IReadOnlyDictionary<string, string> errors)
    : ServiceException(string.Join(" ", errors.Values))
{
    public IReadOnlyDictionary<string, string> Errors { get; } = errors;
}

/// <summary>The operation is valid in general but not allowed in the record's current state.</summary>
public sealed class RuleViolationException(string message) : ServiceException(message);

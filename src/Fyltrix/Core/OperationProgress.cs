namespace Fyltrix.Core;

/// <summary>Progress for long-running work. <see cref="Fraction"/> is 0..1, or negative when unknown.</summary>
public readonly record struct OperationProgress(string Status, double Fraction = -1, string? Detail = null);

/// <summary>Raised when an operation stops because a safety limit was reached.</summary>
public sealed class OperationLimitException : Exception
{
    public OperationLimitException(string message) : base(message) { }
}

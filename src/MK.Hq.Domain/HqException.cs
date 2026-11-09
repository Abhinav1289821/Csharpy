namespace MK.Hq.Domain;

/// <summary>How a failure should be reported to a caller (maps to an HTTP status in the API).</summary>
public enum FailureKind
{
    /// <summary>The request itself is wrong; fix the input.</summary>
    Invalid,
    /// <summary>Something the request refers to does not exist.</summary>
    NotFound,
    /// <summary>The world changed since review, or someone else holds the record. Reload and retry.</summary>
    Conflict,
    /// <summary>HQ's configuration or data is outside what this engine has verified; nothing was written.</summary>
    Unsupported,
    /// <summary>HQ/SQL Server is unreachable or refused the operation.</summary>
    Unavailable,
    /// <summary>The engine cannot tell whether a commit happened. The operation journal will settle it.</summary>
    Uncertain,
    /// <summary>A bug: an internal invariant failed.</summary>
    Internal,
}

/// <summary>
/// A business-level failure with a stable machine code. Messages are written for the shop owner,
/// never contain customer data, and always mean "nothing was written" unless the kind is
/// <see cref="FailureKind.Uncertain"/>.
/// </summary>
public sealed class HqException : Exception
{
    public HqException(string code, string message, FailureKind kind = FailureKind.Invalid, object? detail = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Kind = kind;
        Detail = detail;
    }

    public string Code { get; }
    public FailureKind Kind { get; }
    public object? Detail { get; }

    public static HqException Invalid(string code, string message, object? detail = null) => new(code, message, FailureKind.Invalid, detail);
    public static HqException NotFound(string code, string message) => new(code, message, FailureKind.NotFound);
    public static HqException Conflict(string code, string message, object? detail = null) => new(code, message, FailureKind.Conflict, detail);
    public static HqException Unsupported(string code, string message, object? detail = null) => new(code, message, FailureKind.Unsupported, detail);
    public static HqException Internal(string message) => new("INTERNAL_INVARIANT", message, FailureKind.Internal);
}

public static class Guard
{
    public static void Require(bool condition, string code, string message, FailureKind kind = FailureKind.Invalid, object? detail = null)
    {
        if (!condition) throw new HqException(code, message, kind, detail);
    }

    public static void Supported(bool condition, string code, string message) =>
        Require(condition, code, message, FailureKind.Unsupported);

    public static void Invariant(bool condition, string message) =>
        Require(condition, "INTERNAL_INVARIANT", message, FailureKind.Internal);

    public static T NotNull<T>(T? value, string code, string message, FailureKind kind = FailureKind.NotFound) where T : class =>
        value ?? throw new HqException(code, message, kind);
}

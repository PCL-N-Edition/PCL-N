namespace PCL.Xsr;

/// <summary>
/// Identifies an XSR contract by its stable, human-readable development name.
/// </summary>
public readonly struct XsrSemanticId : IEquatable<XsrSemanticId>
{
    private readonly string? _value;

    private XsrSemanticId(string value)
    {
        _value = value;
    }

    /// <summary>
    /// Gets the identifier text, or an empty string for an uninitialized value.
    /// </summary>
    public string Value => _value ?? string.Empty;

    /// <summary>
    /// Gets a value indicating whether this identifier was parsed successfully.
    /// </summary>
    public bool IsAssigned => _value is not null;

    /// <summary>
    /// Creates an identifier from an opaque, non-empty value with no whitespace or control characters.
    /// </summary>
    public static XsrSemanticId Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!TryParse(value, out XsrSemanticId identifier))
        {
            throw new ArgumentException(
                "An XSR semantic identifier must be non-empty and contain no whitespace or control characters.",
                nameof(value));
        }

        return identifier;
    }

    /// <summary>
    /// Attempts to create an identifier without normalizing or changing its text.
    /// </summary>
    public static bool TryParse(string? value, out XsrSemanticId identifier)
    {
        if (string.IsNullOrEmpty(value) || value.Any(character =>
                char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            identifier = default;
            return false;
        }

        identifier = new XsrSemanticId(value);
        return true;
    }

    /// <inheritdoc />
    public bool Equals(XsrSemanticId other) =>
        StringComparer.Ordinal.Equals(_value, other._value);

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is XsrSemanticId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        _value is null ? 0 : StringComparer.Ordinal.GetHashCode(_value);

    /// <inheritdoc />
    public override string ToString() => Value;

    public static bool operator ==(XsrSemanticId left, XsrSemanticId right) => left.Equals(right);

    public static bool operator !=(XsrSemanticId left, XsrSemanticId right) => !left.Equals(right);
}

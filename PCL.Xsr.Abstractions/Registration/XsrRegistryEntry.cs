namespace PCL.Xsr;

/// <summary>
/// Binds one stable semantic identifier to its compact runtime identifier and descriptor.
/// </summary>
public readonly record struct XsrRegistryEntry<TDescriptor>(
    XsrSemanticId SemanticId,
    XsrRuntimeId RuntimeId,
    TDescriptor Descriptor)
    where TDescriptor : notnull;

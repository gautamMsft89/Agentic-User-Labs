namespace WorkIqFiles;

internal enum PrivateStateInvariant
{
    RecordIdentity, RequestLength, MetadataLength, ContextShape, ConfigurationShape, MessageIdLength,
    LifetimeOrder, LifetimeLimit, Revision, State, ResultLength, RecordBytes, ClaimedReadyWithoutResult, ConsentScope
}
internal enum PrivateLimitUnit { None, Characters, Bytes, Ticks }
internal sealed class PrivateStateValidationException(PrivateStateInvariant invariant,
    long? observed = null, long? maximum = null, PrivateLimitUnit unit = PrivateLimitUnit.None)
    : LabException("Private state record is malformed or outside bounds.")
{
    internal PrivateStateInvariant Invariant { get; } = invariant;
    internal long? Observed { get; } = observed;
    internal long? Maximum { get; } = maximum;
    internal PrivateLimitUnit Unit { get; } = unit;
}

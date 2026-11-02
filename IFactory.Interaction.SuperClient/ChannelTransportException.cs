namespace IFactory.Interaction.SuperClient;

/// <summary>A send or channel failure. BytesAccepted does not imply peer receipt or execution.</summary>
public sealed class ChannelTransportException : IOException
{
    public CloseReason Reason { get; }
    public long BytesAccepted { get; }
    public long BytesPending { get; }
    public ChannelTransportException(CloseReason reason, long accepted, long pending, Exception? inner = null)
        : base($"Transport failed: reason={reason}, accepted={accepted}, pending={pending}.", inner)
    { Reason = reason; BytesAccepted = accepted; BytesPending = pending; }
}

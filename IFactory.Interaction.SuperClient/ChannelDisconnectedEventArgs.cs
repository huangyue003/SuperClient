using System.Net;

namespace IFactory.Interaction.SuperClient;

public sealed class ChannelDisconnectedEventArgs<TPackage>(IChannel<TPackage> channel, CloseReason reason) : EventArgs
{
    public IChannel<TPackage> Channel { get; } = channel;
    public CloseReason Reason { get; } = reason;
    public string Identity => Channel.Identity;
}

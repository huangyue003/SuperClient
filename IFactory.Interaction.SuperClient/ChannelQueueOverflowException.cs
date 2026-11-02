namespace IFactory.Interaction.SuperClient;

internal sealed class ChannelQueueOverflowException(int pending, int capacity)
    : InvalidOperationException($"Decoded package queue is full: pending={pending}, capacity={capacity}.")
{
    public int Pending { get; } = pending;
    public int Capacity { get; } = capacity;
}

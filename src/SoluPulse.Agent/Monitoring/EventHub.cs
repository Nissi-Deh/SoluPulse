using System.Collections.Concurrent;
using System.Threading.Channels;
using SoluPulse.Contracts;

namespace SoluPulse.Agent.Monitoring;

/// <summary>Diffuse les événements (alerte ouverte, résolue…) aux applications connectées.</summary>
public sealed class EventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<AgentEvent>> _subscribers = new();

    public (Guid Id, ChannelReader<AgentEvent> Reader) Subscribe()
    {
        var channel = Channel.CreateBounded<AgentEvent>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    public void Publish(AgentEvent agentEvent)
    {
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(agentEvent);
        }
    }
}

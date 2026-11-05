using System.Text.Json;

namespace SoluPulse.Core.Collection;

public sealed class CollectorResultBuilder(string collector, TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DateTimeOffset _timestamp = timeProvider.GetUtcNow();
    private readonly List<Measurement> _measurements = [];
    private readonly List<SensorIssue> _issues = [];
    private readonly List<InventorySnapshot> _snapshots = [];

    public CollectorResultBuilder Add(string metric, double value, string unit)
    {
        if (!double.IsFinite(value))
        {
            return Issue(metric, "Valeur non numérique ignorée.");
        }

        _measurements.Add(new Measurement(_timestamp, MetricName.Normalize(metric), value, unit, collector));
        return this;
    }

    public CollectorResultBuilder Issue(string sensor, string message)
    {
        _issues.Add(new SensorIssue(collector, sensor, message, _timestamp));
        return this;
    }

    public CollectorResultBuilder Snapshot<T>(string kind, T value)
    {
        _snapshots.Add(new InventorySnapshot(kind, _timestamp, JsonSerializer.Serialize(value, JsonOptions)));
        return this;
    }

    /// <summary>Exécute une lecture de capteur et transforme toute erreur en <see cref="SensorIssue"/>.</summary>
    public async Task TryAsync(string sensor, Func<Task> read)
    {
        try
        {
            await read().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Issue(sensor, ex.Message);
        }
    }

    public void Try(string sensor, Action read)
    {
        try
        {
            read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Issue(sensor, ex.Message);
        }
    }

    public CollectorResult Build() => new(_measurements.ToArray(), _issues.ToArray(), _snapshots.ToArray());
}

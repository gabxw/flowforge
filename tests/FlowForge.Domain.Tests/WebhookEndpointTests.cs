using FlowForge.Domain.Webhooks;
using Xunit;
namespace FlowForge.Domain.Tests;
public sealed class WebhookEndpointTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
    [Fact]
    public void Disabled_endpoint_stays_disabled_after_rotation_and_roundtrip()
    {
        var endpoint = new WebhookEndpoint(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Start);
        endpoint.SetEnabled(false); endpoint.SetEnabled(false); endpoint.Rotate(Start.AddHours(1));
        var recovered = WebhookEndpoint.Restore(endpoint.Snapshot);
        Assert.False(recovered.Snapshot.Enabled); Assert.Equal(endpoint.Snapshot, recovered.Snapshot);
        recovered.SetEnabled(true); Assert.True(recovered.Snapshot.Enabled);
        Assert.Equal(Start.AddHours(1), recovered.Snapshot.RotatedAt);
    }
    [Fact]
    public void Rotation_rejects_time_before_creation_or_previous_rotation_without_changing_state()
    {
        var endpoint = new WebhookEndpoint(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Start);
        Assert.Throws<ArgumentException>(() => endpoint.Rotate(Start.AddTicks(-1)));
        endpoint.Rotate(Start.AddHours(2)); var snapshot = endpoint.Snapshot;
        Assert.Throws<ArgumentException>(() => endpoint.Rotate(Start.AddHours(1)));
        Assert.Equal(snapshot, endpoint.Snapshot);
        Assert.Throws<ArgumentException>(() => WebhookEndpoint.Restore(snapshot with { RotatedAt = Start.AddTicks(-1) }));
    }
    [Fact]
    public void Identity_and_utc_invariants_are_required_on_creation_and_restore()
    {
        Assert.Throws<ArgumentException>(() => new WebhookEndpoint(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), Start));
        Assert.Throws<ArgumentException>(() => new WebhookEndpoint(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), Start));
        Assert.Throws<ArgumentException>(() => new WebhookEndpoint(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, Start));
        Assert.Throws<ArgumentException>(() => new WebhookEndpoint(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Start.ToOffset(TimeSpan.FromHours(-3))));
    }
}

namespace FlowForge.Domain.Workflows;

public readonly struct NodePosition
{
    public NodePosition(double x, double y)
    {
        if (!double.IsFinite(x))
            throw new ArgumentOutOfRangeException(nameof(x), "A coordenada deve ser finita.");
        if (!double.IsFinite(y))
            throw new ArgumentOutOfRangeException(nameof(y), "A coordenada deve ser finita.");

        X = x;
        Y = y;
    }
    public double X { get; }
    public double Y { get; }
}

using System.Threading;

namespace PulseQueue.Core;

/// <summary>
/// Invalidates work from every input generation except the newest one.
/// </summary>
public sealed class InputGenerationGate
{
    private long current;

    public long Current => Volatile.Read(ref current);

    public long Begin() => Advance();

    public long Invalidate() => Advance();

    public bool IsCurrent(long generation) =>
        generation > 0 && generation == Current;

    private long Advance()
    {
        while (true)
        {
            var observed = Current;
            if (observed == long.MaxValue)
            {
                throw new InvalidOperationException("The input generation is exhausted.");
            }

            var next = observed + 1;
            if (Interlocked.CompareExchange(ref current, next, observed) == observed)
            {
                return next;
            }
        }
    }
}

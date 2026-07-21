namespace PulseQueue.Core;

public sealed class AdaptiveRttOptions
{
    public TimeSpan MinimumSuggestedHold { get; init; } = TimeSpan.FromMilliseconds(20);

    public TimeSpan MaximumSuggestedHold { get; init; } = OneShotActionBuffer.AbsoluteHoldCap;

    public TimeSpan MaximumAcceptedSample { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan OutlierFloor { get; init; } = TimeSpan.FromMilliseconds(25);

    public TimeSpan RebaseConsistencyTolerance { get; init; } = TimeSpan.FromMilliseconds(20);

    public TimeSpan SafetyMargin { get; init; } = TimeSpan.FromMilliseconds(5);

    public int WarmupSampleCount { get; init; } = 5;

    public int ConsistentOutliersToRebase { get; init; } = 3;

    public double SmoothingFactor { get; init; } = 0.125;

    public double VariationFactor { get; init; } = 0.25;

    public double OutlierVariationMultiplier { get; init; } = 4.0;

    public double SuggestedHoldVariationMultiplier { get; init; } = 2.0;
}

public enum RttSampleResult
{
    Accepted = 0,
    IgnoredInvalid,
    IgnoredOutlier,
    AcceptedRebase,
}

public sealed class AdaptiveRttEstimator
{
    private readonly AdaptiveRttOptions options;
    private readonly double effectiveMinimumTicks;
    private readonly double effectiveMaximumTicks;

    private double smoothedTicks;
    private double variationTicks;
    private int outlierClusterCount;
    private double outlierClusterMean;
    private double outlierClusterM2;

    public AdaptiveRttEstimator(AdaptiveRttOptions? options = null)
    {
        this.options = options ?? new AdaptiveRttOptions();
        ValidateOptions(this.options);

        effectiveMaximumTicks = Math.Min(
            this.options.MaximumSuggestedHold.Ticks,
            OneShotActionBuffer.AbsoluteHoldCap.Ticks);
        effectiveMinimumTicks = Math.Min(
            this.options.MinimumSuggestedHold.Ticks,
            effectiveMaximumTicks);
    }

    public bool HasEstimate { get; private set; }

    public int ObservedSampleCount { get; private set; }

    public int AcceptedSampleCount { get; private set; }

    public int IgnoredInvalidCount { get; private set; }

    public int IgnoredOutlierCount { get; private set; }

    public TimeSpan EstimatedRtt => HasEstimate
        ? FromRoundedTicks(smoothedTicks)
        : TimeSpan.Zero;

    public TimeSpan EstimatedVariation => HasEstimate
        ? FromRoundedTicks(variationTicks)
        : TimeSpan.Zero;

    public TimeSpan SuggestedHold
    {
        get
        {
            var rawTicks = HasEstimate
                ? smoothedTicks
                    + (options.SuggestedHoldVariationMultiplier * variationTicks)
                    + options.SafetyMargin.Ticks
                : effectiveMinimumTicks;
            return FromRoundedTicks(Math.Clamp(rawTicks, effectiveMinimumTicks, effectiveMaximumTicks));
        }
    }

    public RttSampleResult AddSample(TimeSpan roundTripTime)
    {
        ObservedSampleCount++;

        if (roundTripTime <= TimeSpan.Zero || roundTripTime > options.MaximumAcceptedSample)
        {
            IgnoredInvalidCount++;
            return RttSampleResult.IgnoredInvalid;
        }

        var sampleTicks = (double)roundTripTime.Ticks;
        if (!HasEstimate)
        {
            smoothedTicks = sampleTicks;
            variationTicks = sampleTicks / 2.0;
            HasEstimate = true;
            AcceptedSampleCount++;
            ResetOutlierCluster();
            return RttSampleResult.Accepted;
        }

        if (AcceptedSampleCount >= options.WarmupSampleCount && IsOutlier(sampleTicks))
        {
            if (AddToConsistentOutlierCluster(sampleTicks))
            {
                smoothedTicks = outlierClusterMean;
                var clusterStandardDeviation = Math.Sqrt(
                    outlierClusterM2 / Math.Max(1, outlierClusterCount));
                variationTicks = Math.Max(TimeSpan.FromMilliseconds(1).Ticks, clusterStandardDeviation);
                AcceptedSampleCount++;
                ResetOutlierCluster();
                return RttSampleResult.AcceptedRebase;
            }

            IgnoredOutlierCount++;
            return RttSampleResult.IgnoredOutlier;
        }

        ResetOutlierCluster();
        var priorSmoothedTicks = smoothedTicks;
        variationTicks = ((1.0 - options.VariationFactor) * variationTicks)
            + (options.VariationFactor * Math.Abs(priorSmoothedTicks - sampleTicks));
        smoothedTicks = ((1.0 - options.SmoothingFactor) * priorSmoothedTicks)
            + (options.SmoothingFactor * sampleTicks);
        AcceptedSampleCount++;
        return RttSampleResult.Accepted;
    }

    public void Reset()
    {
        HasEstimate = false;
        ObservedSampleCount = 0;
        AcceptedSampleCount = 0;
        IgnoredInvalidCount = 0;
        IgnoredOutlierCount = 0;
        smoothedTicks = 0;
        variationTicks = 0;
        ResetOutlierCluster();
    }

    private bool IsOutlier(double sampleTicks)
    {
        var threshold = Math.Max(
            options.OutlierFloor.Ticks,
            options.OutlierVariationMultiplier * variationTicks);
        return Math.Abs(sampleTicks - smoothedTicks) > threshold;
    }

    private bool AddToConsistentOutlierCluster(double sampleTicks)
    {
        if (outlierClusterCount == 0
            || Math.Abs(sampleTicks - outlierClusterMean) <= options.RebaseConsistencyTolerance.Ticks)
        {
            outlierClusterCount++;
            var delta = sampleTicks - outlierClusterMean;
            outlierClusterMean += delta / outlierClusterCount;
            var deltaAfterMean = sampleTicks - outlierClusterMean;
            outlierClusterM2 += delta * deltaAfterMean;
        }
        else
        {
            outlierClusterCount = 1;
            outlierClusterMean = sampleTicks;
            outlierClusterM2 = 0;
        }

        return outlierClusterCount >= options.ConsistentOutliersToRebase;
    }

    private void ResetOutlierCluster()
    {
        outlierClusterCount = 0;
        outlierClusterMean = 0;
        outlierClusterM2 = 0;
    }

    private static TimeSpan FromRoundedTicks(double ticks) =>
        TimeSpan.FromTicks((long)Math.Round(ticks, MidpointRounding.AwayFromZero));

    private static void ValidateOptions(AdaptiveRttOptions options)
    {
        if (options.MinimumSuggestedHold < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.MaximumSuggestedHold <= TimeSpan.Zero
            || options.MinimumSuggestedHold > options.MaximumSuggestedHold)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.MaximumAcceptedSample <= TimeSpan.Zero
            || options.OutlierFloor < TimeSpan.Zero
            || options.RebaseConsistencyTolerance < TimeSpan.Zero
            || options.SafetyMargin < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.WarmupSampleCount < 1 || options.ConsistentOutliersToRebase < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.SmoothingFactor is <= 0 or > 1
            || options.VariationFactor is <= 0 or > 1
            || options.OutlierVariationMultiplier < 0
            || options.SuggestedHoldVariationMultiplier < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }
}

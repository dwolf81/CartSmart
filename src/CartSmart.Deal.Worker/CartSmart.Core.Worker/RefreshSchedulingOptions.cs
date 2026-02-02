namespace CartSmart.Core.Worker;

public sealed class RefreshSchedulingOptions
{
    // How many due candidates to pull relative to the batchSize.
    public int CandidatePoolMultiplier { get; init; } = 10;
    public int CandidatePoolMax { get; init; } = 500;

    // --- Priority scoring weights ---
    public double RecentClicks5mBoost { get; init; } = 100;
    public double BestDealBoost { get; init; } = 80;
    public double PrimaryBoost { get; init; } = 50;

    public double Clicks7dNormalizedMaxBoost { get; init; } = 60;
    public int Clicks7dThreshold { get; init; } = 20;
    public double Clicks7dThresholdBoost { get; init; } = 40;

    // Staleness (minutes since last check) * factor -> score.
    public double StalenessMinutesFactor { get; init; } = 0.05;
    public double VolatileStalenessMultiplier { get; init; } = 2.0;

    public int ErrorCountSmallMax { get; init; } = 2;
    public double ErrorCountSmallBoost { get; init; } = 50;
    public int ErrorCountPenaltyMin { get; init; } = 6;
    public double ErrorCountPenalty { get; init; } = -100;

    public decimal HighPriceThreshold { get; init; } = 500;
    public double HighPriceBoost { get; init; } = 20;

    // --- Tiered next-check scheduling ---
    // Tier A: best deal or active now
    public int TierA_MinMinutes { get; init; } = 5;
    public int TierA_MaxMinutes { get; init; } = 15;
    public int TierA_VolatileMinMinutes { get; init; } = 5;
    public int TierA_VolatileMaxMinutes { get; init; } = 10;
    public int TierA_RiskMinutes { get; init; } = 5;

    // Tier B: other primaries
    public int TierB_MinMinutes { get; init; } = 30;
    public int TierB_MaxMinutes { get; init; } = 90;
    public int TierB_VolatileMinMinutes { get; init; } = 30;
    public int TierB_VolatileMaxMinutes { get; init; } = 60;
    public int TierB_RiskMinutes { get; init; } = 30;

    // Tier C: long tail
    public int TierC_MinHours { get; init; } = 6;
    public int TierC_MaxHours { get; init; } = 24;
    public int TierC_VolatileMinHours { get; init; } = 6;
    public int TierC_VolatileMaxHours { get; init; } = 12;

    // Tier D: dead/noisy
    public int TierD_Days { get; init; } = 7;
}

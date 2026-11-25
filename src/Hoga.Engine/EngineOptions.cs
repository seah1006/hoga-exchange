namespace Hoga.Engine;

public sealed record EngineOptions(bool ViEnabled = true, bool IsKospi200 = false, long RandomSeed = 0);

public enum TradingPhase { Continuous, CallAuction }

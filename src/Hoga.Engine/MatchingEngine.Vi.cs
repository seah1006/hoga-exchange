namespace Hoga.Engine;

public sealed partial class MatchingEngine
{
    private SplitMix64 _rng;
    private ViTriggered? _ongoingVi;
    private long _actualEndMs;

    public long StaticReferencePrice { get; private set; }

    private bool TryTriggerVi(long price, long submittedReference, long time, List<EngineEvent> events)
    {
        if (!_options.ViEnabled)
            return false;
        var dynamicRate = _options.IsKospi200 ? 3 : 6; // R-VI-02
        // R-VI-03: static wins if both conditions hold.
        ViKind kind;
        long reference;
        int rate;
        string rule;
        if (TouchesVi(price, StaticReferencePrice, 10))
            (kind, reference, rate, rule) = (ViKind.Static, StaticReferencePrice, 10, "R-VI-03");
        else if (TouchesVi(price, submittedReference, dynamicRate))
            (kind, reference, rate, rule) = (ViKind.Dynamic, submittedReference, dynamicRate, "R-VI-01");
        else
            return false;

        var scheduledEndMs = checked(time + 120_000);
        var actualEndMs = checked(scheduledEndMs + _rng.NextAuctionExtensionMs()); // R-AUCT-04
        // Display only: floors the theoretical threshold, never used in the trigger test.
        var display = reference * (100 + (price > reference ? rate : -rate)) / 100;
        _ongoingVi = new(kind, reference, display, time, scheduledEndMs, rule);
        _actualEndMs = actualEndMs;
        Phase = TradingPhase.CallAuction;
        events.Add(_ongoingVi);
        return true;
    }

    // R-VI-06: prices are bounded by the scenario's price limits; integer products fit long.
    private static bool TouchesVi(long price, long reference, int rate) =>
        price * 100 >= reference * (100 + rate) || price * 100 <= reference * (100 - rate);

    private void CompleteAuction(long sequence, List<EngineEvent> events)
    {
        var match = CalculateAuction();
        if (match is null)
        {
            events.Add(new AuctionNoMatch(_actualEndMs)); // R-AUCT-07: no extension
        }
        else
        {
            ExecuteAuction(match, sequence, events);
            DynamicReferencePrice = StaticReferencePrice = match.Price; // R-VI-07
            events.Add(new AuctionCompleted(match.Price, match.Quantity, _actualEndMs));
        }
        events.Add(new ViReleased(_actualEndMs));
        // R-VI-05: no per-session counter; continuous trading can immediately trigger again.
        _ongoingVi = null;
        _actualEndMs = 0;
        Phase = TradingPhase.Continuous;
    }
}

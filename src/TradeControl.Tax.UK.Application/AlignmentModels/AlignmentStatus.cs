namespace TradeControl.Tax.UK.Application.Alignment;

public enum AlignmentStatus
{
    Unknown = 0,
    Ready = 1,
    Conflict = 2,
    Mismatch = 3,
    AlreadySubmitted = 4,
    Error = 5
}

using FireGaze.PortraitPreview;

public static class ReservationCollectibleProbe
{
    private static PortraitNativePhaseHostReservation? held;
    public static bool Acquire(nint address)
        => new PortraitNativePhaseHostReservations().TryAcquireBatch([address], out held, out _);
    public static bool Release() => held is not null && held.TryRelease(out _);
}

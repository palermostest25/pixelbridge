namespace PixelBridge.Core;

public enum PhoneReadiness { Booting, FirstUnlock, Ready }
public static class PhoneBoot
{
    public static PhoneReadiness Evaluate(string bootCompleted, string userState)
    {
        if (bootCompleted.Trim() != "1") return PhoneReadiness.Booting;
        var state = userState.Trim();
        return state is "BOOTING" or "RUNNING_LOCKED" or "RUNNING_UNLOCKING" ? PhoneReadiness.FirstUnlock : PhoneReadiness.Ready;
    }
}

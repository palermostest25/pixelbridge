using PixelBridge.Core;

namespace PixelBridge.Tests;

public class HotspotTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UsesAndroidConfirmedState(bool enabled)
    {
        var state = HotspotControl.Parse("{\"ok\":true,\"supported\":true,\"enabled\":" + enabled.ToString().ToLowerInvariant() + "}", true);
        Assert.True(state.Supported); Assert.Equal(enabled, state.Enabled);
    }
    [Fact]
    public void ReportsAndroidFailure() => Assert.Contains("carrier", Assert.Throws<IOException>(() => HotspotControl.Parse("{\"ok\":false,\"error\":\"Check carrier support\"}", false)).Message);
    [Fact]
    public void NeverInventsAnOffState() => Assert.Throws<IOException>(() => HotspotControl.Parse("{\"ok\":true,\"supported\":true}", true));
    [Fact]
    public void UnsupportedAndroidCanOpenSettings() => Assert.False(HotspotControl.Parse("{\"ok\":true,\"supported\":false}", true).Supported);
    [Fact]
    public void MissingHelperHasRecoveryInstruction() => Assert.Contains("Update", Assert.Throws<IOException>(() => HotspotControl.Parse("Class not found", false)).Message);
}

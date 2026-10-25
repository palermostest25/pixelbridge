using PixelBridge.Core;
using PixelBridge.Core.Adb;

namespace PixelBridge.Tests;

public class WirelessTests
{
    [Theory]
    [InlineData("192.168.1.2:5555", true)]
    [InlineData("10.20.0.2:37123", true)]
    [InlineData("172.16.0.2:65535", true)]
    [InlineData("172.31.255.2:1", true)]
    [InlineData("172.32.0.2:5555", false)]
    [InlineData("8.8.8.8:5555", false)]
    [InlineData("127.0.0.1:5555", false)]
    [InlineData("192.168.1.2:0", false)]
    [InlineData("192.168.1.2:65536", false)]
    [InlineData("192.168.1.2:5555;echo hi", false)]
    [InlineData("192.168.1.2", false)]
    [InlineData(":5555", false)]
    [InlineData("-s:5555", false)]
    [InlineData("192.168.1.256:5555", false)]
    public void ValidatePrivateEndpoint(string endpoint, bool valid) => Assert.Equal(valid, WirelessAdb.ValidEndpoint(endpoint));

    [Theory]
    [InlineData("012345", true)]
    [InlineData("12345", false)]
    [InlineData("1234567", false)]
    [InlineData("１２３４５６", false)]
    [InlineData("12 456", false)]
    public void ValidateCode(string code, bool valid) => Assert.Equal(valid, WirelessAdb.ValidCode(code));

    [Fact]
    public void OnlyUseWifiInterface()
    {
        var output = "1: lo inet 127.0.0.1/8 scope host\n2: rmnet0 inet 10.12.0.1/24 scope global\n3: wlan0 inet 192.168.1.20/24 brd 192.168.1.255 scope global wlan0\n";
        Assert.Equal("192.168.1.20", WirelessAdb.WifiAddress(output));
        Assert.Null(WirelessAdb.WifiAddress("2: wlan0 inet 8.8.8.8/24 scope global\n"));
    }

    [Fact]
    public void PreferSavedWifiAndFallbackToOwnUsbOnly()
    {
        var devices = AdbClient.ParseDevices("phone device\n192.168.1.20:5555 device\nother device\n");
        Assert.Equal("192.168.1.20:5555", WirelessAdb.Selection("phone", "192.168.1.20:5555", "phone", devices));
        Assert.Equal("other", WirelessAdb.Selection("other", "192.168.1.20:5555", "phone", devices));
        Assert.Equal("phone", WirelessAdb.Selection("", "192.168.1.20:5555", "phone", devices.Where(d => !d.Serial.Contains(':')).ToArray()));
        Assert.Equal("192.168.1.20:5555", WirelessAdb.Selection("", "192.168.1.20:5555", "phone", devices.Where(d => d.Serial == "other").ToArray()));
        Assert.Equal("", WirelessAdb.Selection("", "invalid", "phone", devices));
    }

    [Fact]
    public void MdnsMatchesOnePhoneAndNeverGuesses()
    {
        const string rows = "List of discovered mdns services\nadb-phone-abc _adb-tls-pairing._tcp 192.168.1.20:34567\nadb-phone-abc _adb-tls-connect._tcp 192.168.1.20:45678\nadb-other-xyz _adb-tls-connect._tcp 192.168.1.21:23456\n";
        var phone = WirelessAdb.Discover(rows, host: "192.168.1.20");
        Assert.Equal("192.168.1.20:45678", phone?.Endpoint);
        Assert.Equal("adb-phone-abc._adb-tls-connect._tcp", phone?.Service);
        Assert.Equal(phone, WirelessAdb.Discover(rows, identity: "phone"));
        Assert.Null(WirelessAdb.Discover(rows));
        Assert.Null(WirelessAdb.Discover(rows, identity: "phon"));
        var devices = AdbClient.ParseDevices("phone device\nadb-phone-abc._adb-tls-connect._tcp device\n");
        Assert.Equal(phone?.Service, WirelessAdb.Selection("phone", phone!.Value.Endpoint, "phone", devices, phone.Value.Service));
    }

    [Fact]
    public async Task UsbSetupRetainsPairingAndTurnsOffListener()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "pb-wireless-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            var adb = Path.Combine(dir, "adb-fixture"); var commands = Path.Combine(dir, "commands"); var enabled = Path.Combine(dir, "enabled"); var discovery = Path.Combine(dir, "discovery");
            await File.WriteAllTextAsync(adb, $"""
                #!/bin/sh
                printf '%s\n' "$*" >> '{commands}'
                if [ "$1" = devices ]; then
                  echo 'phone device'
                  if [ ! -f '{discovery}' ]; then touch '{discovery}'; sleep 0.6; exit 0; fi
                  if [ -f '{enabled}' ]; then echo '192.168.1.20:5555 device'; fi
                  exit 0
                fi
                case "$*" in
                  *'getprop ro.serialno'*) echo phone ;;
                  *'ip -4 -o addr show'*) echo '3: wlan0 inet 192.168.1.20/24 scope global wlan0' ;;
                  *'tcpip 5555'*) touch '{enabled}'; echo restarting ;;
                  *' usb') rm -f '{enabled}'; echo restarting ;;
                  connect*) echo connected ;;
                  disconnect*) echo disconnected ;;
                  *) exit 99 ;;
                esac
                """);
            File.SetUnixFileMode(adb, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var settings = new BridgeSettings { AdbPath = adb, TargetSerial = "phone", AutoConnect = false };
            var pairing = new PairingStore(dir); var key = pairing.CreateAndSave("phone"); Array.Clear(key);
            var originalDate = pairing.PairedUtc("phone"); var saves = 0;
            await using var session = new DeviceSession(settings, pairing, () => saves++); session.Start();
            for (var i = 0; i < 100 && !File.Exists(discovery); i++) await Task.Delay(20);
            Assert.Contains("verified", await session.EnableWirelessAsync());
            await Task.Delay(800); // The stale discovery response must not change the newly selected Wi-Fi transport.
            Assert.Equal("192.168.1.20:5555", session.Serial); Assert.Equal("Wi-Fi", session.TransportLabel);
            Assert.Equal(originalDate, session.PairedUtc); Assert.False(settings.AutoConnect); Assert.False(settings.WirelessSecure);
            Assert.Contains("off", await session.DisableWirelessAsync());
            Assert.Empty(settings.WirelessEndpoint); Assert.False(settings.AutoConnect); Assert.Equal(2, saves);
            var log = await File.ReadAllTextAsync(commands);
            Assert.Contains("-s phone tcpip 5555", log); Assert.Contains("-s phone usb", log);
            Assert.DoesNotContain("kill-server", log); Assert.DoesNotContain("am start", log);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task TlsPairingDiscoversPortAndDoesNotPersistCode()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "pb-wireless-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            var adb = Path.Combine(dir, "adb-fixture");
            await File.WriteAllTextAsync(adb, """
                #!/bin/sh
                case "$*" in
                  pair*) echo 'Successfully paired to 192.168.1.20:34567' ;;
                  'mdns services') echo 'adb-phone-abc _adb-tls-connect._tcp 192.168.1.20:45678' ;;
                  *'getprop ro.serialno'*) echo phone ;;
                  *'getprop service.adb.tls.port'*) echo 45678 ;;
                  connect*) echo connected ;;
                  disconnect*) echo disconnected ;;
                  *) exit 99 ;;
                esac
                """);
            File.SetUnixFileMode(adb, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var settings = new BridgeSettings { AdbPath = adb, AutoConnect = false }; var saves = 0;
            await using var session = new DeviceSession(settings, new PairingStore(dir), () => saves++);
            Assert.Contains("Encrypted", await session.PairWirelessAsync("192.168.1.20:34567", "012345", ""));
            Assert.True(settings.WirelessSecure); Assert.Equal("phone", settings.WirelessSerial);
            Assert.Equal("192.168.1.20:45678", session.WirelessEndpoint); Assert.Equal("phone", settings.TargetSerial);
            Assert.DoesNotContain("012345", System.Text.Json.JsonSerializer.Serialize(settings)); Assert.Equal(1, saves);
            Assert.Contains("removed", await session.DisableWirelessAsync()); Assert.Empty(settings.WirelessEndpoint);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ReusedAddressStopsBeforeAppLaunchOrPairing()
    {
        if (OperatingSystem.IsWindows()) return; // The portable parser/selection tests above run on Windows too.
        var dir = Path.Combine(Path.GetTempPath(), "pb-wireless-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            var adb = Path.Combine(dir, "adb-fixture"); var commands = Path.Combine(dir, "commands");
            await File.WriteAllTextAsync(adb, "#!/bin/sh\nprintf '%s\\n' \"$*\" >> '" + commands + "'\nif [ \"$1\" = devices ]; then echo '192.168.1.20:5555 device'; else echo another-phone; fi\n");
            File.SetUnixFileMode(adb, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var settings = new BridgeSettings { AdbPath = adb, TargetSerial = "phone", WirelessEndpoint = "192.168.1.20:5555", WirelessSerial = "phone" };
            await using var session = new DeviceSession(settings, new PairingStore(dir)); session.Start();
            for (var i = 0; i < 100 && session.State != LinkState.WrongDevice; i++) await Task.Delay(20);
            Assert.Equal(LinkState.WrongDevice, session.State);
            Assert.Contains("does not identify your phone", session.StateDetail);
            var log = await File.ReadAllTextAsync(commands);
            Assert.Contains("getprop ro.serialno", log);
            Assert.DoesNotContain("am start", log); Assert.DoesNotContain("forward", log); Assert.DoesNotContain("tcpip", log);
        }
        finally { Directory.Delete(dir, true); }
    }
}

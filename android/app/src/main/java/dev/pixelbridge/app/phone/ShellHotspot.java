package dev.pixelbridge.app.phone;

import android.content.Context;
import android.net.TetheringInterface;
import android.net.TetheringManager;
import android.net.wifi.SoftApConfiguration;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.Looper;
import org.json.JSONObject;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Field;
import java.util.Set;
import java.util.function.Supplier;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;

/** Explicit hotspot controls using only the already-authorized shell's tethering permission.
 * No permission is granted to the companion. Never changes or exports the hotspot password,
 * never bypasses carrier provisioning, and never starts automatically with the bridge. */
public final class ShellHotspot {
    public static void main(String[] args) {
        TetheringManager manager = null;
        TetheringManager.TetheringEventCallback observer = null;
        int exit = 0;
        try {
            if (android.os.Process.myUid() != 2000) throw new SecurityException("Run hotspot controls through the authorized computer connection.");
            String action = args.length == 1 ? args[0] : "status";
            if (!action.equals("status") && !action.equals("on") && !action.equals("off")) throw new IllegalArgumentException("Unknown hotspot action.");
            if (Build.VERSION.SDK_INT < 36) {
                System.out.println(new JSONObject().put("ok", true).put("supported", false).put("message", "Open Android hotspot settings on this phone."));
                System.exit(0); return;
            }
            Looper.prepareMainLooper();
            Context context = ShellCallAudio.shellContext();
            manager = shellTetheringManager(context);
            AtomicBoolean enabled = new AtomicBoolean();
            CountDownLatch initial = new CountDownLatch(1);
            observer = new TetheringManager.TetheringEventCallback() {
                @Override public void onTetheredInterfacesChanged(Set<TetheringInterface> interfaces) {
                    enabled.set(interfaces.stream().anyMatch(i -> i.getType() == TetheringManager.TETHERING_WIFI));
                    initial.countDown();
                }
            };
            manager.registerTetheringEventCallback(Runnable::run, observer);
            if (!initial.await(8, TimeUnit.SECONDS)) throw new IllegalStateException("Android did not report hotspot status. Open hotspot settings and try again.");
            boolean wanted = action.equals("on");
            if (!action.equals("status") && enabled.get() != wanted) {
                if (wanted) {
                    WifiManager wifi = context.getSystemService(WifiManager.class);
                    SoftApConfiguration config = (SoftApConfiguration) WifiManager.class.getMethod("getSoftApConfiguration").invoke(wifi);
                    int security = config.getSecurityType();
                    if (security != SoftApConfiguration.SECURITY_TYPE_WPA2_PSK && security != SoftApConfiguration.SECURITY_TYPE_WPA3_SAE && security != SoftApConfiguration.SECURITY_TYPE_WPA3_SAE_TRANSITION)
                        throw new IllegalStateException("Set a password-protected hotspot in Android settings first.");
                    CountDownLatch accepted = new CountDownLatch(1);
                    AtomicInteger failure = new AtomicInteger(-1);
                    manager.startTethering(new TetheringManager.TetheringRequest.Builder(TetheringManager.TETHERING_WIFI).build(), Runnable::run,
                        new TetheringManager.StartTetheringCallback() {
                            @Override public void onTetheringStarted() { failure.set(0); accepted.countDown(); }
                            @Override public void onTetheringFailed(int error) { failure.set(error); accepted.countDown(); }
                        });
                    if (!accepted.await(12, TimeUnit.SECONDS)) throw new IllegalStateException("Hotspot request timed out. Check Android for a carrier or permission prompt.");
                    if (failure.get() != 0) throw new IllegalStateException(failure.get() == 14
                        ? "Android did not authorize hotspot control. Open the phone's hotspot settings instead."
                        : "Android could not start the hotspot (error " + failure.get() + "). Check your carrier's hotspot support and Android settings.");
                } else {
                    // The privileged shell may stop an existing hotspot started from Android Settings too.
                    TetheringManager.class.getMethod("stopTethering", int.class).invoke(manager, TetheringManager.TETHERING_WIFI);
                }
                long deadline = System.currentTimeMillis() + 8000;
                while (enabled.get() != wanted && System.currentTimeMillis() < deadline) Thread.sleep(100);
                if (enabled.get() != wanted) throw new IllegalStateException("Android has not confirmed the hotspot change. Check the phone's hotspot settings.");
            }
            System.out.println(new JSONObject().put("ok", true).put("supported", true).put("enabled", enabled.get()));
        } catch (Throwable error) {
            Throwable cause = error instanceof InvocationTargetException && error.getCause() != null ? error.getCause() : error;
            System.out.println("{\"ok\":false,\"error\":" + JSONObject.quote(cause.getMessage() == null ? cause.getClass().getSimpleName() : cause.getMessage()) + "}");
            exit = 1;
        } finally {
            if (manager != null && observer != null) try { manager.unregisterTetheringEventCallback(observer); } catch (Throwable ignored) { }
        }
        System.exit(exit);
    }

    private static TetheringManager shellTetheringManager(Context context) throws Exception {
        TetheringManager service = (TetheringManager) context.getSystemService("tethering");
        if (service == null) throw new UnsupportedOperationException("Android tethering controls are unavailable.");
        // ContextWrapper delegates service lookup to its base. Keep the existing system
        // connector, but attribute requests to our actual shell UID/package, as required
        // by TetheringService. No permission or device configuration is changed.
        Field supplier = TetheringManager.class.getDeclaredField("mConnectorSupplier");
        supplier.setAccessible(true);
        return TetheringManager.class.getConstructor(Context.class, Supplier.class)
            .newInstance(context, supplier.get(service));
    }
}

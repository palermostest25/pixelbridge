package dev.pixelbridge.app.phone;

import android.app.Application;
import android.app.Instrumentation;
import android.content.Context;
import android.content.ContextWrapper;
import android.content.AttributionSource;
import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioRecord;
import android.media.AudioTrack;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.telecom.TelecomManager;
import java.io.*;
import java.lang.reflect.*;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Optional USB call audio, launched ONLY by the user's authorized ADB shell.
 * Uses the shell's existing CALL_AUDIO_INTERCEPTION permission; grants no app permissions,
 * opens no network listener, and never records to disk. Ordinary app UIDs cannot use it.
 * Wire: PBA1 + int32 sample rate + int32 channel count; frames: byte type + int32 length.
 * Type 1 = PCM16LE mono, type 2 = uplink mute byte, type 3 = UTF-8 failure, type 4 = counters.
 */
public final class ShellCallAudio {
    private static final int RATE = 16000, MAX_FRAME = 6400;
    private final AtomicBoolean running = new AtomicBoolean(true);
    private final DataOutputStream out = new DataOutputStream(new BufferedOutputStream(System.out));
    private volatile boolean muted;
    private AudioRecord downlink;
    private AudioTrack uplink;
    private AudioManager audio;
    private boolean ownsAudioMode;
    private volatile long lastMicrophoneFrame = SystemClock.elapsedRealtime();
    private volatile int microphoneRms, microphoneSamples;

    public static void main(String[] args) {
        ShellCallAudio bridge = new ShellCallAudio();
        try {
            Looper.prepareMainLooper();
            Context context = shellContext();
            Constructor<AudioManager> audioConstructor = AudioManager.class.getDeclaredConstructor(Context.class);
            audioConstructor.setAccessible(true);
            AudioManager audio = audioConstructor.newInstance(context);
            bridge.audio = audio;
            boolean supported = (Boolean) AudioManager.class.getMethod("isPstnCallAudioInterceptable").invoke(audio);
            if (args.length > 0 && args[0].equals("--probe")) {
                System.out.println("{\"supported\":" + supported + "}"); System.exit(0);
            }
            bridge.out.writeInt(0x50424131); bridge.out.writeInt(RATE); bridge.out.writeInt(1); bridge.out.flush();
            if (!supported) throw new UnsupportedOperationException("This phone does not support USB cellular call audio");
            long deadline = System.currentTimeMillis() + 8000;
            while (!inCall(audio) && System.currentTimeMillis() < deadline) Thread.sleep(100);
            if (!inCall(audio)) throw new IllegalStateException("No connected call is available for USB audio");
            Constructor<TelecomManager> telecomConstructor = TelecomManager.class.getDeclaredConstructor(Context.class);
            telecomConstructor.setAccessible(true);
            TelecomManager telecom = telecomConstructor.newInstance(context);
            audio.setMode(5); bridge.ownsAudioMode = true; // MODE_CALL_REDIRECT: replace the physical call microphone/speaker.
            if (audio.getMode() != 5) throw new UnsupportedOperationException("The phone did not allow USB call-audio routing");
            AudioFormat input = new AudioFormat.Builder().setSampleRate(RATE).setEncoding(AudioFormat.ENCODING_PCM_16BIT).setChannelMask(AudioFormat.CHANNEL_IN_MONO).build();
            AudioFormat output = new AudioFormat.Builder().setSampleRate(RATE).setEncoding(AudioFormat.ENCODING_PCM_16BIT).setChannelMask(AudioFormat.CHANNEL_OUT_MONO).build();
            bridge.downlink = (AudioRecord) AudioManager.class.getMethod("getCallDownlinkExtractionAudioRecord", AudioFormat.class).invoke(audio, input);
            bridge.uplink = (AudioTrack) AudioManager.class.getMethod("getCallUplinkInjectionAudioTrack", AudioFormat.class).invoke(audio, output);
            bridge.downlink.startRecording(); bridge.uplink.play();
            new Thread(bridge::receiveMicrophone, "pb-call-uplink").start();
            new Thread(bridge::sendConversation, "pb-call-downlink").start();
            Handler handler = new Handler(Looper.getMainLooper());
            handler.postDelayed(new Runnable() {
                @Override public void run() {
                    if (!telecom.isInCall() || !inCall(audio)) bridge.close();
                    else if (SystemClock.elapsedRealtime() - bridge.lastMicrophoneFrame > 5000) { bridge.fail("No microphone audio reached the phone; retry computer audio"); bridge.close(); }
                    else handler.postDelayed(this, 500);
                }
            }, 500);
            Looper.loop();
        } catch (Throwable e) {
            Throwable cause = e instanceof InvocationTargetException && e.getCause() != null ? e.getCause() : e;
            bridge.fail(cause.getMessage() == null ? cause.getClass().getSimpleName() : cause.getMessage());
            bridge.close();
        }
    }

    private static boolean inCall(AudioManager audio) {
        int mode = audio.getMode(); return mode == AudioManager.MODE_IN_CALL || mode == 5; // MODE_CALL_REDIRECT (system API)
    }

    private static Context shellContext() throws Exception {
        // Attribute audio requests to the real, already-authorized shell UID/package.
        Class<?> type = Class.forName("android.app.ActivityThread");
        Object thread = type.getMethod("systemMain").invoke(null);
        Context system = (Context) type.getMethod("getSystemContext").invoke(thread);
        Context base = system.createPackageContext("com.android.shell", Context.CONTEXT_IGNORE_SECURITY);
        Context context = new ContextWrapper(base) {
            @Override public Context getApplicationContext() { return this; }
            @Override public String getPackageName() { return "com.android.shell"; }
            @Override public String getOpPackageName() { return "com.android.shell"; }
            @Override public AttributionSource getAttributionSource() {
                return new AttributionSource.Builder(android.os.Process.myUid()).setPackageName("com.android.shell").build();
            }
        };
        Class<?> bindType = Class.forName("android.app.ActivityThread$AppBindData");
        Constructor<?> constructor = bindType.getDeclaredConstructor(); constructor.setAccessible(true);
        Object bind = constructor.newInstance();
        Field info = bindType.getDeclaredField("appInfo"); info.setAccessible(true); info.set(bind, context.getApplicationInfo());
        Field bound = type.getDeclaredField("mBoundApplication"); bound.setAccessible(true); bound.set(thread, bind);
        Application application = Instrumentation.newApplication(Application.class, context);
        Field initial = type.getDeclaredField("mInitialApplication"); initial.setAccessible(true); initial.set(thread, application);
        return context;
    }

    private void receiveMicrophone() {
        try {
            DataInputStream input = new DataInputStream(new BufferedInputStream(System.in));
            byte[] silence = new byte[MAX_FRAME];
            while (running.get()) {
                int type = input.readUnsignedByte(), length = input.readInt();
                if (length < 0 || length > MAX_FRAME) throw new IOException("Invalid call audio frame");
                byte[] data = new byte[length]; input.readFully(data);
                if (type == 2 && length == 1) { muted = data[0] != 0; continue; }
                if (type != 1 || (length & 1) != 0) throw new IOException("Invalid microphone audio frame");
                lastMicrophoneFrame = SystemClock.elapsedRealtime();
                long energy = 0;
                for (int index = 0; index + 1 < data.length; index += 2) {
                    short sample = (short) ((data[index] & 255) | ((data[index + 1] & 255) << 8));
                    energy += (long) sample * sample;
                }
                microphoneRms = Math.max(microphoneRms, (int) Math.sqrt((double) energy / Math.max(data.length / 2, 1)));
                int offset = 0;
                while (offset < length && running.get()) {
                    int count = uplink.write(muted ? silence : data, offset, length - offset, AudioTrack.WRITE_BLOCKING);
                    if (count <= 0) throw new IOException("The phone microphone route stopped");
                    offset += count;
                    microphoneSamples += count / 2;
                }
            }
        } catch (EOFException ignored) { }
        catch (Throwable e) { if (running.get()) fail("USB microphone route stopped: " + e.getClass().getSimpleName()); }
        finally { close(); }
    }

    private void sendConversation() {
        byte[] buffer = new byte[640];
        int frames = 0;
        try {
            while (running.get()) {
                int count = downlink.read(buffer, 0, buffer.length, AudioRecord.READ_BLOCKING);
                if (count <= 0) throw new IOException("The phone speaker route stopped");
                synchronized (out) {
                    out.writeByte(1); out.writeInt(count); out.write(buffer, 0, count);
                    if (++frames % 25 == 0) {
                        out.writeByte(4); out.writeInt(24);
                        out.writeInt(microphoneRms); out.writeInt(microphoneSamples); out.writeInt(uplink.getPlaybackHeadPosition());
                        out.writeInt(audio.isMicrophoneMute() ? 1 : 0); out.writeInt(audio.getMode());
                        android.media.AudioDeviceInfo device = uplink.getRoutedDevice(); out.writeInt(device == null ? 0 : device.getType());
                    }
                    out.flush();
                }
            }
        } catch (Throwable e) { if (running.get()) fail("USB speaker route stopped: " + e.getClass().getSimpleName()); }
        finally { close(); }
    }

    private void fail(String message) {
        try {
            byte[] text = message.getBytes(java.nio.charset.StandardCharsets.UTF_8);
            synchronized (out) { out.writeByte(3); out.writeInt(Math.min(text.length, MAX_FRAME)); out.write(text, 0, Math.min(text.length, MAX_FRAME)); out.flush(); }
        } catch (IOException ignored) { }
    }
    private void close() {
        if (!running.compareAndSet(true, false)) return;
        try { if (downlink != null) { downlink.stop(); downlink.release(); } } catch (RuntimeException ignored) { }
        try { if (uplink != null) { uplink.stop(); uplink.release(); } } catch (RuntimeException ignored) { }
        try { if (ownsAudioMode && audio != null) audio.setMode(AudioManager.MODE_NORMAL); } catch (RuntimeException ignored) { }
        System.exit(0); // Releases audio policies, the mode listener, and the authorized shell process.
    }
}

package dev.pixelbridge.app.phone;

import java.util.ArrayDeque;

/** Recent live speech only. Reading USB never waits for the telephony playback clock. */
final class CallAudioQueue {
    private static final int MAX_BYTES = 6400; // 200 ms at 16 kHz, mono PCM16.
    private static final long MAX_AGE_NS = 200_000_000L;
    private record Chunk(byte[] pcm, long timeNs) { }
    private final ArrayDeque<Chunk> chunks = new ArrayDeque<>();
    private int bytes;

    synchronized void offer(byte[] pcm, long nowNs) {
        if (pcm.length == 0 || pcm.length > MAX_BYTES || (pcm.length & 1) != 0) throw new IllegalArgumentException("Invalid microphone samples");
        expire(nowNs);
        while (bytes + pcm.length > MAX_BYTES) bytes -= chunks.removeFirst().pcm().length;
        chunks.addLast(new Chunk(pcm, nowNs)); bytes += pcm.length;
        notifyAll();
    }
    synchronized byte[] poll(long nowNs) {
        expire(nowNs);
        if (chunks.isEmpty()) return null;
        byte[] pcm = chunks.removeFirst().pcm(); bytes -= pcm.length; return pcm;
    }
    private void expire(long nowNs) {
        while (!chunks.isEmpty() && nowNs - chunks.peekFirst().timeNs() > MAX_AGE_NS) bytes -= chunks.removeFirst().pcm().length;
    }
    synchronized byte[] take() throws InterruptedException {
        while (true) { byte[] pcm = poll(System.nanoTime()); if (pcm != null) return pcm; wait(); }
    }
}

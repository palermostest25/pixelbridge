package dev.pixelbridge.app.phone

import org.junit.Assert.*
import org.junit.Test

class CallAudioQueueTest {
    @Test fun stalledPlaybackKeepsRecentSpeechInsteadOfASecondOfBacklog() {
        val queue = CallAudioQueue()
        for (index in 0 until 50) queue.offer(ByteArray(640) { index.toByte() }, 0)
        // A one-second USB burst can retain at most the most recent 200 ms.
        for (index in 40 until 50) assertEquals(index.toByte(), queue.poll(0)!![0])
        assertNull(queue.poll(0))
    }
    @Test fun audioThatWaitedTooLongIsDiscardedAfterAStall() {
        val queue = CallAudioQueue()
        queue.offer(byteArrayOf(1, 0), 0)
        queue.offer(byteArrayOf(2, 0), 250_000_000)
        assertArrayEquals(byteArrayOf(2, 0), queue.poll(250_000_000))
        assertNull(queue.poll(250_000_000))
    }
    @Test fun ordinaryCallbacksKeepEverySampleInOrder() {
        val queue = CallAudioQueue()
        for (index in 0 until 100) {
            val time = index * 20_000_000L
            val pcm = ByteArray(640) { index.toByte() }
            queue.offer(pcm, time)
            assertArrayEquals(pcm, queue.poll(time + 10_000_000))
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using DualPCStream.Shared;

namespace DualPCStream.Receiver
{
    /// <summary>
    /// Reassembles VideoPacket UDP fragments back into JPEG frames. Latency is
    /// bounded instead of reliable: a frame still incomplete after ~150ms, or
    /// older than one that already completed, is simply dropped.
    /// </summary>
    public class UdpVideoReceiver : IDisposable
    {
        private const int ReassemblyTimeoutMs = 150;

        private sealed class PendingFrame
        {
            public byte[]?[] Chunks = Array.Empty<byte[]?>();
            public int Received;
            public int TotalBytes;
            public long Timestamp;
            public long FirstSeenMs;
        }

        private readonly UdpClient _socket;
        private readonly Dictionary<uint, PendingFrame> _pending = new();
        private Thread? _thread;
        private Thread? _decodeThread;
        private volatile bool _running;
        private uint _newestCompleted;
        private bool _anyCompleted;

        // Single-slot handoff to the decode thread. JPEG decode takes multiple
        // milliseconds; doing it on the receive thread would stall socket reads
        // mid-burst and lose packets. Latest-wins: if decode can't keep up, we
        // skip straight to the newest frame instead of queueing up latency.
        private byte[]? _latestJpeg;
        private long _latestTimestamp;
        private readonly AutoResetEvent _decodeSignal = new(false);

        /// <summary>Fired on the receive thread - marshal to the UI thread yourself.
        /// The handler owns (and should eventually dispose) the Bitmap.</summary>
        public event Action<Bitmap, long>? FrameReady;

        public long FramesCompleted { get; private set; }
        public long FramesDropped { get; private set; }
        public long PacketsDropped { get; private set; }

        /// <summary>Every datagram that arrived on the port, valid or not -
        /// stays at 0 when nothing is reaching this PC at all (wrong IP on the
        /// sender, or firewall blocking inbound UDP).</summary>
        public long PacketsReceived { get; private set; }

        public UdpVideoReceiver(int listenPort)
        {
            _socket = new UdpClient(listenPort);
            // A whole frame arrives as a ~100-datagram burst; the default 64KB
            // OS buffer overflows mid-burst and the frame can never complete.
            _socket.Client.ReceiveBufferSize = 8 * 1024 * 1024;
        }

        public void Start()
        {
            _running = true;
            _thread = new Thread(ReceiveLoop) { IsBackground = true };
            _thread.Start();
            _decodeThread = new Thread(DecodeLoop) { IsBackground = true };
            _decodeThread.Start();
        }

        private void ReceiveLoop()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                byte[] datagram;
                try { datagram = _socket.Receive(ref any); }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }

                PacketsReceived++;

                // Sender's "Test connection" probe - echo a pong straight back.
                if (Probe.IsPing(datagram))
                {
                    try { _socket.Send(Probe.Pong, Probe.Pong.Length, any); }
                    catch (SocketException) { /* reply is best-effort */ }
                    continue;
                }

                if (datagram.Length <= VideoPacket.HeaderSize) { PacketsDropped++; continue; }
                var (frameId, packetIndex, totalPackets, timestamp) = VideoPacket.ParseHeader(datagram);
                if (totalPackets == 0 || packetIndex >= totalPackets) { PacketsDropped++; continue; }

                // Late packet for a frame we've already displayed (or superseded).
                if (_anyCompleted && (int)(frameId - _newestCompleted) <= 0) { PacketsDropped++; continue; }

                if (!_pending.TryGetValue(frameId, out var frame))
                {
                    frame = new PendingFrame
                    {
                        Chunks = new byte[totalPackets][],
                        Timestamp = timestamp,
                        FirstSeenMs = Environment.TickCount64
                    };
                    _pending[frameId] = frame;
                }

                if (frame.Chunks.Length != totalPackets) { PacketsDropped++; continue; } // corrupted/inconsistent header
                if (frame.Chunks[packetIndex] != null) continue; // duplicate

                int payload = datagram.Length - VideoPacket.HeaderSize;
                var chunk = new byte[payload];
                Buffer.BlockCopy(datagram, VideoPacket.HeaderSize, chunk, 0, payload);
                frame.Chunks[packetIndex] = chunk;
                frame.Received++;
                frame.TotalBytes += payload;

                if (frame.Received == totalPackets) CompleteFrame(frameId, frame);
                PruneStale();
            }
        }

        private void CompleteFrame(uint frameId, PendingFrame frame)
        {
            _pending.Remove(frameId);
            _newestCompleted = frameId;
            _anyCompleted = true;

            // Any frame still pending that's older than this one will never be
            // shown - drop it now rather than waiting out its timeout.
            List<uint>? stale = null;
            foreach (var id in _pending.Keys)
                if ((int)(id - frameId) < 0) (stale ??= new List<uint>()).Add(id);
            if (stale != null)
                foreach (var id in stale) { _pending.Remove(id); FramesDropped++; }

            var jpeg = new byte[frame.TotalBytes];
            int pos = 0;
            foreach (var c in frame.Chunks)
            {
                Buffer.BlockCopy(c!, 0, jpeg, pos, c!.Length);
                pos += c.Length;
            }

            FramesCompleted++;
            _latestTimestamp = frame.Timestamp;
            var superseded = Interlocked.Exchange(ref _latestJpeg, jpeg);
            if (superseded != null) FramesDropped++; // decoder busy - old frame never shown
            _decodeSignal.Set();
        }

        private void DecodeLoop()
        {
            while (_running)
            {
                _decodeSignal.WaitOne(100);
                var jpeg = Interlocked.Exchange(ref _latestJpeg, null);
                if (jpeg == null) continue;

                try
                {
                    using var ms = new MemoryStream(jpeg);
                    using var decoded = new Bitmap(ms);
                    var copy = new Bitmap(decoded); // detach from the stream GDI+ keeps referencing
                    FrameReady?.Invoke(copy, _latestTimestamp);
                }
                catch (Exception)
                {
                    FramesDropped++; // corrupt JPEG (e.g. interleaved fragments) - skip it
                }
            }
        }

        private void PruneStale()
        {
            long now = Environment.TickCount64;
            List<uint>? stale = null;
            foreach (var kv in _pending)
                if (now - kv.Value.FirstSeenMs > ReassemblyTimeoutMs) (stale ??= new List<uint>()).Add(kv.Key);
            if (stale != null)
                foreach (var id in stale) { _pending.Remove(id); FramesDropped++; }
        }

        public void Dispose()
        {
            _running = false;
            _socket.Close();
            _decodeSignal.Set();
            _thread?.Join(500);
            _decodeThread?.Join(500);
            _decodeSignal.Dispose();
        }
    }
}

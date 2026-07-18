using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using DualPCStream.Shared;

namespace DualPCStream.Receiver
{
    /// <summary>
    /// Plays received PCM out through a chosen WASAPI render device.
    /// Pass null for "system default" - what OBS's Desktop Audio source
    /// listens to. Passing a *different* device (e.g. a second virtual
    /// audio cable installed on this PC) is how you keep a stream, like the
    /// mic, independently adjustable in OBS's mixer instead of it being
    /// silently mixed together with everything else at the OS level.
    /// </summary>
    public class UdpAudioReceiver : IDisposable
    {
        private readonly UdpClient _socket;
        private readonly WasapiOut _output;
        private readonly BufferedWaveProvider _buffer;
        private Thread? _thread;
        private volatile bool _running;
        private uint _lastSequence;

        /// <summary>Datagrams that arrived on this port - stays 0 when nothing
        /// is reaching us (wrong sender IP or firewall).</summary>
        public long PacketsReceived { get; private set; }

        public UdpAudioReceiver(int listenPort, WaveFormat format, MMDevice? outputDevice = null)
        {
            _socket = new UdpClient(listenPort);
            _socket.Client.ReceiveBufferSize = 1 * 1024 * 1024; // ride out receive-thread hiccups without dropping
            _buffer = new BufferedWaveProvider(format)
            {
                // Small buffer = low latency. If the network stalls and it
                // fills up, we discard rather than let audio drift trying to "catch up".
                BufferDuration = TimeSpan.FromMilliseconds(300),
                DiscardOnBufferOverflow = true
            };

            _output = outputDevice != null
                ? new WasapiOut(outputDevice, AudioClientShareMode.Shared, true, 30)
                : new WasapiOut(AudioClientShareMode.Shared, 30); // system default render device
            _output.Init(_buffer);
        }

        public void Start()
        {
            _running = true;
            _output.Play();
            _thread = new Thread(ReceiveLoop) { IsBackground = true };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            _socket.Close();
            _output.Stop();
            _thread?.Join(500);
        }

        private void ReceiveLoop()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                byte[] datagram;
                try { datagram = _socket.Receive(ref any); }
                catch (SocketException) { break; }

                PacketsReceived++;
                if (datagram.Length <= AudioPacket.HeaderSize) continue;
                var (seq, _) = AudioPacket.ParseHeader(datagram);

                // Loss tolerance: just play whatever arrives, in the order it
                // arrives. A dropped packet becomes a tiny click, not a crash.
                if (seq < _lastSequence && (_lastSequence - seq) < uint.MaxValue / 2) continue;
                _lastSequence = seq;

                int len = datagram.Length - AudioPacket.HeaderSize;
                _buffer.AddSamples(datagram, AudioPacket.HeaderSize, len);

                // Latency watchdog: anything queued beyond ~150ms is backlog
                // from a stall, and playing it out just delays everything after
                // it forever. Snap back to live - one click beats permanent lag.
                if (_buffer.BufferedDuration.TotalMilliseconds > 150)
                    _buffer.ClearBuffer();
            }
        }

        public void Dispose()
        {
            Stop();
            _output.Dispose();
        }
    }
}

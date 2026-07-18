using System;
using System.Net;
using System.Net.Sockets;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using DualPCStream.Shared;

namespace DualPCStream.Sender
{
    /// <summary>
    /// Captures one WASAPI endpoint (loopback of a render device for desktop
    /// audio, or a capture device for the mic) and fires each buffer straight
    /// out as sequenced UDP chunks. Desktop audio and mic each get their own
    /// instance on their own port - keeping them separate all the way to the
    /// receiving PC is what lets OBS give each its own volume slider.
    /// </summary>
    public class UdpAudioSender : IDisposable
    {
        private readonly UdpClient _socket = new();
        private readonly IPEndPoint _target;
        private readonly IWaveIn _capture;
        private readonly byte[] _datagram = new byte[AudioPacket.MaxDatagram];
        private uint _sequence;

        /// <summary>The capture format - the Receiver must be configured to match this.</summary>
        public WaveFormat WaveFormat => _capture.WaveFormat;

        private UdpAudioSender(string targetIp, int targetPort, IWaveIn capture)
        {
            _target = new IPEndPoint(IPAddress.Parse(targetIp), targetPort);
            _capture = capture;
            _capture.DataAvailable += OnDataAvailable;
        }

        /// <summary>Desktop audio: loopback-capture whatever is playing on a render device.</summary>
        public static UdpAudioSender ForLoopback(string targetIp, int targetPort, MMDevice renderDevice) =>
            new(targetIp, targetPort, new LowLatencyLoopbackCapture(renderDevice));

        /// <summary>Microphone: capture a recording device directly.</summary>
        public static UdpAudioSender ForMicrophone(string targetIp, int targetPort, MMDevice captureDevice) =>
            new(targetIp, targetPort, new WasapiCapture(captureDevice, false, CaptureBufferMs));

        // NAudio's WASAPI capture buffers 100ms by default before each
        // DataAvailable callback - that's 100ms of built-in latency. 25ms
        // means smaller, 4x more frequent chunks instead.
        private const int CaptureBufferMs = 25;

        /// <summary>WasapiLoopbackCapture's public constructor doesn't expose the
        /// buffer length, so replicate its loopback flag on top of WasapiCapture,
        /// which does.</summary>
        private sealed class LowLatencyLoopbackCapture : WasapiCapture
        {
            public LowLatencyLoopbackCapture(MMDevice device) : base(device, false, CaptureBufferMs) { }
            protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
                AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
        }

        public void Start() => _capture.StartRecording();

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            long timestamp = Environment.TickCount64;
            for (int offset = 0; offset < e.BytesRecorded; offset += AudioPacket.MaxPayload)
            {
                int payload = Math.Min(AudioPacket.MaxPayload, e.BytesRecorded - offset);
                AudioPacket.WriteHeader(_datagram, _sequence++, timestamp);
                Buffer.BlockCopy(e.Buffer, offset, _datagram, AudioPacket.HeaderSize, payload);
                try { _socket.Send(_datagram, AudioPacket.HeaderSize + payload, _target); }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }
            }
        }

        public void Dispose()
        {
            try { _capture.StopRecording(); } catch { /* never started / already stopped */ }
            _capture.DataAvailable -= OnDataAvailable;
            _capture.Dispose();
            _socket.Dispose();
        }
    }
}

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using DualPCStream.Shared;

namespace DualPCStream.Sender
{
    /// <summary>
    /// The self-contained CPU pipeline: DXGI capture -> JPEG encode -> the
    /// frame split into ≤1400-byte UDP datagrams with a VideoPacket header.
    /// Fire-and-forget: a lost datagram just means the receiver drops that
    /// one frame.
    /// </summary>
    public class UdpVideoSender : IDisposable
    {
        private readonly UdpClient _socket = new();
        private readonly IPEndPoint _target;
        private readonly IntPtr _windowHandle;
        private readonly DesktopDuplicator _duplicator = new();
        private readonly ImageCodecInfo _jpegCodec =
            ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");

        private Thread? _thread;
        private volatile bool _running;
        private uint _frameId;

        /// <summary>1-100. Lower = less bandwidth, more artifacts. 45-55 is a good LAN-thrifty range.</summary>
        public int JpegQuality { get; set; } = 65;

        /// <summary>Capping at 30 roughly halves both CPU and bandwidth.</summary>
        public int TargetFps { get; set; } = 60;

        public double LastCaptureMs { get; private set; }
        public double LastEncodeMs { get; private set; }
        public double LastSendMs { get; private set; }
        public long FramesSent { get; private set; }
        public long BytesSent { get; private set; }

        public UdpVideoSender(string targetIp, int targetPort, IntPtr targetWindowHandle)
        {
            _target = new IPEndPoint(IPAddress.Parse(targetIp), targetPort);
            _windowHandle = targetWindowHandle;
            // A whole frame (~100+ datagrams) goes out in one burst; a big OS
            // send buffer keeps that burst from blocking or dropping locally.
            _socket.Client.SendBufferSize = 4 * 1024 * 1024;
        }

        public void Start()
        {
            _running = true;
            _thread = new Thread(SendLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        private void SendLoop()
        {
            var sw = new Stopwatch();
            var pacer = Stopwatch.StartNew();
            var frameInterval = TimeSpan.FromSeconds(1.0 / TargetFps);
            var nextDue = pacer.Elapsed;
            using var jpegStream = new MemoryStream();
            using var encParams = new EncoderParameters(1);

            while (_running)
            {
                sw.Restart();
                using var full = _duplicator.CaptureFrame(Math.Max(1, 1000 / TargetFps));
                if (full == null) { Thread.Sleep(1); continue; } // no screen change - nothing to send
                LastCaptureMs = sw.Elapsed.TotalMilliseconds;

                if (pacer.Elapsed < nextDue) continue; // pace to target fps
                nextDue = pacer.Elapsed + frameInterval;

                sw.Restart();
                Bitmap toEncode = full;
                Bitmap? cropped = null;
                if (_windowHandle != IntPtr.Zero)
                {
                    var rect = WindowUtils.GetWindowBounds(_windowHandle);
                    rect.Intersect(new Rectangle(0, 0, full.Width, full.Height));
                    if (rect.Width > 0 && rect.Height > 0)
                    {
                        cropped = full.Clone(rect, full.PixelFormat);
                        toEncode = cropped;
                    }
                }

                jpegStream.SetLength(0);
                encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(JpegQuality, 1, 100));
                toEncode.Save(jpegStream, _jpegCodec, encParams);
                cropped?.Dispose();
                LastEncodeMs = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                if (!SendFrame(jpegStream.GetBuffer(), (int)jpegStream.Length)) break;
                LastSendMs = sw.Elapsed.TotalMilliseconds;
            }
        }

        private bool SendFrame(byte[] jpeg, int length)
        {
            int totalPackets = (length + VideoPacket.MaxPayload - 1) / VideoPacket.MaxPayload;
            if (totalPackets == 0 || totalPackets > ushort.MaxValue) return true; // absurd frame - just skip it

            long timestamp = Environment.TickCount64;
            var datagram = new byte[VideoPacket.MaxDatagram];
            for (int i = 0; i < totalPackets; i++)
            {
                int offset = i * VideoPacket.MaxPayload;
                int payload = Math.Min(VideoPacket.MaxPayload, length - offset);
                VideoPacket.WriteHeader(datagram, _frameId, (ushort)i, (ushort)totalPackets, timestamp);
                Buffer.BlockCopy(jpeg, offset, datagram, VideoPacket.HeaderSize, payload);
                try { _socket.Send(datagram, VideoPacket.HeaderSize + payload, _target); }
                catch (SocketException) { return false; }  // socket closed under us - stop the loop
                catch (ObjectDisposedException) { return false; }
            }
            _frameId++;
            FramesSent++;
            BytesSent += length;
            return true;
        }

        public void Dispose()
        {
            _running = false;
            _thread?.Join(500);
            _socket.Dispose();
            _duplicator.Dispose();
        }
    }
}

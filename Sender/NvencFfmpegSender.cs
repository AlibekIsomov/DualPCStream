using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace DualPCStream.Sender
{
    /// <summary>
    /// Alternative to UdpVideoSender: uses the same DesktopDuplicator for
    /// capture, but instead of JPEG-encoding frames ourselves, streams raw
    /// BGRA bytes into an ffmpeg subprocess over stdin. ffmpeg encodes with
    /// the GPU's NVENC block (a fixed-function encode chip, mostly separate
    /// from the shader cores your game is using) and sends the result as
    /// MPEG-TS/UDP.
    ///
    /// Deliberately video-only: audio (desktop + optional mic) is handled by
    /// UdpAudioSender over its own port(s), same as in JPEG mode. Keeping
    /// audio out of ffmpeg means each audio source stays a fully separate
    /// stream all the way to the receiving PC, which is what lets OBS treat
    /// them as independently-adjustable sources instead of one pre-mixed
    /// track baked in by ffmpeg's amix filter.
    /// </summary>
    public class NvencFfmpegSender : IDisposable
    {
        private readonly DesktopDuplicator _duplicator;
        private readonly IntPtr _targetWindowHandle;
        private readonly int _outWidth, _outHeight;
        private readonly int _fps;
        private Process? _ffmpeg;
        private Thread? _captureThread;
        private volatile bool _running;
        private Bitmap? _canvas; // fixed-size compositing target - rawvideo requires a constant frame size

        public string FfmpegPath { get; set; } = "ffmpeg";
        public string TargetIp { get; }
        public int TargetPort { get; }
        public string Preset { get; set; } = "p1"; // p1 = fastest / lowest latency NVENC preset
        public int BitrateMbps { get; set; } = 15;
        public string? LastFfmpegLogLine { get; private set; }

        public NvencFfmpegSender(string targetIp, int targetPort, IntPtr targetWindowHandle, int fps = 60)
        {
            TargetIp = targetIp;
            TargetPort = targetPort;
            _targetWindowHandle = targetWindowHandle;
            _fps = fps;
            _duplicator = new DesktopDuplicator();

            // Raw video piping needs one fixed frame size for the whole
            // session. We lock it in at Start() time; if a captured window
            // is later resized, we letterbox/scale into this fixed canvas
            // rather than breaking the pipe (see CaptureLoop).
            Rectangle bounds = _targetWindowHandle != IntPtr.Zero
                ? WindowUtils.GetWindowBounds(_targetWindowHandle)
                : new Rectangle(0, 0, _duplicator.Width, _duplicator.Height);

            _outWidth = Evenify(bounds.Width > 0 ? bounds.Width : _duplicator.Width);
            _outHeight = Evenify(bounds.Height > 0 ? bounds.Height : _duplicator.Height);
        }

        // NVENC (and yuv420p generally) requires even width/height.
        private static int Evenify(int n) => n % 2 == 0 ? n : n - 1;

        private string BuildFfmpegArguments()
        {
            // Tiny VBV window (~2 frames) so the encoder can't build up a
            // multi-frame backlog "smoothing" the bitrate - backlog is latency.
            int vbvKbit = Math.Max(BitrateMbps * 1000 * 2 / _fps, 250);
            return
                "-hide_banner -loglevel warning " +
                // Video in: raw BGRA frames on stdin. Fixed size/fps means
                // ffmpeg doesn't have to guess timing - we pace writes ourselves.
                $"-f rawvideo -pix_fmt bgra -s {_outWidth}x{_outHeight} -r {_fps} -i - " +
                // Hardware encode: NVENC, lowest-latency tuning, CBR so the
                // stream has a predictable bitrate instead of VBR spikes.
                // -bf 0 / -rc-lookahead 0 / -delay 0: B-frames, lookahead and
                // NVENC's output queue each hold frames back = added latency.
                $"-c:v h264_nvenc -preset {Preset} -tune ll -zerolatency 1 -rc cbr " +
                $"-b:v {BitrateMbps}M -maxrate {BitrateMbps}M -bufsize {vbvKbit}k " +
                $"-bf 0 -rc-lookahead 0 -delay 0 -g {_fps} -pix_fmt yuv420p " +
                "-an " + // no audio in this stream at all - see class doc
                // The mpegts muxer buffers ~0.7s by default before emitting
                // anything - flush every packet immediately instead.
                "-flush_packets 1 -muxdelay 0 -muxpreload 0 " +
                $"-f mpegts udp://{TargetIp}:{TargetPort}?pkt_size=1316";
        }

        public void Start()
        {
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                Arguments = BuildFfmpegArguments()
            };

            _ffmpeg = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg - check FfmpegPath / that ffmpeg.exe is on PATH.");
            _ffmpeg.ErrorDataReceived += (_, e) => { if (e.Data != null) LastFfmpegLogLine = e.Data; };
            _ffmpeg.BeginErrorReadLine();

            _canvas = new Bitmap(_outWidth, _outHeight, PixelFormat.Format32bppRgb);

            _running = true;
            _captureThread = new Thread(CaptureLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
            _captureThread.Start();
        }

        private void CaptureLoop()
        {
            var stdin = _ffmpeg!.StandardInput.BaseStream;
            var frameInterval = TimeSpan.FromSeconds(1.0 / _fps);
            var pacer = Stopwatch.StartNew();
            var nextDue = pacer.Elapsed + frameInterval;
            var frameBytes = new byte[_outWidth * _outHeight * 4];
            bool haveFrame = false;

            while (_running)
            {
                // Wait for a changed frame, but never past the next send slot:
                // on a static screen duplication yields nothing, and if we sent
                // nothing the TS stream would stall and the player would start
                // buffering. Instead the previous frame is re-sent on schedule,
                // keeping a rock-steady cadence for the decoder.
                double msLeft = (nextDue - pacer.Elapsed).TotalMilliseconds;
                if (msLeft > 0)
                {
                    using (var full = _duplicator.CaptureFrame((int)Math.Ceiling(msLeft)))
                    {
                        if (full != null)
                        {
                            CompositeFrame(full);
                            var data = _canvas!.LockBits(new Rectangle(0, 0, _outWidth, _outHeight), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                            try { Marshal.Copy(data.Scan0, frameBytes, 0, frameBytes.Length); }
                            finally { _canvas!.UnlockBits(data); }
                            haveFrame = true;
                        }
                    }
                    if (pacer.Elapsed < nextDue) continue; // slot not due yet - maybe a newer frame still arrives
                }

                if (!haveFrame) { nextDue = pacer.Elapsed + frameInterval; continue; }

                nextDue += frameInterval;
                if (nextDue < pacer.Elapsed) nextDue = pacer.Elapsed + frameInterval; // fell behind - resync, don't burst

                try { stdin.Write(frameBytes, 0, frameBytes.Length); }
                catch (IOException) { break; } // ffmpeg exited / pipe closed
                catch (ObjectDisposedException) { break; }
            }
        }

        // Composite the captured region onto the fixed-size canvas. For window
        // mode this also handles the window having moved or resized since
        // Start() - GDI scales it to fit rather than us having to restart the
        // ffmpeg pipe.
        private void CompositeFrame(Bitmap full)
        {
            using var g = Graphics.FromImage(_canvas!);
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            if (_targetWindowHandle != IntPtr.Zero)
            {
                var rect = WindowUtils.GetWindowBounds(_targetWindowHandle);
                rect.Intersect(new Rectangle(0, 0, full.Width, full.Height));
                if (rect.Width > 0 && rect.Height > 0)
                    g.DrawImage(full, new Rectangle(0, 0, _outWidth, _outHeight), rect, GraphicsUnit.Pixel);
            }
            else
            {
                g.DrawImage(full, new Rectangle(0, 0, _outWidth, _outHeight));
            }
        }

        public void Stop()
        {
            _running = false;
            _captureThread?.Join(500);
            try { _ffmpeg?.StandardInput.BaseStream.Close(); } catch { /* already gone */ }
            if (_ffmpeg != null && !_ffmpeg.HasExited)
            {
                _ffmpeg.WaitForExit(1000);
                if (!_ffmpeg.HasExited) _ffmpeg.Kill();
            }
        }

        public void Dispose()
        {
            Stop();
            _duplicator.Dispose();
            _canvas?.Dispose();
            _ffmpeg?.Dispose();
        }
    }
}

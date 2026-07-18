using System;
using System.Drawing;
using System.Drawing.Imaging;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using D3D11Device = SharpDX.Direct3D11.Device;
using DXGIResource = SharpDX.DXGI.Resource;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace DualPCStream.Sender
{
    /// <summary>
    /// DXGI Desktop Duplication capture of the primary monitor. Very cheap:
    /// the OS only hands us a frame when something on screen actually changed,
    /// so a static screen costs essentially nothing.
    /// </summary>
    public sealed class DesktopDuplicator : IDisposable
    {
        private readonly D3D11Device _device;
        private readonly Texture2D _staging;
        private OutputDuplication? _duplication;

        public int Width { get; }
        public int Height { get; }

        public DesktopDuplicator()
        {
            using var factory = new Factory1();
            using var adapter = factory.GetAdapter1(0);
            _device = new D3D11Device(adapter);

            using var output = adapter.GetOutput(0);
            var bounds = output.Description.DesktopBounds;
            Width = bounds.Right - bounds.Left;
            Height = bounds.Bottom - bounds.Top;

            using var output1 = output.QueryInterface<Output1>();
            _duplication = output1.DuplicateOutput(_device);

            _staging = new Texture2D(_device, new Texture2DDescription
            {
                Width = Width,
                Height = Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CpuAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None,
                OptionFlags = ResourceOptionFlags.None
            });
        }

        /// <summary>
        /// Returns the next changed desktop frame as a 32bpp Bitmap, or null
        /// if nothing changed within the timeout (or duplication access was
        /// momentarily lost, e.g. a fullscreen-exclusive transition).
        /// The caller owns (and must dispose) the returned Bitmap.
        /// </summary>
        public Bitmap? CaptureFrame(int timeoutMs)
        {
            if (_duplication == null && !TryRecreateDuplication())
                return null;

            DXGIResource? screenResource = null;
            var result = _duplication!.TryAcquireNextFrame(timeoutMs, out _, out screenResource);
            if (result.Failure)
            {
                screenResource?.Dispose();
                if (result != SharpDX.DXGI.ResultCode.WaitTimeout)
                {
                    // Access lost (fullscreen switch, resolution change, secure
                    // desktop...) - drop and lazily recreate on the next call.
                    _duplication.Dispose();
                    _duplication = null;
                }
                return null;
            }

            try
            {
                using var tex = screenResource!.QueryInterface<Texture2D>();
                _device.ImmediateContext.CopyResource(tex, _staging);
            }
            finally
            {
                screenResource!.Dispose();
                _duplication.ReleaseFrame();
            }

            var map = _device.ImmediateContext.MapSubresource(_staging, 0, MapMode.Read, MapFlags.None);
            try
            {
                var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppRgb);
                var data = bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    // Row-by-row: the GPU staging texture's row pitch is often
                    // wider than the bitmap stride.
                    for (int y = 0; y < Height; y++)
                        SharpDX.Utilities.CopyMemory(data.Scan0 + y * data.Stride, map.DataPointer + y * map.RowPitch, Width * 4);
                }
                finally { bmp.UnlockBits(data); }
                return bmp;
            }
            finally
            {
                _device.ImmediateContext.UnmapSubresource(_staging, 0);
            }
        }

        private bool TryRecreateDuplication()
        {
            try
            {
                using var dxgiDevice = _device.QueryInterface<SharpDX.DXGI.Device>();
                using var adapter = dxgiDevice.Adapter;
                using var output = adapter.GetOutput(0);
                using var output1 = output.QueryInterface<Output1>();
                _duplication = output1.DuplicateOutput(_device);
                return true;
            }
            catch (SharpDX.SharpDXException)
            {
                return false; // still unavailable - caller just gets null frames for now
            }
        }

        public void Dispose()
        {
            _duplication?.Dispose();
            _staging.Dispose();
            _device.Dispose();
        }
    }
}

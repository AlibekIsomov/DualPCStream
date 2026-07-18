using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DualPCStream.Receiver
{
    public class MainForm : Form
    {
        private readonly PictureBox _video = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
        private readonly NumericUpDown _videoPort = new() { Minimum = 1024, Maximum = 65535, Value = 5000 };

        private readonly NumericUpDown _audioPort = new() { Minimum = 1024, Maximum = 65535, Value = 5001 };
        private readonly ComboBox _audioOutputCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };

        private readonly CheckBox _micEnabled = new() { Text = "Also receive mic", AutoSize = true, ForeColor = Color.White };
        private readonly NumericUpDown _micPort = new() { Minimum = 1024, Maximum = 65535, Value = 5002, Enabled = false };
        private readonly ComboBox _micFormat = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, Enabled = false };
        private readonly ComboBox _micOutputCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, Enabled = false };

        private readonly Button _startStop = new() { Text = "Start Receiver", Width = 140 };
        private readonly CheckBox _borderless = new() { Text = "Borderless (for OBS Window Capture)", AutoSize = true, ForeColor = Color.White };
        private readonly Label _stats = new() { AutoSize = true, ForeColor = Color.Lime, Dock = DockStyle.Bottom };

        private readonly List<MMDevice> _outputDevices = new(); // index 0 is always "System default"

        private UdpVideoReceiver? _videoReceiver;
        private UdpAudioReceiver? _audioReceiver;
        private UdpAudioReceiver? _micReceiver;
        private System.Windows.Forms.Timer? _statsTimer;
        private Bitmap? _pendingFrame; // latest-wins handoff from receive thread to UI

        public MainForm()
        {
            Text = "DualPC Stream - Receiver";
            Width = 980; Height = 640;
            BackColor = Color.Black;

            // AutoSize so the panel grows to fit however many rows the controls
            // wrap into - a fixed height clips them behind the video area.
            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.FromArgb(30, 30, 30),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(4)
            };
            // Row 1: ports + desktop audio routing
            AddLabeled(top, "Video port:", _videoPort);
            AddLabeled(top, "Audio port:", _audioPort);
            AddLabeled(top, "Audio out:", _audioOutputCombo);
            top.SetFlowBreak(_audioOutputCombo, true);
            // Row 2: everything mic-related
            top.Controls.Add(_micEnabled);
            _micEnabled.Margin = new Padding(6, 12, 6, 0);
            AddLabeled(top, "Mic port:", _micPort);
            AddLabeled(top, "Mic fmt:", _micFormat);
            AddLabeled(top, "Mic out:", _micOutputCombo);
            top.SetFlowBreak(_micOutputCombo, true);
            // Row 3: window options + start
            top.Controls.Add(_borderless);
            _borderless.Margin = new Padding(6, 12, 6, 0);
            top.Controls.Add(_startStop);

            _micFormat.Items.Add(new NamedFormat("48000Hz Mono 16-bit PCM", WaveFormat.CreateCustomFormat(WaveFormatEncoding.Pcm, 48000, 1, 96000, 2, 16)));
            _micFormat.Items.Add(new NamedFormat("48000Hz Stereo 16-bit PCM", new WaveFormat(48000, 16, 2)));
            _micFormat.Items.Add(new NamedFormat("44100Hz Mono 16-bit PCM", WaveFormat.CreateCustomFormat(WaveFormatEncoding.Pcm, 44100, 1, 88200, 2, 16)));
            _micFormat.Items.Add(new NamedFormat("48000Hz Mono 32-bit float", WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)));
            _micFormat.Items.Add(new NamedFormat("48000Hz Stereo 32-bit float", WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
            _micFormat.SelectedIndex = 0;

            Controls.Add(_video);
            Controls.Add(_stats);
            Controls.Add(top);

            PopulateOutputDevices();

            _startStop.Click += (_, __) => Toggle();
            _micEnabled.CheckedChanged += (_, __) => _micPort.Enabled = _micFormat.Enabled = _micOutputCombo.Enabled = _micEnabled.Checked;
            _borderless.CheckedChanged += (_, __) =>
                FormBorderStyle = _borderless.Checked ? FormBorderStyle.None : FormBorderStyle.Sizable;
            FormClosing += (_, __) => { _videoReceiver?.Dispose(); _audioReceiver?.Dispose(); _micReceiver?.Dispose(); };
        }

        private static void AddLabeled(FlowLayoutPanel panel, string text, Control control)
        {
            panel.Controls.Add(new Label { Text = text, ForeColor = Color.White, AutoSize = true, Padding = new Padding(6, 10, 2, 0) });
            panel.Controls.Add(control);
        }

        private void PopulateOutputDevices()
        {
            _audioOutputCombo.Items.Add("System default");
            _micOutputCombo.Items.Add("System default");
            _outputDevices.Add(null!); // placeholder for "default" at index 0

            using var enumerator = new MMDeviceEnumerator();
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                _outputDevices.Add(dev);
                _audioOutputCombo.Items.Add(dev.FriendlyName);
                _micOutputCombo.Items.Add(dev.FriendlyName);
            }
            _audioOutputCombo.SelectedIndex = 0;
            _micOutputCombo.SelectedIndex = 0;

            // If you want mic to be independently adjustable in OBS's mixer,
            // pick a DIFFERENT device here than for "Audio out" - e.g. install
            // a free virtual audio cable (VB-CABLE) and route the mic there,
            // then add it in OBS as its own "Audio Input Capture" source.
        }

        private void Toggle()
        {
            if (_videoReceiver == null)
            {
                try
                {
                    StartReceivers();
                }
                catch (Exception ex)
                {
                    _videoReceiver?.Dispose(); _videoReceiver = null;
                    _audioReceiver?.Dispose(); _audioReceiver = null;
                    _micReceiver?.Dispose(); _micReceiver = null;
                    MessageBox.Show(
                        $"Failed to start receiver:\n{ex.Message}\n\n" +
                        "If a port is already in use (e.g. an OBS Media Source is bound to it),\n" +
                        "change the port number here - in NVENC mode the video port can be\n" +
                        "anything unused, since video goes straight to OBS.",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                _statsTimer = new System.Windows.Forms.Timer { Interval = 500 };
                _statsTimer.Tick += (_, __) => _stats.Text = _videoReceiver!.PacketsReceived == 0
                    ? $"  LISTENING on UDP port {(int)_videoPort.Value} — no packets yet. Check the Sender's target IP, and allow this app through Windows Firewall (Private network) on this PC."
                    : $"  packets: {_videoReceiver.PacketsReceived}   frames: {_videoReceiver.FramesCompleted}   dropped: {_videoReceiver.FramesDropped}   stray/late: {_videoReceiver.PacketsDropped}";
                _statsTimer.Start();

                _startStop.Text = "Stop Receiver";
            }
            else
            {
                _statsTimer?.Stop();
                _videoReceiver.Dispose();
                _audioReceiver?.Dispose();
                _micReceiver?.Dispose();
                _videoReceiver = null;
                _audioReceiver = null;
                _micReceiver = null;
                _stats.Text = "";
                _startStop.Text = "Start Receiver";
            }
        }

        private void StartReceivers()
        {
            _videoReceiver = new UdpVideoReceiver((int)_videoPort.Value);
                _videoReceiver.FrameReady += (bmp, ts) =>
                {
                    if (IsDisposed) { bmp.Dispose(); return; }
                    // Latest-wins: if the UI thread hasn't painted the previous
                    // frame yet, replace it instead of queueing behind it -
                    // a BeginInvoke backlog is invisible but very real latency.
                    var superseded = Interlocked.Exchange(ref _pendingFrame, bmp);
                    superseded?.Dispose();
                    if (superseded == null)
                    {
                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                var next = Interlocked.Exchange(ref _pendingFrame, null);
                                if (next == null) return;
                                var old = _video.Image;
                                _video.Image = next;
                                old?.Dispose();
                            }));
                        }
                        catch (InvalidOperationException) { /* form closed mid-flight */ }
                    }
                };
                _videoReceiver.Start();

                // MUST MATCH the sender's WASAPI desktop-audio mix format,
                // shown in the Sender's "Audio format" popup when you hit
                // Start there. 48kHz/32-bit float/stereo is the common
                // Windows default - change this if yours differs.
                var desktopFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
                var desktopDevice = _outputDevices[_audioOutputCombo.SelectedIndex]; // null = system default
                _audioReceiver = new UdpAudioReceiver((int)_audioPort.Value, desktopFormat, desktopDevice);
                _audioReceiver.Start();

            if (_micEnabled.Checked)
            {
                var micFormat = ((NamedFormat)_micFormat.SelectedItem!).Format;
                var micDevice = _outputDevices[_micOutputCombo.SelectedIndex];
                _micReceiver = new UdpAudioReceiver((int)_micPort.Value, micFormat, micDevice);
                _micReceiver.Start();
            }
        }
    }

    /// <summary>Wraps a WaveFormat with a human-readable label for the mic-format combo box.</summary>
    internal readonly struct NamedFormat
    {
        public readonly string Label;
        public readonly WaveFormat Format;
        public NamedFormat(string label, WaveFormat format) { Label = label; Format = format; }
        public override string ToString() => Label;
    }
}

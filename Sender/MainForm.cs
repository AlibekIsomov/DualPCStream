using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using DualPCStream.Shared;

namespace DualPCStream.Sender
{
    public class MainForm : Form
    {
        private readonly ComboBox _windowCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        private readonly TextBox _ipBox = new() { Text = "127.0.0.1", Width = 120 };
        private readonly Button _testBtn = new() { Text = "Test", Width = 50 };

        private readonly RadioButton _modeJpeg = new() { Text = "Custom UDP (JPEG, CPU)", Checked = true, AutoSize = true };
        private readonly RadioButton _modeNvenc = new() { Text = "NVENC via ffmpeg (GPU)", AutoSize = true };
        private readonly TextBox _ffmpegPathBox = new() { Text = "ffmpeg", Width = 160 };
        private readonly Button _browseFfmpeg = new() { Text = "...", Width = 28 };
        private readonly NumericUpDown _bitrateMbps = new() { Minimum = 1, Maximum = 100, Value = 15 };
        private readonly ComboBox _presetCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60 };

        // Audio is intentionally independent of the video encoder choice -
        // both modes use these same senders, so desktop audio and mic always
        // arrive as separate streams the receiver can route independently.
        private readonly ComboBox _audioCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        private readonly CheckBox _micEnabled = new() { Text = "Also send microphone separately", AutoSize = true };
        private readonly ComboBox _micCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, Enabled = false };
        private readonly NumericUpDown _videoPort = new() { Minimum = 1024, Maximum = 65535, Value = 5000 };
        private readonly NumericUpDown _audioPort = new() { Minimum = 1024, Maximum = 65535, Value = 5001 };
        private readonly NumericUpDown _micPort = new() { Minimum = 1024, Maximum = 65535, Value = 5002, Enabled = false };

        private readonly Button _startStop = new() { Text = "Start Transmission", Width = 160, Height = 32 };
        private readonly Label _stats = new() { AutoSize = true, Text = "Idle" };
        private readonly Panel _jpegPanel = new() { AutoSize = true, Location = new System.Drawing.Point(0, 0) };
        private readonly Panel _nvencPanel = new() { AutoSize = true, Visible = false, Location = new System.Drawing.Point(0, 0) };

        private readonly List<(string title, IntPtr handle)> _windows = new();
        private readonly List<MMDevice> _audioDevices = new();
        private readonly List<MMDevice> _micDevices = new();

        private UdpVideoSender? _videoSender;
        private NvencFfmpegSender? _nvencSender;
        private UdpAudioSender? _audioSender;
        private UdpAudioSender? _micSender;
        private System.Windows.Forms.Timer? _statsTimer;

        public MainForm()
        {
            Text = "DualPC Stream - Sender";
            Width = 500; Height = 480;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10) };
            root.Controls.Add(new Label { Text = "Capture target:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, 0);
            root.Controls.Add(_windowCombo, 1, 0);
            root.Controls.Add(new Label { Text = "Target IP:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, 1);
            var ipRow = new FlowLayoutPanel { AutoSize = true };
            ipRow.Controls.Add(_ipBox);
            ipRow.Controls.Add(_testBtn);
            root.Controls.Add(ipRow, 1, 1);

            var modePanel = new FlowLayoutPanel { AutoSize = true };
            modePanel.Controls.Add(_modeJpeg);
            modePanel.Controls.Add(_modeNvenc);
            root.Controls.Add(new Label { Text = "Video encoder:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, 2);
            root.Controls.Add(modePanel, 1, 2);

            BuildJpegPanel();
            BuildNvencPanel();
            var modeHost = new Panel { AutoSize = true, Height = 70 };
            modeHost.Controls.Add(_jpegPanel);
            modeHost.Controls.Add(_nvencPanel);
            root.Controls.Add(modeHost, 1, 3);

            BuildAudioSection(root);

            root.Controls.Add(_startStop, 1, 7);
            root.Controls.Add(_stats, 1, 8);
            Controls.Add(root);

            PopulateWindows();
            PopulateAudioDevices();

            _modeJpeg.CheckedChanged += (_, __) => { _jpegPanel.Visible = _modeJpeg.Checked; _nvencPanel.Visible = !_modeJpeg.Checked; };
            _micEnabled.CheckedChanged += (_, __) => _micCombo.Enabled = _micPort.Enabled = _micEnabled.Checked;
            _browseFfmpeg.Click += (_, __) => BrowseForFfmpeg();
            _testBtn.Click += (_, __) => TestConnection();
            _startStop.Click += (_, __) => ToggleTransmission();
            FormClosing += (_, __) =>
            {
                _videoSender?.Dispose(); _nvencSender?.Dispose();
                _audioSender?.Dispose(); _micSender?.Dispose();
            };
        }

        private void BuildJpegPanel()
        {
            _jpegPanel.Controls.Add(new Label
            {
                Text = "JPEG-encodes each frame on the CPU. No extra setup needed.",
                AutoSize = true, ForeColor = System.Drawing.Color.DimGray
            });
        }

        private void BuildNvencPanel()
        {
            _presetCombo.Items.AddRange(new object[] { "p1", "p2", "p3", "p4", "p5", "p6", "p7" });
            _presetCombo.SelectedItem = "p1"; // fastest / lowest latency

            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 };

            var ffmpegRow = new FlowLayoutPanel { AutoSize = true };
            ffmpegRow.Controls.Add(_ffmpegPathBox);
            ffmpegRow.Controls.Add(_browseFfmpeg);
            t.Controls.Add(new Label { Text = "ffmpeg.exe:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, 0);
            t.Controls.Add(ffmpegRow, 1, 0);

            var encRow = new FlowLayoutPanel { AutoSize = true };
            encRow.Controls.Add(new Label { Text = "Bitrate (Mbps):", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
            encRow.Controls.Add(_bitrateMbps);
            encRow.Controls.Add(new Label { Text = "Preset:", AutoSize = true, Padding = new Padding(10, 6, 4, 0) });
            encRow.Controls.Add(_presetCombo);
            t.Controls.Add(new Label { Text = "Encode:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, 1);
            t.Controls.Add(encRow, 1, 1);

            _nvencPanel.Controls.Add(t);
        }

        private void BuildAudioSection(TableLayoutPanel root)
        {
            root.Controls.Add(new Label { Text = "Desktop audio:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, 4);
            root.Controls.Add(_audioCombo, 1, 4);

            var micRow = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 };
            micRow.Controls.Add(_micEnabled, 1, 0);
            micRow.Controls.Add(new Label { Text = "Microphone:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, 1);
            micRow.Controls.Add(_micCombo, 1, 1);
            root.Controls.Add(micRow, 1, 5);

            var portPanel = new FlowLayoutPanel { AutoSize = true };
            portPanel.Controls.Add(new Label { Text = "Video:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
            portPanel.Controls.Add(_videoPort);
            portPanel.Controls.Add(new Label { Text = "Desktop audio:", AutoSize = true, Padding = new Padding(10, 6, 4, 0) });
            portPanel.Controls.Add(_audioPort);
            portPanel.Controls.Add(new Label { Text = "Mic:", AutoSize = true, Padding = new Padding(10, 6, 4, 0) });
            portPanel.Controls.Add(_micPort);
            root.Controls.Add(new Label { Text = "Ports:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, 6);
            // NOTE: ports row placed via the label above; port controls themselves
            // go directly beneath the Start button row for layout simplicity.
            root.Controls.Add(portPanel, 1, 6);
        }

        /// <summary>
        /// Fires a 4-byte probe at the Receiver's video port and waits for its
        /// echo, so the user can verify IP/port/firewall BEFORE streaming
        /// instead of staring at a black window wondering which side is broken.
        /// </summary>
        private void TestConnection()
        {
            string ip = _ipBox.Text.Trim();
            int port = (int)_videoPort.Value;
            _testBtn.Enabled = false;
            try
            {
                using var udp = new UdpClient();
                udp.Client.ReceiveTimeout = 2000;
                udp.Send(Probe.Ping, Probe.Ping.Length, ip, port);

                var from = new IPEndPoint(IPAddress.Any, 0);
                var reply = udp.Receive(ref from); // throws SocketException on timeout

                if (Probe.IsPong(reply))
                    MessageBox.Show(
                        $"SUCCESS - Receiver is listening at {ip}:{port}.\nYou can start the transmission.",
                        "Connection test", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show(
                        $"Something at {ip}:{port} replied, but it doesn't look like the DualPC Receiver.",
                        "Connection test", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (SocketException)
            {
                MessageBox.Show(
                    $"No reply from {ip}:{port}.\n\nCheck, in order:\n" +
                    "1. Receiver app is running on the other PC and 'Start Receiver' was clicked\n" +
                    $"2. The IP is right - run 'ipconfig' on the Receiver PC (IPv4 address)\n" +
                    $"3. Video port matches on both sides (currently {port})\n" +
                    "4. Windows Firewall on the RECEIVER PC allows Receiver.exe on Private networks\n" +
                    "   (Settings > Windows Security > Firewall > Allow an app through firewall)\n" +
                    "5. Both PCs are on the same LAN and can ping each other",
                    "Connection test", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex) // bad IP format etc.
            {
                MessageBox.Show($"Test failed: {ex.Message}", "Connection test",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _testBtn.Enabled = true;
            }
        }

        private void BrowseForFfmpeg()
        {
            using var dlg = new OpenFileDialog { Filter = "ffmpeg.exe|ffmpeg.exe|All files|*.*" };
            if (dlg.ShowDialog() == DialogResult.OK) _ffmpegPathBox.Text = dlg.FileName;
        }

        private void PopulateWindows()
        {
            _windowCombo.Items.Add("Full Screen (primary monitor)");
            _windows.Add(("__FULLSCREEN__", IntPtr.Zero));

            NativeMethods.EnumWindows((hWnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hWnd)) return true;
                var sb = new StringBuilder(256);
                NativeMethods.GetWindowText(hWnd, sb, 256);
                var title = sb.ToString();
                if (string.IsNullOrWhiteSpace(title)) return true;
                _windows.Add((title, hWnd));
                _windowCombo.Items.Add(title);
                return true;
            }, IntPtr.Zero);

            _windowCombo.SelectedIndex = 0;
        }

        private void PopulateAudioDevices()
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                _audioDevices.Add(dev);
                _audioCombo.Items.Add(dev.FriendlyName); // look for your Voicemeeter output here
            }
            if (_audioCombo.Items.Count > 0) _audioCombo.SelectedIndex = 0;

            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                _micDevices.Add(dev);
                _micCombo.Items.Add(dev.FriendlyName);
            }
            if (_micCombo.Items.Count > 0) _micCombo.SelectedIndex = 0;
        }

        private void ToggleTransmission()
        {
            bool starting = _videoSender == null && _nvencSender == null;

            if (starting)
            {
                if (_audioCombo.SelectedIndex < 0)
                {
                    MessageBox.Show("No active playback device available for desktop-audio capture.",
                        "Audio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (_micEnabled.Checked && _micCombo.SelectedIndex < 0)
                {
                    MessageBox.Show("Mic sending is enabled but no recording device is available.",
                        "Audio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string formatInfo;
                try
                {
                    var (title, handle) = _windows[_windowCombo.SelectedIndex];
                    string ip = _ipBox.Text.Trim();

                    // --- Video: whichever encoder is selected ---
                    if (_modeJpeg.Checked)
                    {
                        _videoSender = new UdpVideoSender(ip, (int)_videoPort.Value, handle);
                        _videoSender.Start();
                    }
                    else
                    {
                        _nvencSender = new NvencFfmpegSender(ip, (int)_videoPort.Value, handle, fps: 60)
                        {
                            FfmpegPath = _ffmpegPathBox.Text,
                            BitrateMbps = (int)_bitrateMbps.Value,
                            Preset = (string)_presetCombo.SelectedItem!
                        };
                        _nvencSender.Start();
                    }

                    // --- Audio: always our own senders, regardless of encoder ---
                    var device = _audioDevices[_audioCombo.SelectedIndex];
                    _audioSender = UdpAudioSender.ForLoopback(ip, (int)_audioPort.Value, device);
                    _audioSender.Start();

                    formatInfo = $"Desktop audio format: {_audioSender.WaveFormat}";
                    if (_micEnabled.Checked)
                    {
                        var micDevice = _micDevices[_micCombo.SelectedIndex];
                        _micSender = UdpAudioSender.ForMicrophone(ip, (int)_micPort.Value, micDevice);
                        _micSender.Start();
                        formatInfo += $"\nMic audio format: {_micSender.WaveFormat}";
                    }
                }
                catch (Exception ex)
                {
                    // Bad IP, missing ffmpeg.exe, capture init failure... - clean
                    // up whatever did start instead of crashing half-transmitting.
                    _videoSender?.Dispose(); _videoSender = null;
                    _nvencSender?.Dispose(); _nvencSender = null;
                    _audioSender?.Dispose(); _audioSender = null;
                    _micSender?.Dispose(); _micSender = null;
                    MessageBox.Show($"Failed to start transmission:\n{ex.Message}",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                MessageBox.Show(
                    $"{formatInfo}\nMake sure the Receiver's audio format(s) match this.\n\n" +
                    "Tip: on the Receiver, route desktop audio and mic to DIFFERENT " +
                    "output devices if you want independent volume sliders for each in OBS.",
                    "Audio format", MessageBoxButtons.OK, MessageBoxIcon.Information);

                _statsTimer = new System.Windows.Forms.Timer { Interval = 500 };
                _statsTimer.Tick += (_, __) => _stats.Text = _modeJpeg.Checked
                    ? (_videoSender!.FramesSent == 0
                        ? "capturing... no frames sent yet"
                        : $"sent {_videoSender.FramesSent} frames ({_videoSender.BytesSent / 1048576.0:F1} MB)  |  capture {_videoSender.LastCaptureMs:F1}ms  encode {_videoSender.LastEncodeMs:F1}ms  send {_videoSender.LastSendMs:F1}ms")
                    : (_nvencSender!.LastFfmpegLogLine ?? "streaming...");
                _statsTimer.Start();

                _startStop.Text = "Stop Transmission";
                SetControlsEnabled(false);
            }
            else
            {
                _statsTimer?.Stop();
                _videoSender?.Dispose(); _videoSender = null;
                _nvencSender?.Dispose(); _nvencSender = null;
                _audioSender?.Dispose(); _audioSender = null;
                _micSender?.Dispose(); _micSender = null;
                _stats.Text = "Idle";
                _startStop.Text = "Start Transmission";
                SetControlsEnabled(true);
            }
        }

        private void SetControlsEnabled(bool enabled)
        {
            _windowCombo.Enabled = _audioCombo.Enabled = _micEnabled.Enabled =
                _modeJpeg.Enabled = _modeNvenc.Enabled = enabled;
        }
    }

    internal static class NativeMethods
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    }
}

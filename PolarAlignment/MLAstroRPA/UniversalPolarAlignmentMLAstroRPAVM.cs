using NINA.Core.Utility;
using System;
using System.ComponentModel;
using System.IO.Ports;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NINA.Profile.Interfaces;

namespace NINA.Plugins.PolarAlignment.MLAstroRPA
{
    public partial class UniversalPolarAlignmentMLAstroRPAVM : UniversalPolarAlignmentBaseVM, INotifyPropertyChanged
    {
        private string testConnectStatus;
        public override string TestConnectStatus
        {
            get => testConnectStatus;
            protected set { testConnectStatus = value; OnPropertyChanged(nameof(TestConnectStatus)); }
        }

        // The TestConnectCommand type is dictated by IPolarAlignmentSystemVM /
        // UniversalPolarAlignmentBaseVM, which still expose the obsolete NINA.Core.Utility.RelayCommand.
#pragma warning disable CS0618
        private readonly NINA.Core.Utility.RelayCommand testConnectCommand;
        public override NINA.Core.Utility.RelayCommand TestConnectCommand => testConnectCommand;
        public bool IsMLAstroRPASelected => SystemName == "MLAstroRPA";

        public UniversalPolarAlignmentMLAstroRPAVM(IProfileService profileService) : base(profileService)
        {
            testConnectCommand = new RelayCommand(_ => _ = TestConnectAsync());
        }
#pragma warning restore CS0618
 
        protected override IPolarAlignmentSystem CreateSystem() => new UniversalPolarAlignmentMLAstroRPA();
        protected override string SystemName => "MLAstroRPA";

        public override bool DoAutomatedAdjustments {
            get => Properties.Settings.Default.DoAutomatedAdjustments;
            set {
                Properties.Settings.Default.DoAutomatedAdjustments = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override double AutomatedAdjustmentSettleTime {
            get => Properties.Settings.Default.AutomatedAdjustmentSettleTime;
            set {
                Properties.Settings.Default.AutomatedAdjustmentSettleTime = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public bool MLAstroRPAOvershootEnabled {
            get => Properties.Settings.Default.MLAstroRPAOvershootEnabled;
            set {
                Properties.Settings.Default.MLAstroRPAOvershootEnabled = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public bool MLAstroRPAOvershootUp {
            get => Properties.Settings.Default.MLAstroRPAOvershootUp;
            set {
                Properties.Settings.Default.MLAstroRPAOvershootUp = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public double MLAstroRPAOvershootUpArcMin {
            get => Properties.Settings.Default.MLAstroRPAOvershootUpArcMin;
            set {
                // Overshoot is limited to 0 .. 240 arcminutes past the target.
                Properties.Settings.Default.MLAstroRPAOvershootUpArcMin = Math.Clamp(value, 0, 240);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public bool MLAstroRPAOvershootDown {
            get => Properties.Settings.Default.MLAstroRPAOvershootDown;
            set {
                Properties.Settings.Default.MLAstroRPAOvershootDown = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public double MLAstroRPAOvershootDownArcMin {
            get => Properties.Settings.Default.MLAstroRPAOvershootDownArcMin;
            set {
                // Overshoot is limited to 0 .. 240 arcminutes past the target.
                Properties.Settings.Default.MLAstroRPAOvershootDownArcMin = Math.Clamp(value, 0, 240);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override float XGearRatio { get => 1f; set { } }
        public override int XSpeed { get => 1; set { } }
        public override float YGearRatio { get => 1f; set { } }
        public override int YSpeed { get => 1; set { } }
        public override bool ReverseAzimuth {
            get => Properties.Settings.Default.MLAstroRPAReverseAzimuth;
            set {
                Properties.Settings.Default.MLAstroRPAReverseAzimuth = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override bool ReverseAltitude {
            get => Properties.Settings.Default.MLAstroRPAReverseAltitude;
            set {
                Properties.Settings.Default.MLAstroRPAReverseAltitude = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override bool EnableAutoReverse {
            get => Properties.Settings.Default.MLAstroRPAEnableAutoReverse;
            set {
                Properties.Settings.Default.MLAstroRPAEnableAutoReverse = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                OnPropertyChanged(nameof(ManualReverseEnabled));
            }
        }

        /// <summary>
        /// Percentage of the full correction moved on the very first nudge while
        /// auto-reverse is probing the direction (1–100).
        /// </summary>
        public double MLAstroRPAReverseDetectPercent {
            get => Properties.Settings.Default.MLAstroRPAReverseDetectPercent;
            set {
                Properties.Settings.Default.MLAstroRPAReverseDetectPercent = Math.Clamp(value, 1, 100);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Safety factor (in percent) applied to automated correction moves: the Azimuth
        /// axis always, and the Altitude axis when overshoot is not used for the current
        /// direction. 75 = correct 75% (0.75) of the measured error, 100 = full error.
        /// </summary>
        public double MLAstroRPACorrectionFactorPercent {
            get => Properties.Settings.Default.MLAstroRPACorrectionFactorPercent;
            set {
                Properties.Settings.Default.MLAstroRPACorrectionFactorPercent = Math.Clamp(value, 1, 100);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }
        public override float XBacklashCompensation { get => 0f; set { } }

        private async Task TestConnectAsync()
        {
            Logger.Info($"[MLAstroRPA-TestConnect] CLICKED {DateTime.Now:HH:mm:ss.fff}");

            // 1) Ưu tiên dùng CHUNG cổng với plugin MLAstro (MLAstro là CHỦ cổng). Nếu MLAstro
            //    đang giữ cổng, test qua nó - mở cổng trực tiếp ở đây sẽ bị "port in use".
            var link = MLAstroLink.TryCreate();
            if (link != null && link.IsConnected && !string.IsNullOrWhiteSpace(link.ConfiguredComPort))
            {
                TestConnectStatus = $"Checking {link.ConfiguredComPort} via MLAstro plugin...";
                try
                {
                    var shared = new SharedMlastroSerial(link);
                    shared.Open(); // BeginExternalControl - cổng MLAstro đã mở nên tức thì
                    shared.WriteLine("?");
                    var status = await ReadStatusViaSharedAsync(shared);
                    shared.Close();
                    if (!string.IsNullOrWhiteSpace(status))
                    {
                        TestConnectStatus = $"MLAstroRPA detected via MLAstro plugin on {link.ConfiguredComPort}. Status: {status}";
                        Logger.Info($"[MLAstroRPA-TestConnect] Detected via MLAstro plugin on {link.ConfiguredComPort}. Status: {status}");
                        return;
                    }
                    TestConnectStatus = $"MLAstro plugin connected but no valid status on {link.ConfiguredComPort}.";
                    Logger.Error($"[MLAstroRPA-TestConnect] No valid status via MLAstro plugin on {link.ConfiguredComPort}.");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[MLAstroRPA-TestConnect] Shared test via MLAstro failed: {ex.Message}");
                }
                // Không xác nhận được qua MLAstro thì thử scan trực tiếp bên dưới.
            }

            var ports = SerialPort.GetPortNames().OrderBy(p => p).ToArray();
            var portsText = ports.Length == 0 ? "<none>" : string.Join(",", ports);
            Logger.Info($"[MLAstroRPA-TestConnect] Available ports: {portsText}");

            if (ports.Length == 0)
            {
                TestConnectStatus = "No COM ports found.";
                Logger.Error("[MLAstroRPA-TestConnect] No COM ports found.");
                return;
            }

            foreach (var comPort in ports)
            {
                TestConnectStatus = $"[DEBUG] Checking {comPort}... {DateTime.Now:HH:mm:ss.fff}";
                Logger.Info($"[MLAstroRPA-TestConnect] Checking {comPort}... {DateTime.Now:HH:mm:ss.fff}");

                using var port = new LoggingSerialPort(comPort, 115200, Parity.None, 8, StopBits.One)
                {
                    NewLine = "\n",
                    Handshake = Handshake.None,
                    DtrEnable = true,
                    RtsEnable = true,
                    ReadTimeout = 1000,
                    WriteTimeout = 1000
                };

                try
                {
                    port.Open();
                    // Give the device a bit more time to become responsive after open
                    await Task.Delay(300);
                    port.DiscardInBuffer();

                    Logger.Info($"[MLAstroRPA-TestConnect] Opened {comPort} (115200 8N1) {DateTime.Now:HH:mm:ss.fff}");

                    port.WriteLine("[MLAstroRPA-TC]");
                    var ack = port.ReadLine()?.Trim();
                    Logger.Info($"[MLAstroRPA-TestConnect] {comPort} handshake response: {ack}");

                    var ackToken = ack?.Split(',')[0];
                    if (!string.Equals(ackToken, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    port.WriteLine("?");
                    string status = null;
                    try {
                        // Read a few lines and pick the first that matches the expected status regex.
                        for (int i = 0; i < 3; i++) {
                            var line = port.ReadLine()?.Trim();
                            Logger.Info($"[MLAstroRPA-TestConnect] {comPort} status line {i + 1}: {line}");
                            if (!string.IsNullOrWhiteSpace(line) && StatusRegex().IsMatch(line)) {
                                status = line;
                                break;
                            }
                        }
                    } catch (TimeoutException) {
                        Logger.Info($"[MLAstroRPA-TestConnect] {comPort} status read timed out.");
                    }

                    Logger.Info($"[MLAstroRPA-TestConnect] {comPort} status response: {status}");

                    if (!string.IsNullOrWhiteSpace(status) && StatusRegex().IsMatch(status))
                    {
                        TestConnectStatus = $"MLAstroRPA detected on {comPort}. Status: {status}";
                        Logger.Info($"[MLAstroRPA-TestConnect] MLAstroRPA detected on {comPort}. Status: {status}");
                        return;
                    }

                    TestConnectStatus = $"Handshake succeeded on {comPort}, but the status response was invalid.";
                    Logger.Error($"[MLAstroRPA-TestConnect] Handshake succeeded on {comPort}, but the status response was invalid: {status}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[MLAstroRPA-TestConnect] {comPort} failed: {ex.Message}");
                }
                finally 
                {
                    if (port.IsOpen) port.Close();
                }
            }

            TestConnectStatus = $"MLAstroRPA was not detected on any COM port. Scanned: {portsText}";
            Logger.Error($"[MLAstroRPA-TestConnect] MLAstroRPA was not detected on any COM port. Scanned: {portsText}");
        }

        private async Task<string> ReadStatusViaSharedAsync(SharedMlastroSerial shared)
        {
            var deadline = Environment.TickCount + 2000;
            var sb = new System.Text.StringBuilder();
            while (Environment.TickCount < deadline)
            {
                try { sb.Append(shared.ReadExisting()); } catch { }
                var m = StatusRegex().Match(sb.ToString());
                if (m.Success) return m.Value.Trim();
                await Task.Delay(50);
            }
            return null;
        }

        [GeneratedRegex(@"<(?<status>[^|>]+)\|M[Pp]os:(?<x>[+-]?\d+(\.\d+)?),(?<y>[+-]?\d+(\.\d+)?)(,(?<z>[+-]?\d+(\.\d+)?))?\|")]
        private static partial Regex StatusRegex();
    }
}

using NINA.Core.Utility;
using System;
using System.Globalization;
using System.IO.Ports;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment {
    public abstract partial class UniversalPolarAlignmentBase : IPolarAlignmentSystem {
        // port có thể là: LoggingSerialPort (TPPA tự mở, fallback) hoặc SharedMlastroSerial
        // (dùng CHUNG cổng với plugin MLAstro — MLAstro là chủ cổng).
        protected ISerialLink port;
        protected bool IsExpectingStatusResponse { get; set; } = false;

        protected abstract string SystemName { get; }
        protected virtual string NewLineSequence => "\r\n";
        protected virtual int ScanReadTimeout => 1000;
        protected virtual int ScanWriteTimeout => 1000;
        protected virtual bool ClearBufferOnConnect => false;
        protected virtual string StatusQueryCommand => "?";
        protected virtual int StatusResponseLineCount => 2;

        protected virtual void OnPortOpened(ISerialLink serialPort) { }

        protected virtual bool IsStatusResponseValid(string status) {
            return GetStatusRegex().Match(status).Success;
        }

        protected virtual string ReadStatusResponse(ISerialLink serialPort) {
            var status = serialPort.ReadLine();
            for (int i = 1; i < StatusResponseLineCount; i++) {
                _ = serialPort.ReadLine();
            }
            return status;
        }

        protected abstract Regex GetStatusRegex();

        protected ISerialLink Port => port;

        /// <summary>Chế độ mặc định (Avalon/OAPA/...): tự quét & mở cổng như trước đây.</summary>
        protected UniversalPolarAlignmentBase() {
            OpenAndValidate();
        }

        /// <summary>
        /// deferOpen = true: KHÔNG tự mở ở ctor để subclass chọn lúc mở.
        /// (UniversalPolarAlignmentMLAstroRPA dùng để ưu tiên "dùng chung cổng với MLAstro",
        /// không có MLAstro thì tự quét như cũ.)
        /// </summary>
        protected UniversalPolarAlignmentBase(bool deferOpen) {
        }

        /// <summary>Gắn một transport đã mở sẵn (vd phiên dùng chung với plugin MLAstro).
        /// (Không gọi virtual UpdateStatus() ở đây để tránh dispatch sớm khi còn trong base ctor;
        /// subclass gọi UpdateStatus() sau khi dựng xong.)</summary>
        protected UniversalPolarAlignmentBase(ISerialLink openedPort) {
            port = openedPort;
        }

        protected void AttachPort(ISerialLink openedPort) => port = openedPort;

        /// <summary>Quét toàn bộ cổng COM, mở cổng nói chuyện được với thiết bị rồi đọc trạng thái.</summary>
        protected void OpenAndValidate() {
            var comPorts = SerialPort.GetPortNames();
            foreach (var comPort in comPorts) {
                var serialPortToTest = new LoggingSerialPort() {
                    PortName = comPort,
                    BaudRate = 115200,
                    Parity = Parity.None,
                    DataBits = 8,
                    StopBits = StopBits.One,
                    NewLine = NewLineSequence
                };

                serialPortToTest.ReadTimeout = ScanReadTimeout;
                serialPortToTest.WriteTimeout = ScanWriteTimeout;

                try {
                    serialPortToTest.Open();
                    if (serialPortToTest.IsOpen) {
                        if (ClearBufferOnConnect) {
                            Thread.Sleep(100);
                            serialPortToTest.DiscardInBuffer();
                        }

                        OnPortOpened(serialPortToTest);

                        // indicate that we are explicitly querying the device for status
                        IsExpectingStatusResponse = true;
                        serialPortToTest.WriteLine(StatusQueryCommand);
                        var status = ReadStatusResponse(serialPortToTest);
                        IsExpectingStatusResponse = false;
                        if (IsStatusResponseValid(status)) {
                            port = serialPortToTest;
                            Logger.Info($"Found {SystemName} on {comPort}");
                            break;
                        } else {
                            serialPortToTest.Close();
                            serialPortToTest.Dispose();
                            continue;
                        }
                    }
                } catch (Exception ex) {
                    Logger.Error($"Error scanning {comPort} for {SystemName}: {ex.Message}");
                    serialPortToTest?.Close();
                    serialPortToTest?.Dispose();
                }
            }
            if (port == null) {
                throw new Exception($"Unable to find {SystemName}");
            }
            UpdateStatus();
        }

        public bool Connected => port.IsOpen;
        public string Status { get; protected set; }

        protected float XPosition { get; private set; }
        protected float YPosition { get; private set; }
        protected float ZPosition { get; private set; }

        public LastDirection XLastDirection { get; protected set; } = LastDirection.Positive;
        public LastDirection YLastDirection { get; protected set; } = LastDirection.Positive;
        public LastDirection ZLastDirection { get; protected set; } = LastDirection.Positive;

        public float XPosition1 { get => XPosition / XGearRatio; }
        public float YPosition1 { get => YPosition / YGearRatio; }
        public float ZPosition1 { get => ZPosition / ZGearRatio; }

        public abstract float XGearRatio { get; set; }
        public abstract float YGearRatio { get; set; }
        public float ZGearRatio { get; set; } = 1;

        protected SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);

        public virtual async Task MoveRelative(Axis axis, int speed, float position, CancellationToken token) {
            await semaphore.WaitAsync(token);
            try {
                UpdateStatus();
                var axisCommand = axis switch {
                    Axis.XAxis => "X",
                    Axis.YAxis => "Y",
                    Axis.ZAxis => "Z",
                    _ => throw new ArgumentException("Invalid Axis"),
                };
                var gearRatio = axis switch {
                    Axis.XAxis => XGearRatio,
                    Axis.YAxis => YGearRatio,
                    Axis.ZAxis => ZGearRatio,
                    _ => throw new ArgumentException("Invalid Axis"),
                };

                Func<float> checkProperty = axis switch {
                    Axis.XAxis => () => XPosition,
                    Axis.YAxis => () => YPosition,
                    Axis.ZAxis => () => ZPosition,
                    _ => throw new ArgumentException("Invalid Axis"),
                };

                var target = checkProperty() + position * gearRatio;

                switch (axis) {
                    case Axis.XAxis: XLastDirection = position >= 0 ? LastDirection.Positive : LastDirection.Negative; break;
                    case Axis.YAxis: YLastDirection = position >= 0 ? LastDirection.Positive : LastDirection.Negative; break;
                    case Axis.ZAxis: ZLastDirection = position >= 0 ? LastDirection.Positive : LastDirection.Negative; break;
                }

                var command = $"$J=G91G21{axisCommand}{(position * gearRatio).ToString(CultureInfo.InvariantCulture)}F{speed.ToString(CultureInfo.InvariantCulture)}";
                Logger.Info($"Sending command: {command}");
                port.WriteLine(command);
                var ok = port.ReadLine();
                Logger.Info($"Response: {ok}");

                var startPos = checkProperty();
                var timeout = TimeSpan.FromSeconds(30);
                var startTime = DateTime.Now;
                var lastPos = startPos;
                var stuckCount = 0;

                while (Math.Abs(checkProperty() - target) > 0.01f) {
                    UpdateStatus();
                    var currentPos = checkProperty();

                    if (Math.Abs(currentPos - lastPos) < 0.01f) {
                        stuckCount++;
                        if (stuckCount > 5) {
                            throw new TimeoutException($"Motor appears stuck at position {currentPos}. Target was {target}. Check hardware and endstops.");
                        }
                    } else {
                        stuckCount = 0;
                    }
                    lastPos = currentPos;

                    if (DateTime.Now - startTime > timeout) {
                        throw new TimeoutException($"Movement timeout after {timeout.TotalSeconds}s. Current: {currentPos}, Target: {target}");
                    }

                    await Task.Delay(300, token);
                }
            } finally {
                semaphore.Release();
            }
        }

        public virtual async Task MoveAbsolute(Axis axis, int speed, float position, CancellationToken token) {
            await semaphore.WaitAsync(token);
            try {
                UpdateStatus();
                var axisCommand = axis switch {
                    Axis.XAxis => "X",
                    Axis.YAxis => "Y",
                    Axis.ZAxis => "Z",
                    _ => throw new ArgumentException("Invalid Axis"),
                };
                var gearRatio = axis switch {
                    Axis.XAxis => XGearRatio,
                    Axis.YAxis => YGearRatio,
                    Axis.ZAxis => ZGearRatio,
                    _ => throw new ArgumentException("Invalid Axis"),
                };

                var target = position * gearRatio;

                switch (axis) {
                    case Axis.XAxis: XLastDirection = position - XPosition1 >= 0 ? LastDirection.Positive : LastDirection.Negative; break;
                    case Axis.YAxis: YLastDirection = position - YPosition1 >= 0 ? LastDirection.Positive : LastDirection.Negative; break;
                    case Axis.ZAxis: ZLastDirection = position - ZPosition1 >= 0 ? LastDirection.Positive : LastDirection.Negative; break;
                }

                var command = $"$J=G53{axisCommand}{target.ToString(CultureInfo.InvariantCulture)}F{speed.ToString(CultureInfo.InvariantCulture)}";
                Logger.Info($"Sending command: {command}");
                port.WriteLine(command);
                var ok = port.ReadLine();
                Logger.Info($"Response: {ok}");

                Func<float> checkProperty = axis switch {
                    Axis.XAxis => () => XPosition,
                    Axis.YAxis => () => YPosition,
                    Axis.ZAxis => () => ZPosition,
                    _ => throw new ArgumentException("Invalid Axis"),
                };

                var startPos = checkProperty();
                var timeout = TimeSpan.FromSeconds(30);
                var startTime = DateTime.Now;
                var lastPos = startPos;
                var stuckCount = 0;

                while (Math.Abs(checkProperty() - target) > 0.01f) {
                    UpdateStatus();
                    var currentPos = checkProperty();

                    if (Math.Abs(currentPos - lastPos) < 0.01f) {
                        stuckCount++;
                        if (stuckCount > 5) {
                            throw new TimeoutException($"Motor appears stuck at position {currentPos}. Target was {target}. Check hardware and endstops.");
                        }
                    } else {
                        stuckCount = 0;
                    }
                    lastPos = currentPos;

                    if (DateTime.Now - startTime > timeout) {
                        throw new TimeoutException($"Movement timeout after {timeout.TotalSeconds}s. Current: {currentPos}, Target: {target}");
                    }

                    await Task.Delay(300, token);
                }
            } finally {
                semaphore.Release();
            }
        }

        protected virtual void UpdateStatus() {
            port.WriteLine(StatusQueryCommand);
            var status = ReadStatusLine(port);

            if (!TryApplyStatusLine(status)) {
                Logger.Error($"Failed to parse {SystemName} status: {status}");
            }
        }

        protected bool TryApplyStatusLine(string status) {
            var match = GetStatusRegex().Match(status);
            if (match.Success) {
                Status = match.Groups["status"].Value;
                XPosition = float.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture);
                YPosition = float.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
                if (match.Groups["z"].Success) {
                    ZPosition = float.Parse(match.Groups["z"].Value, CultureInfo.InvariantCulture);
                }
                return true;
            }
            return false;
        }

        private static string ReadStatusLine(ISerialLink serialPort) {
            var status = serialPort.ReadLine();
            if (string.IsNullOrWhiteSpace(status) ||
                string.Equals(status.Trim(), "ok", StringComparison.OrdinalIgnoreCase)) {
                status = serialPort.ReadLine();
            } else {
                _ = serialPort.ReadLine();
            }
            return status;
        }

        public async Task RefreshStatus(CancellationToken token) {
            await semaphore.WaitAsync(token);
            try {
                UpdateStatus();
            } finally {
                semaphore.Release();
            }
        }

        public virtual Task Abort(CancellationToken token) {
            return Task.CompletedTask;
        }

        public void Dispose() => port?.Dispose();
    }
}

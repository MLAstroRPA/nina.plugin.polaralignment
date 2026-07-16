using NINA.Core.Utility;
using System;
using System.Globalization;
using System.IO.Ports;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks; 

namespace NINA.Plugins.PolarAlignment.MLAstroRPA {
    public partial class UniversalPolarAlignmentMLAstroRPA : UniversalPolarAlignmentBase {
        private readonly object alignmentSync = new();
        private TaskCompletionSource<string> alignmentCompletionSource;

        protected override string SystemName => "MLAstroRPA";
        protected override string NewLineSequence => "\n";
        protected override int ScanReadTimeout => 2000;
        protected override int ScanWriteTimeout => 1000;
        protected override bool ClearBufferOnConnect => true;
        // The MLAstroRPA device emits a short prompt/echo line, then the status payload line,
        // and finally a metadata line. Read three lines and return the meaningful status.
        protected override int StatusResponseLineCount => 3;

        protected override string ReadStatusResponse(SerialPort serialPort) {
            try {
                string first = null;
                string preferred = null;
                for (int i = 0; i < StatusResponseLineCount; i++) {
                    string line;
                    try {
                        line = serialPort.ReadLine();
                    } catch (TimeoutException) {
                        Logger.Info($"[MLAstroRPA] ReadStatusResponse: read timeout on line {i + 1}");
                        break;
                    }

                    Logger.Info($"[MLAstroRPA] ReadStatusResponse: line {i + 1}: {line}");
                    if (i == 0) first = line;
                    if (!string.IsNullOrWhiteSpace(line)) {
                        var trimmed = line.Trim();
                        if (GetStatusRegex().IsMatch(trimmed)) {
                            preferred = trimmed;
                            // keep reading to flush any remaining lines
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(preferred)) {
                    Logger.Info($"[MLAstroRPA] ReadStatusResponse: selected preferred line: {preferred}");
                    return preferred;
                }
                Logger.Info($"[MLAstroRPA] ReadStatusResponse: returning first line: {first}");
                return first;
            } catch (Exception ex) {
                Logger.Error($"[MLAstroRPA] ReadStatusResponse error: {ex.Message}");
                return null;
            }
        }

        private float xGearRatio = 1f;
        private float yGearRatio = 1f;
        public override float XGearRatio { get => xGearRatio; set => xGearRatio = value; }
        public override float YGearRatio { get => yGearRatio; set => yGearRatio = value; }

        protected override Regex GetStatusRegex() => StatusRegex();

        protected override void OnPortOpened(SerialPort serialPort) {
            serialPort.WriteLine("[MLAstroRPA-TC]");
            var response = serialPort.ReadLine();
            if (!string.Equals(response?.Trim(), "ok", StringComparison.OrdinalIgnoreCase)) {
                throw new Exception($"Handshake failed. Response: {response}");
            }
            Logger.Info("[MLAstroRPA] Handshake OK");
        }

        protected override bool IsStatusResponseValid(string status) {
            return GetStatusRegex().IsMatch(status);
        }

        public override async Task MoveRelative(Axis axis, int speed, float position, CancellationToken token) {
            TaskCompletionSource<string> completionSource;

            await semaphore.WaitAsync(token);
            try {
                if (axis == Axis.XAxis) XLastDirection = position >= 0 ? LastDirection.Positive : LastDirection.Negative;
                if (axis == Axis.YAxis) YLastDirection = position >= 0 ? LastDirection.Positive : LastDirection.Negative;

                var (deg, min, sec, dir) = ToDms(position);

                string command = axis == Axis.XAxis
                    ? $"AzED:{deg},AzEM:{min},AzES:{sec.ToString("0.###", CultureInfo.InvariantCulture)},AzDi:{dir},AlED:0,AlEM:0,AlES:0,AlDi:1,AAll:1"
                    : $"AzED:0,AzEM:0,AzES:0,AzDi:1,AlED:{deg},AlEM:{min},AlES:{sec.ToString("0.###", CultureInfo.InvariantCulture)},AlDi:{dir},AAll:1";

                completionSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (alignmentSync) {
                    alignmentCompletionSource = completionSource;
                }

                Logger.Info($"[MLAstroRPA] TX ALIGN: {command}");
                Port.WriteLine(command);

                var ack = Port.ReadLine()?.Trim();
                if (!string.Equals(ack, "ok", StringComparison.OrdinalIgnoreCase)) {
                    throw new Exception($"Align command rejected: {ack}");
                }
            } catch {
                lock (alignmentSync) {
                    alignmentCompletionSource = null;
                }
                throw;
            } finally {
                semaphore.Release();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(90), timeoutCts.Token);

            // Poll "?" periodically until the device reports READY or ALIGN_COMPLETED
            var pollingTask = Task.Run(async () => {
                while (!completionSource.Task.IsCompleted) {
                    try {
                        await semaphore.WaitAsync(timeoutCts.Token);
                        try {
                            Port.WriteLine(StatusQueryCommand);
                            var line = ReadStatusResponse(Port)?.Trim();
                            Logger.Info($"[MLAstroRPA] Poll status: {line}");
                            if (!string.IsNullOrWhiteSpace(line)) {
                                UpdateStatusFromLine(line);
                            }
                        } finally {
                            semaphore.Release();
                        }
                    } catch (OperationCanceledException) {
                        break;
                    } catch (Exception ex) {
                        Logger.Error($"[MLAstroRPA] Poll error: {ex.Message}");
                    }
                    await Task.Delay(300, timeoutCts.Token).ContinueWith(_ => { });
                }
            }, timeoutCts.Token);

            var finishedTask = await Task.WhenAny(completionSource.Task, timeoutTask);

            lock (alignmentSync) {
                alignmentCompletionSource = null;
            }
            timeoutCts.Cancel();
            await pollingTask.ContinueWith(_ => { });

            if (finishedTask == timeoutTask) {
                throw new TimeoutException("Timeout waiting MLAstroRPA alignment completion from RefreshStatus telemetry.");
            }

            var finalStatus = await completionSource.Task;
            Logger.Info($"[MLAstroRPA] Align completed with status: {finalStatus}");
        }

        public override async Task Abort(CancellationToken token) {
            await semaphore.WaitAsync(token);
            try {
                Logger.Info("[MLAstroRPA] Sending abort command: STOP:1");
                Port.WriteLine("STOP:1");

                string response = string.Empty;
                try {
                    response = Port.ReadLine()?.Trim() ?? string.Empty;
                } catch {
                }

                TaskCompletionSource<string> completionSource;
                lock (alignmentSync) {
                    completionSource = alignmentCompletionSource;
                    alignmentCompletionSource = null;
                }

                completionSource?.TrySetException(new OperationCanceledException("Alignment aborted by user."));
                Logger.Info($"[MLAstroRPA] Abort command sent. Response: {response}");
            } finally {
                semaphore.Release();
            }
        }

        protected override void UpdateStatus() {
            Port.WriteLine(StatusQueryCommand);
            var line = ReadStatusResponse(Port)?.Trim();
            if (!string.IsNullOrWhiteSpace(line)) {
                UpdateStatusFromLine(line);
            }
        }

        private void UpdateStatusFromLine(string line) {
            if (!TryApplyStatusLine(line)) {
                Logger.Error($"Failed to parse {SystemName} status: {line}");
                return;
            }

            TaskCompletionSource<string> completionSource;
            lock (alignmentSync) {
                completionSource = alignmentCompletionSource;
            }

            if (completionSource == null) {
                return;
            }

            if (string.Equals(Status, "ERROR", StringComparison.OrdinalIgnoreCase)) {
                completionSource.TrySetException(new Exception($"{SystemName} reported ERROR."));
                return;
            }

            if (string.Equals(Status, "READY", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Status, "ALIGN_COMPLETED", StringComparison.OrdinalIgnoreCase)) {
                completionSource.TrySetResult(Status);
            }
        }

        public override async Task MoveAbsolute(Axis axis, int speed, float position, CancellationToken token) {
            await RefreshStatus(token);
            var delta = axis == Axis.XAxis ? position - XPosition1 : position - YPosition1;
            await MoveRelative(axis, speed, delta, token);
        }

        private static (int deg, int min, double sec, int dir) ToDms(float arcMinutes) {
            var positive = arcMinutes >= 0f;
            var absMinutes = Math.Abs(arcMinutes);

            var totalDegrees = absMinutes / 60.0;
            var deg = (int)Math.Floor(totalDegrees);
            var remainMin = (totalDegrees - deg) * 60.0;
            var min = (int)Math.Floor(remainMin);
            var sec = (remainMin - min) * 60.0;

            if (sec >= 59.9995) { sec = 0; min++; }
            if (min >= 60) { min = 0; deg++; }

            var dir = positive ? 1 : 0;
            return (deg, min, sec, dir);
        }

        [GeneratedRegex(@"<(?<status>[^|>]+)\|M[Pp]os:(?<x>[+-]?\d+(\.\d+)?),(?<y>[+-]?\d+(\.\d+)?)(,(?<z>[+-]?\d+(\.\d+)?))?\|")]
        private static partial Regex StatusRegex();
    }
}

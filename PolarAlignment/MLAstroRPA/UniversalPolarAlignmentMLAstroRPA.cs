using NINA.Core.Utility;
using System;
using System.Globalization;
using System.IO.Ports;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks; 

namespace NINA.Plugins.PolarAlignment.MLAstroRPA {
    public partial class UniversalPolarAlignmentMLAstroRPA : UniversalPolarAlignmentBase, IDisposable {
        private readonly object alignmentSync = new();
        private TaskCompletionSource<string> alignmentCompletionSource;

        /// <summary>
        /// Ưu tiên dùng CHUNG cổng COM với plugin MLAstro (MLAstro là CHỦ cổng, cùng process NINA).
        /// Nếu MLAstro chưa cài / chưa nạp thì TPPA tự quét &amp; mở cổng riêng (fallback như trước).
        /// </summary>
        public UniversalPolarAlignmentMLAstroRPA() : base(deferOpen: true) {
            if (!TryOpenPreferred()) {
                throw new Exception($"Unable to find {SystemName}");
            }
        }

        private bool TryOpenPreferred() {
            try {
                var link = MLAstroLink.TryCreate();
                if (link != null && !string.IsNullOrWhiteSpace(link.ConfiguredComPort)) {
                    var shared = new SharedMlastroSerial(link);
                    shared.StopRequested += OnExternalStop;   // MLAstro bấm STOP/E-STOP -> dừng PA
                    shared.Open();   // ném exception nếu không mở được -> rơi xuống catch -> fallback
                    AttachPort(shared);
                    UpdateStatus();
                    if (Connected && !string.IsNullOrWhiteSpace(Status)) {
                        Logger.Info($"[MLAstroRPA] Connected through MLAstro plugin on {link.ConfiguredComPort}");
                        return true;
                    }
                    Logger.Info("[MLAstroRPA] MLAstro shared session not usable; falling back to direct scan.");
                    try { shared.Close(); } catch { }
                }
            } catch (Exception ex) {
                Logger.Error($"[MLAstroRPA] Shared connect via MLAstro failed ({ex.Message}); falling back to direct scan.");
            }
            // Fallback: không có MLAstro plugin -> TPPA tự mở cổng như cũ.
            port = null;   // để OpenAndValidate() báo lỗi đúng nếu không tìm thấy thiết bị
            OpenAndValidate();
            return port != null;
        }

        /// <summary>MLAstro báo STOP/E-STOP (hoặc ngắt) giữa chừng -> hủy PA đang chạy ngay.</summary>
        private void OnExternalStop() {
            Logger.Info("[MLAstroRPA] Aborting PA because MLAstro requested STOP.");
            try {
                if (Port?.IsOpen == true) Port.WriteLine("STOP:1");
            } catch (Exception ex) {
                Logger.Error($"[MLAstroRPA] External STOP send failed: {ex.Message}");
            }
            TaskCompletionSource<string> completionSource;
            lock (alignmentSync) {
                completionSource = alignmentCompletionSource;
                alignmentCompletionSource = null;
            }
            completionSource?.TrySetException(new OperationCanceledException("PA aborted by MLAstro STOP."));
        }

        protected override string SystemName => "MLAstroRPA";
        protected override string NewLineSequence => "\n";
        protected override int ScanReadTimeout => 300;
        protected override int ScanWriteTimeout => 300;
        protected override bool ClearBufferOnConnect => true;
        // The MLAstroRPA device emits a short prompt/echo line, then the status payload line,
        // and finally a metadata line. Read three lines and return the meaningful status.
        protected override int StatusResponseLineCount => 3;

        protected override string ReadStatusResponse(ISerialLink serialPort)
        {
            try
            {
                var buffer = string.Empty;
                string firstNonEmpty = null;
                string firstShortToken = null;
                var shortTokenCounts = new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);

                // whitelist of short status tokens to treat specially
                var shortTokens = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
                {
                    "ok",
                    "READY",
                    "COMPLETED",
                    "COMPLETTED",
                    "DISCONNECTED"
                };

                // If we did not explicitly query (i.e., spontaneous data), do a single non-blocking read
                if (!IsExpectingStatusResponse)
                {
                    try
                    {
                        var available = serialPort.BytesToRead;
                        if (available <= 0)
                            return null;

                        buffer = serialPort.ReadExisting();
                        var lines = buffer.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                        for (int i = 0; i < lines.Length; i++)
                        {
                            var trimmed = lines[i].Trim();
                            if (string.IsNullOrWhiteSpace(trimmed))
                                continue;
                            if (firstNonEmpty == null)
                                firstNonEmpty = trimmed;

                            // prefer a full regex match
                            var m = GetStatusRegex().Match(trimmed);
                            if (m.Success)
                            {
                                var found = m.Value.Trim();
                                Logger.Info($"[MLAstroRPA] ReadStatusResponse: found status (non-query): {found}");
                                return found;
                            }

                            Logger.Info($"[MLAstroRPA] ReadStatusResponse(part non-query): {trimmed}");
                        }

                        // Fall back to the first non-empty fragment, but ignore a bare
                        // "ok" acknowledgment since it carries no status information.
                        if (!string.IsNullOrWhiteSpace(firstNonEmpty)
                            && !string.Equals(firstNonEmpty, "ok", StringComparison.OrdinalIgnoreCase))
                        {
                            return firstNonEmpty;
                        }

                        return null;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[MLAstroRPA] ReadStatusResponse error (non-query): {ex.Message}");
                        return null;
                    }
                }

                // total window to aggregate fragments from firmware (adjustable)
                var deadline = DateTime.UtcNow.AddMilliseconds(300);

                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        var available = serialPort.BytesToRead;
                        if (available > 0)
                        {
                            buffer += serialPort.ReadExisting();

                            // 1) try regex match across the whole buffer to handle split fragments
                            var match = GetStatusRegex().Match(buffer);
                            if (match.Success)
                            {
                                var found = match.Value.Trim();
                                Logger.Info($"[MLAstroRPA] ReadStatusResponse: found status: {found}");
                                return found;
                            }

                            var lines = buffer.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

                            // keep last partial fragment in buffer
                            buffer = lines[^1];

                            for (int i = 0; i < lines.Length - 1; i++)
                            {
                                var line = lines[i];
                                var trimmed = line.Trim();

                                if (string.IsNullOrWhiteSpace(trimmed))
                                    continue;

                                // if it's a known short token, remember it but don't spam logs
                                if (shortTokens.Contains(trimmed))
                                {
                                    if (firstShortToken == null)
                                        firstShortToken = trimmed;
                                    if (shortTokenCounts.ContainsKey(trimmed))
                                        shortTokenCounts[trimmed]++;
                                    else
                                        shortTokenCounts[trimmed] = 1;
                                    continue;
                                }

                                // log longer fragments
                                Logger.Info($"[MLAstroRPA] ReadStatusResponse(part): {line}");

                                if (firstNonEmpty == null)
                                    firstNonEmpty = trimmed;

                                if (GetStatusRegex().IsMatch(trimmed))
                                {
                                    Logger.Info($"[MLAstroRPA] ReadStatusResponse: selected preferred line: {trimmed}");
                                    return trimmed;
                                }
                            }
                        }
                        else
                        {
                            Thread.Sleep(10);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[MLAstroRPA] ReadStatusResponse error (inner): {ex.Message}");
                        break;
                    }
                }

                // final attempt: try match across any remaining buffer
                var finalMatch = GetStatusRegex().Match(buffer);
                if (finalMatch.Success)
                {
                    var found = finalMatch.Value.Trim();
                    Logger.Info($"[MLAstroRPA] ReadStatusResponse: final match: {found}");
                    return found;
                }

                // If we saw short tokens, log a compact summary and return the first short token as a fallback
                if (shortTokenCounts.Count > 0)
                {
                    try
                    {
                        var parts = new System.Collections.Generic.List<string>();
                        foreach (var kv in shortTokenCounts)
                        {
                            parts.Add($"{kv.Key} x{kv.Value}");
                        }
                        var summary = string.Join(", ", parts);
                        Logger.Info($"[MLAstroRPA] ReadStatusResponse: short tokens summary: {summary}");
                    }
                    catch { }

                    return firstShortToken;
                }

                // Only return a non-empty fragment if it looks like a complete status (starts with '<')
                if (!string.IsNullOrWhiteSpace(firstNonEmpty)
                    && firstNonEmpty.TrimStart().StartsWith("<"))
                {
                    Logger.Info($"[MLAstroRPA] ReadStatusResponse: returning first non-empty: {firstNonEmpty}");
                    return firstNonEmpty;
                }

                Logger.Info("[MLAstroRPA] ReadStatusResponse: no data received or fragments were incomplete");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"[MLAstroRPA] ReadStatusResponse error: {ex.Message}");
                return null;
            }
        }

        private float xGearRatio = 1f;
        private float yGearRatio = 1f;
        public override float XGearRatio { get => xGearRatio; set => xGearRatio = value; }
        public override float YGearRatio { get => yGearRatio; set => yGearRatio = value; }

        protected override Regex GetStatusRegex() => StatusRegex();

        protected override void OnPortOpened(ISerialLink serialPort) {
            serialPort.WriteLine("[MLAstroRPA-TC]");
            var response = serialPort.ReadLine();
            var firstToken = response?.Trim().Split(',')[0];
            if (!string.Equals(firstToken, "ok", StringComparison.OrdinalIgnoreCase)) {
                throw new Exception($"Handshake failed. Response: {response}");
            }
            Logger.Info($"[MLAstroRPA] Handshake OK: {response?.Trim()}");
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

                // Remove stale responses left over from background "?" polling so the
                // next read is the device's actual acknowledgment of this command.
                if (Port.BytesToRead > 0) {
                    Logger.Info($"[MLAstroRPA] Discarding {Port.BytesToRead} stale bytes before ALIGN");
                    Port.DiscardInBuffer();
                }

                Port.WriteLine(command);

                ReadAlignAck();
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
                                // indicate this ReadStatusResponse call follows an explicit query
                                IsExpectingStatusResponse = true;
                                Port.WriteLine(StatusQueryCommand);
                                var line = ReadStatusResponse(Port)?.Trim();
                                IsExpectingStatusResponse = false;
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

        private string ReadAlignAck() {
            string line;
            try {
                line = Port.ReadLine()?.Trim();
            } catch (TimeoutException) {
                Logger.Info("[MLAstroRPA] RX ALIGN ack: none received within read timeout; continuing");
                return null;
            }

            if (string.IsNullOrEmpty(line)) {
                Logger.Info("[MLAstroRPA] RX ALIGN ack: (empty line)");
                return null;
            }

            // Firmware acknowledges with "ok" (optionally followed by details) or by
            // immediately reporting a status line. Both mean the command was accepted.
            if (line.StartsWith("ok", StringComparison.OrdinalIgnoreCase)
                || GetStatusRegex().IsMatch(line)) {
                Logger.Info($"[MLAstroRPA] RX ALIGN ack: {line}");
                return line;
            }

            Logger.Error($"[MLAstroRPA] RX ALIGN ack (unexpected): {line}");
            throw new Exception($"Align command rejected: {line}");
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
            IsExpectingStatusResponse = true;
            try {
                Port.WriteLine(StatusQueryCommand);
                var line = ReadStatusResponse(Port)?.Trim();
                if (!string.IsNullOrWhiteSpace(line)) {
                    UpdateStatusFromLine(line);
                }
            } finally {
                IsExpectingStatusResponse = false;
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

        // Explicit IDisposable implementation: the firmware's "Disconnect" command performs a
        // graceful stop and releases the serial handshake, so it must be sent right before the
        // COM port is actually closed. The base VM calls Dispose() through IPolarAlignmentSystem,
        // so interface dispatch reaches this implementation instead of the inherited base one.
        void IDisposable.Dispose() {
            try {
                // Khi dùng CHUNG cổng với MLAstro (SharedMlastroSerial): KHÔNG gửi "Disconnect"
                // cho firmware - MLAstro vẫn là chủ và còn điều khiển thiết bị. Nếu gửi Disconnect,
                // firmware nhả handshake nên lần mở lại qua MLAstro sẽ không điều khiển được.
                // Chỉ gửi "Disconnect" khi TPPA TỰ mở cổng (fallback, không có MLAstro plugin).
                if (Port?.IsOpen == true && !(Port is SharedMlastroSerial)) {
                    Logger.Info("[MLAstroRPA] Sending Disconnect command to firmware");
                    Port.WriteLine("Disconnect");
                    // Give the firmware a moment to stop motors and release the handshake
                    // before the COM port is closed.
                    Thread.Sleep(150);
                }
            } catch (Exception ex) {
                Logger.Error($"[MLAstroRPA] Disconnect command failed: {ex.Message}");
            } finally {
                base.Dispose();
            }
        }
    }
}

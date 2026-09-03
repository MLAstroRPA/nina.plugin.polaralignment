using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;

namespace NINA.Plugins.PolarAlignment {
    /// <summary>
    /// <see cref="ISerialLink"/> cho phiên DÙNG CHUNG cổng COM với plugin MLAstro (cùng process).
    /// - Ghi  : gửi qua MLAstro (chủ cổng) — dùng chung write-lock nên không đứt giữa dòng.
    /// - Đọc  : dòng RX được MLAstro forward vào hàng đợi; TPPA đọc từ hàng đợi.
    /// Khi mở/đóng ở TPPA sẽ đồng bộ mở/đóng với MLAstro (2 chiều).
    /// </summary>
    public sealed class SharedMlastroSerial : ISerialLink {
        private readonly MLAstroLink link;
        private readonly ConcurrentQueue<string> rxQueue = new();
        private readonly Action<string> onLine;
        private readonly Action<bool> onState;
        private readonly Action<string> onStop;
        private volatile bool open;
        // Chỉ hiện ĐÚNG 1 notification "ngắt do MLAstro" cho mỗi phiên: kênh STOP (reason=MLAstro
        // disconnected) và kênh State(false) đều có thể báo cùng một sự kiện ngắt.
        private volatile bool disconnectNotified;

        /// <summary>Xảy ra khi MLAstro báo STOP/E-STOP/ngắt - TPPA phải dừng PA ngay.</summary>
        public event Action StopRequested;

        public SharedMlastroSerial(MLAstroLink link) {
            this.link = link;
            onLine = EnqueueLine;
            onState = OnStateChanged;
            onStop = OnStopRequested;
        }

        private void OnStopRequested(string reason) {
            Logger.Info($"[MLAstroRPA] STOP requested by MLAstro: {reason}");
            ShowExternalStopNotification(reason);
            // Dừng toàn bộ routine PA của TPPA (driver + executeCTS của Dockable).
            try { PolarAlignmentPlugin.RequestStopFromExternal(reason); } catch (Exception ex) { Logger.Error($"[MLAstroRPA] RequestStopFromExternal failed: {ex.Message}"); }
            StopRequested?.Invoke();
        }

        /// <summary>
        /// Notification nêu rõ NGUYÊN NHÂN: tín hiệu dừng/ngắt này đến từ plugin MLAstro
        /// (kênh STOP - STOP/E-STOP bấm trên MLAstro, hoặc MLAstro đang ngắt cổng).
        /// </summary>
        private void ShowExternalStopNotification(string reason) {
            try {
                string message;
                if (reason?.IndexOf("E-STOP", StringComparison.OrdinalIgnoreCase) >= 0) {
                    message = "E-STOP pressed on MLAstro plugin - TPPA PA stopped.";
                } else if (reason?.IndexOf("STOP", StringComparison.OrdinalIgnoreCase) >= 0) {
                    message = "STOP pressed on MLAstro plugin - TPPA PA stopped.";
                } else if (reason?.IndexOf("disconnect", StringComparison.OrdinalIgnoreCase) >= 0) {
                    // Ngắt cổng do MLAstro: notification sẽ do kênh State(false) hiển thị (1 lần).
                    message = "Disconnected by MLAstro plugin - TPPA session closed.";
                    disconnectNotified = true;
                } else {
                    message = $"Stopped by MLAstro plugin ({reason}).";
                }
                Notification.ShowWarning(message);
            } catch (Exception ex) {
                Logger.Error($"[MLAstroRPA] Notification failed: {ex.Message}");
            }
        }

        public bool IsOpen => open && link.IsConnected;
        public string NewLine { get; set; } = "\n";
        public int ReadTimeout { get; set; } = 1000;
        public int WriteTimeout { get; set; } = 1000;

        public int BytesToRead {
            get {
                int n = 0;
                foreach (var s in rxQueue) n += s.Length;
                return n;
            }
        }

        /// <summary>Yêu cầu MLAstro mở cổng (auto-open cho cả MLAstro) rồi bắt đầu phiên chia sẻ.</summary>
        public void Open() {
            if (open) return;
            disconnectNotified = false;
            var ok = link.BeginExternalControl().GetAwaiter().GetResult();
            if (!ok || !link.IsConnected) {
                throw new Exception("Unable to open serial through MLAstro plugin.");
            }
            link.Subscribe(onLine, onState);
            link.SubscribeStop(onStop);
            open = true;
            Logger.Info("[MLAstroRPA] Shared serial session opened via MLAstro plugin (external control active).");
        }

        /// <summary>Kết thúc phiên: ngừng nhận, cho MLAstro poll lại và ĐÓNG cổng chung cho cả 2 phía.</summary>
        public void Close() {
            if (!open) return;
            open = false;
            disconnectNotified = false;
            try { link.Unsubscribe(onLine, onState); } catch { }
            try { link.UnsubscribeStop(onStop); } catch { }
            // THẢ quyền điều khiển, KHÔNG đóng cổng: MLAstro mở khoá UI & poll "?" trở lại.
            try { link.EndExternalControl(); } catch { }
            DiscardInBuffer();
            Logger.Info("[MLAstroRPA] Shared serial session closed (control released, port stays open).");
        }

        public void DiscardInBuffer() {
            while (rxQueue.TryDequeue(out _)) { }
        }

        public void WriteLine(string text) {
            link.Send(text + NewLine);
        }

        public string ReadLine() {
            if (rxQueue.TryDequeue(out var line0)) return line0;
            var deadline = Environment.TickCount + ReadTimeout;
            while (Environment.TickCount < deadline) {
                if (rxQueue.TryDequeue(out var line)) return line;
                Thread.Sleep(10);
            }
            throw new TimeoutException("ReadLine timed out (shared MLAstro session)");
        }

        public string ReadExisting() {
            var sb = new StringBuilder();
            while (rxQueue.TryDequeue(out var l)) { sb.Append(l).Append(NewLine); }
            return sb.ToString();
        }

        private void EnqueueLine(string line) => rxQueue.Enqueue(line);

        private void OnStateChanged(bool connected) {
            if (!connected) {
                open = false;
                Logger.Info("[MLAstroRPA] Shared session marked closed (MLAstro disconnected).");
                // Notification nguyên nhân DO MLAstro plugin (1 lần/phiên - tránh trùng với kênh STOP).
                if (!disconnectNotified) {
                    disconnectNotified = true;
                    try {
                        Notification.ShowWarning("Disconnected by MLAstro plugin - TPPA session closed.");
                    } catch (Exception ex) {
                        Logger.Error($"[MLAstroRPA] Notification failed: {ex.Message}");
                    }
                }
                // Dọn sạch listener để lần Connect sau không bị trùng/lọt sự kiện cũ.
                try { link.Unsubscribe(onLine, onState); } catch { }
                try { link.UnsubscribeStop(onStop); } catch { }
                DiscardInBuffer();
            }
        }

        public void Dispose() => Close();
    }
}

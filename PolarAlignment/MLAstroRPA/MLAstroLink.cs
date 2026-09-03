using NINA.Core.Utility;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment {
    /// <summary>
    /// Cầu nối runtime tới plugin MLAstro (chạy trong CÙNG process NINA, assembly riêng).
    /// MLAstro là CHỦ cổng COM duy nhất; TPPA ưu tiên dùng chung cổng qua MLAstro.
    /// Nếu plugin MLAstro chưa cài / chưa nạp thì <see cref="TryCreate"/> trả null
    /// và TPPA tự mở cổng riêng (fallback như trước đây).
    /// (Không reference chéo lúc biên dịch — dùng reflection theo tên assembly/type.)
    /// </summary>
    public sealed class MLAstroLink : IDisposable {
        private const string AssemblyName = "MLAstro_Robotic_Polar_Alignment";
        private const string TypeName = "MLAstro_Robotic_Polar_Alignment.Services.SerialConnectionService";

        private readonly object instance;
        private readonly PropertyInfo isConnectedProp;
        private readonly PropertyInfo comPortProp;
        private readonly MethodInfo ensureConnect;
        private readonly MethodInfo disconnect;
        private readonly MethodInfo send;
        private readonly MethodInfo addLine;
        private readonly MethodInfo removeLine;
        private readonly MethodInfo addState;
        private readonly MethodInfo removeState;
        private readonly MethodInfo setPause;
        private readonly MethodInfo beginControl;
        private readonly MethodInfo endControl;
        private readonly PropertyInfo isControlActiveProp;
        private readonly MethodInfo addStop;
        private readonly MethodInfo removeStop;

        private MLAstroLink(object instance) {
            this.instance = instance;
            var t = instance.GetType();
            isConnectedProp = t.GetProperty("IsConnected");
            comPortProp = t.GetProperty("ConfiguredComPort");
            ensureConnect = t.GetMethod("EnsureExternalConnectedAsync");
            disconnect = t.GetMethod("Disconnect");
            send = t.GetMethod("Send");
            addLine = t.GetMethod("AddExternalLineListener");
            removeLine = t.GetMethod("RemoveExternalLineListener");
            addState = t.GetMethod("AddExternalStateListener");
            removeState = t.GetMethod("RemoveExternalStateListener");
            setPause = t.GetMethod("SetExternalPauseQuery");
            beginControl = t.GetMethod("BeginExternalControlAsync");
            endControl = t.GetMethod("EndExternalControl");
            isControlActiveProp = t.GetProperty("IsExternalControlActive");
            addStop = t.GetMethod("AddExternalStopListener");
            removeStop = t.GetMethod("RemoveExternalStopListener");
        }

        /// <summary>Trả link tới MLAstro nếu plugin đang nạp; null nếu không (-> TPPA tự mở cổng).</summary>
        public static MLAstroLink TryCreate() {
            try {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, AssemblyName, StringComparison.OrdinalIgnoreCase));
                if (asm == null) return null;
                var type = asm.GetType(TypeName);
                if (type == null) return null;
                var instance = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (instance == null) return null;
                return new MLAstroLink(instance);
            } catch {
                return null;
            }
        }

        public bool IsConnected {
            get {
                try { return isConnectedProp != null && (bool)isConnectedProp.GetValue(instance); }
                catch { return false; }
            }
        }

        public string ConfiguredComPort {
            get {
                try { return comPortProp?.GetValue(instance) as string; }
                catch { return null; }
            }
        }

        /// <summary>Mở cổng qua MLAstro (auto-open cho cả MLAstro nếu chưa mở).</summary>
        public Task<bool> ConnectAsync() {
            if (ensureConnect == null) return Task.FromResult(false);
            try { return (Task<bool>)ensureConnect.Invoke(instance, null); }
            catch (Exception ex) { Logger.Error($"[MLAstroLink] Connect failed: {ex.Message}"); return Task.FromResult(false); }
        }

        public void Disconnect() {
            try { disconnect?.Invoke(instance, null); }
            catch (Exception ex) { Logger.Error($"[MLAstroLink] Disconnect failed: {ex.Message}"); }
        }

        /// <summary>Ghi một lệnh đã có ký tự xuống dòng (vd "...\n").</summary>
        public bool Send(string line) {
            if (send == null) return false;
            try { return (bool)send.Invoke(instance, new object[] { line }); }
            catch (Exception ex) { Logger.Error($"[MLAstroLink] Send failed: {ex.Message}"); return false; }
        }

        /// <summary>Tạm dừng poll "?" của MLAstro khi TPPA đang chủ động điều khiển.</summary>
        public void SetPauseQuery(bool pause) {
            try { setPause?.Invoke(instance, new object[] { pause }); }
            catch { }
        }

        public bool ExternalControlActive {
            get {
                try { return isControlActiveProp != null && (bool)isControlActiveProp.GetValue(instance); }
                catch { return false; }
            }
        }

        /// <summary>TPPA bắt đầu GIỮ quyền điều khiển (auto-open + khoá UI MLAstro).</summary>
        public Task<bool> BeginExternalControl() {
            if (beginControl == null) return Task.FromResult(false);
            try { return (Task<bool>)beginControl.Invoke(instance, null); }
            catch (Exception ex) { Logger.Error($"[MLAstroLink] BeginExternalControl failed: {ex.Message}"); return Task.FromResult(false); }
        }

        /// <summary>TPPA THẢ quyền (KHÔNG đóng cổng) - MLAstro mở khoá UI & poll lại.</summary>
        public void EndExternalControl() {
            try { endControl?.Invoke(instance, null); }
            catch (Exception ex) { Logger.Error($"[MLAstroLink] EndExternalControl failed: {ex.Message}"); }
        }

        public void SubscribeStop(Action<string> onStop) {
            try { if (onStop != null) addStop?.Invoke(instance, new object[] { onStop }); } catch { }
        }

        public void UnsubscribeStop(Action<string> onStop) {
            try { if (onStop != null) removeStop?.Invoke(instance, new object[] { onStop }); } catch { }
        }

        public void Subscribe(Action<string> onLine, Action<bool> onState) {
            try { if (onLine != null) addLine?.Invoke(instance, new object[] { onLine }); } catch { }
            try { if (onState != null) addState?.Invoke(instance, new object[] { onState }); } catch { }
        }

        public void Unsubscribe(Action<string> onLine, Action<bool> onState) {
            try { if (onLine != null) removeLine?.Invoke(instance, new object[] { onLine }); } catch { }
            try { if (onState != null) removeState?.Invoke(instance, new object[] { onState }); } catch { }
        }

        public void Dispose() => Disconnect();
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NINA.Plugins.PolarAlignment {
    public abstract partial class UniversalPolarAlignmentBaseVM : BaseVM, IPolarAlignmentSystemVM {
        protected IPolarAlignmentSystem upa;

        protected abstract IPolarAlignmentSystem CreateSystem();
        protected abstract string SystemName { get; }

        protected UniversalPolarAlignmentBaseVM(IProfileService profileService) : base(profileService) {
            IsNotMoving = true;
        }

        [ObservableProperty]
        private bool connected;

        [ObservableProperty]
        private float positionX;

        [ObservableProperty]
        private float positionY;

        [ObservableProperty]
        private float targetPositionX;

        [ObservableProperty]
        private float targetPositionY;

        public virtual string TestConnectStatus { get; protected set; } = string.Empty;
        public virtual NINA.Core.Utility.RelayCommand TestConnectCommand => null;

        public abstract bool DoAutomatedAdjustments { get; set; }
        public abstract double AutomatedAdjustmentSettleTime { get; set; }
        public abstract float XGearRatio { get; set; }
        public abstract int XSpeed { get; set; }
        public abstract float YGearRatio { get; set; }
        public abstract int YSpeed { get; set; }
        public abstract bool ReverseAzimuth { get; set; }
        public abstract bool ReverseAltitude { get; set; }
        public abstract float XBacklashCompensation { get; set; }

        /// <summary>
        /// When true, the automated correction loop is allowed to auto-reverse an axis
        /// when it detects the error is getting worse. When false (default), only the
        /// manual ReverseAzimuth / ReverseAltitude toggles determine the axis direction.
        /// </summary>
        public virtual bool EnableAutoReverse { get => false; set { } }
        public bool ManualReverseEnabled => !EnableAutoReverse;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(NudgeXCommand))]
        [NotifyCanExecuteChangedFor(nameof(NudgeYCommand))]
        [NotifyCanExecuteChangedFor(nameof(MoveXCommand))]
        [NotifyCanExecuteChangedFor(nameof(MoveYCommand))]
        private bool isNotMoving;

        private CancellationTokenSource pollCts;

        [RelayCommand]
        public Task Connect() {
            if (upa?.Connected == true) { return Task.CompletedTask; }
            return Task.Run(async () => {
                try {
                    await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = true);

                    upa = CreateSystem();
                    _ = StartPoll();
                    Connected = true;
                    Notification.ShowInformation($"Successfully connected to {SystemName}");
                } catch (Exception ex) {
                    Logger.Error(ex);
                    Notification.ShowError($"Unable to connect to {SystemName}");
                }
            });
        }

        [RelayCommand]
        public void Disconnect() {
            // QUAN TRỌNG: không return sớm khi upa.Connected == false. Khi MLAstro đóng cổng,
            // link đã mất nên upa.Connected = false - nhưng VM vẫn phải set Connected=false,
            // dispose hệ thống và cho phép Connect tạo lại lần sau.
            if (upa == null) { return; }
            var wasConnected = upa.Connected;
            Connected = false;
            try {
                // Dừng routine PA đang chạy (nếu có) trước khi thả quyền điều khiển.
                try { PolarAlignmentPlugin.RequestStopFromExternal($"TPPA disconnect ({SystemName})"); } catch (Exception) { }
                pollCts?.Cancel();
                try { upa.Dispose(); } catch (Exception ex) { Logger.Error(ex); }
            } catch (Exception ex) {
                Logger.Error(ex);
            } finally {
                upa = null; // để Connect tạo lại hệ thống mới lần sau
            }
            // Chỉ báo "Disconnected" khi là thao tác ngắt CHỦ ĐỘNG lúc link còn sống (wasConnected == true).
            // Nếu link đã mất do MLAstro plugin ngắt (wasConnected == false) thì SharedMlastroSerial đã
            // hiện notification nêu rõ nguyên nhân "do MLAstro plugin" - không hiện toast trùng nữa.
            if (wasConnected) {
                Notification.ShowInformation($"Disconnected from {SystemName}");
            }
        }

        [RelayCommand(CanExecute = (nameof(IsNotMoving)))]
        public async Task NudgeX(float position, CancellationToken token) {
            try {
                if (!EnableAutoReverse && ReverseAzimuth) { position = position * -1; }
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = false);

                Logger.Info($"Nudging {SystemName} along X axis by {position}");
                var lastDirection = upa.XLastDirection;
                await upa.MoveRelative(Axis.XAxis, XSpeed, position, token).ConfigureAwait(false);
                var currentDirection = upa.XLastDirection;
                await ClearBacklash(lastDirection, currentDirection, token);
            } catch (Exception ex) {
                Logger.Error(ex);
                if (ex is TimeoutException) {
                    Notification.ShowError($"Movement timeout: {ex.Message}");
                }
            } finally {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = true);
            }
        }

        [RelayCommand(CanExecute = (nameof(IsNotMoving)))]
        public async Task NudgeY(float position, CancellationToken token) {
            try {
                if (!EnableAutoReverse && ReverseAltitude) { position = position * -1; }
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = false);

                Logger.Info($"Nudging {SystemName} along Y axis by {position}");
                await upa.MoveRelative(Axis.YAxis, YSpeed, position, token).ConfigureAwait(false);
            } catch (Exception ex) {
                Logger.Error(ex);
                if (ex is TimeoutException) {
                    Notification.ShowError($"Movement timeout: {ex.Message}");
                }
            } finally {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = true);
            }
        }

        public new void RaiseAllPropertiesChanged() {
            base.RaiseAllPropertiesChanged();
        }

        [RelayCommand(CanExecute = (nameof(IsNotMoving)))]
        public async Task MoveX(CancellationToken token) {
            try {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = false);

                var target = TargetPositionX;
                if (!EnableAutoReverse && ReverseAzimuth) { target = target * -1; }

                Logger.Info($"Moving {SystemName} along X axis to {target}");
                var lastDirection = upa.XLastDirection;

                await upa.MoveAbsolute(Axis.XAxis, XSpeed, target, token).ConfigureAwait(false);
                var currentDirection = upa.XLastDirection;
                await ClearBacklash(lastDirection, currentDirection, token);
            } catch (Exception ex) {
                Logger.Error(ex);
                if (ex is TimeoutException) {
                    Notification.ShowError($"Movement timeout: {ex.Message}");
                }
            } finally {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = true);
            }
        }

        private async Task ClearBacklash(LastDirection lastDirection, LastDirection currentDirection, CancellationToken token) {
            if (lastDirection != currentDirection) {
                if (Math.Abs(XBacklashCompensation) > 0) {
                    Logger.Info("Direction changed. Clearing backlash");
                    await upa.MoveRelative(Axis.XAxis, XSpeed, -XBacklashCompensation, token).ConfigureAwait(false);
                    await upa.MoveRelative(Axis.XAxis, XSpeed, XBacklashCompensation, token).ConfigureAwait(false);
                }
            }
        }

        [RelayCommand(CanExecute = (nameof(IsNotMoving)))]
        public async Task MoveY(CancellationToken token) {
            try {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = false);

                var target = TargetPositionY;
                if (!EnableAutoReverse && ReverseAltitude) { target = target * -1; }

                Logger.Info($"Moving {SystemName} along Y axis to {target}");
                await upa.MoveAbsolute(Axis.YAxis, YSpeed, target, token).ConfigureAwait(false);
            } catch (Exception ex) {
                Logger.Error(ex);
                if (ex is TimeoutException) {
                    Notification.ShowError($"Movement timeout: {ex.Message}");
                }
            } finally {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = true);
            }
        }

        [RelayCommand]
        public virtual async Task Abort(CancellationToken token) {
            try {
                if (upa?.Connected == true) {
                    await upa.Abort(token).ConfigureAwait(false);
                }
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        private async Task StartPoll() {
            pollCts = new CancellationTokenSource();
            var token = pollCts.Token;
            var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(300));
            try {
                while (await timer.WaitForNextTickAsync(token) && !token.IsCancellationRequested) {
                    bool lost = false;
                    try {
                        await upa.RefreshStatus(token);
                    } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                        break;
                    } catch {
                        lost = true; // mất kết nối trong lúc đọc (vd MLAstro đóng cổng)
                    }
                    if (lost || !upa.Connected) {
                        // Liên kết bị mất (vd MLAstro plugin Disconnect khi đang dùng chung cổng):
                        // TPPA phải tự Disconnect + dừng PA.
                        Logger.Info($"[{SystemName}] Connection lost - auto disconnecting.");
                        Application.Current?.Dispatcher?.BeginInvoke(new Action(Disconnect));
                        break;
                    }
                    PositionX = upa.XPosition1;
                    PositionY = upa.YPosition1;
                }
            } catch (OperationCanceledException) {
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }
    }
}

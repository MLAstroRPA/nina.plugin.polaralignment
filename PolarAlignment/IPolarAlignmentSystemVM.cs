using NINA.Core.Utility;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment {
    public interface IPolarAlignmentSystemVM {
        bool Connected { get; }
        bool DoAutomatedAdjustments { get; set; }
        double AutomatedAdjustmentSettleTime { get; set; }
        string TestConnectStatus { get; }
        NINA.Core.Utility.RelayCommand TestConnectCommand { get; }

        Task Connect();
        void Disconnect();
        Task NudgeX(float position, CancellationToken token);
        Task NudgeY(float position, CancellationToken token);
        Task Abort(CancellationToken token);
        void RaiseAllPropertiesChanged();
    }
}

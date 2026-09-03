using System;

namespace NINA.Plugins.PolarAlignment {
    /// <summary>
    /// Trừu tượng hoá đường truyền serial để TPPA có thể dùng một trong hai:
    ///  1) <see cref="LoggingSerialPort"/>  — TPPA tự mở cổng COM (fallback, không có MLAstro).
    ///  2) <see cref="SharedMlastroSerial"/> — dùng CHUNG cổng COM do plugin MLAstro (cùng process NINA) làm chủ.
    /// </summary>
    public interface ISerialLink : IDisposable {
        string NewLine { get; set; }
        int ReadTimeout { get; set; }
        int WriteTimeout { get; set; }
        bool IsOpen { get; }
        int BytesToRead { get; }
        void Open();
        void Close();
        void DiscardInBuffer();
        void WriteLine(string text);
        string ReadLine();
        string ReadExisting();
    }
}

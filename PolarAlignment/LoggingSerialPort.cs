using NINA.Core.Utility;
using System;
using System.IO.Ports;

namespace NINA.Plugins.PolarAlignment {
    /// <summary>
    /// Wraps <see cref="SerialPort"/> and logs every byte/line written and read so
    /// all serial traffic can be diagnosed from the NINA log.
    /// </summary>
    public sealed class LoggingSerialPort : ISerialLink {
        private readonly SerialPort inner;

        public LoggingSerialPort() {
            inner = new SerialPort();
        }

        public LoggingSerialPort(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits) {
            inner = new SerialPort(portName, baudRate, parity, dataBits, stopBits);
        }

        public string PortName { get => inner.PortName; set => inner.PortName = value; }
        public int BaudRate { get => inner.BaudRate; set => inner.BaudRate = value; }
        public Parity Parity { get => inner.Parity; set => inner.Parity = value; }
        public int DataBits { get => inner.DataBits; set => inner.DataBits = value; }
        public StopBits StopBits { get => inner.StopBits; set => inner.StopBits = value; }
        public string NewLine { get => inner.NewLine; set => inner.NewLine = value; }
        public int ReadTimeout { get => inner.ReadTimeout; set => inner.ReadTimeout = value; }
        public int WriteTimeout { get => inner.WriteTimeout; set => inner.WriteTimeout = value; }
        public Handshake Handshake { get => inner.Handshake; set => inner.Handshake = value; }
        public bool DtrEnable { get => inner.DtrEnable; set => inner.DtrEnable = value; }
        public bool RtsEnable { get => inner.RtsEnable; set => inner.RtsEnable = value; }
        public bool IsOpen => inner.IsOpen;
        public int BytesToRead => inner.BytesToRead;

        public void Open() {
            inner.Open();
            Logger.Info($"[Serial] Opened {inner.PortName} ({inner.BaudRate} baud)");
        }

        public void Close() {
            Logger.Info($"[Serial] Closed {inner.PortName}");
            inner.Close();
        }

        public void Dispose() {
            Logger.Info($"[Serial] Disposed {inner.PortName}");
            inner.Dispose();
        }

        public void DiscardInBuffer() {
            Logger.Info($"[Serial] DiscardInBuffer {inner.PortName} ({inner.BytesToRead} bytes discarded)");
            inner.DiscardInBuffer();
        }

        public void WriteLine(string text) {
            try {
                inner.WriteLine(text);
            } catch (Exception ex) {
                Logger.Error($"[Serial-TX] {inner.PortName}: write failed: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        public string ReadLine() {
            try {
                return inner.ReadLine();
            } catch (Exception ex) {
                Logger.Info($"[Serial-RX] {inner.PortName}: read failed: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        public string ReadExisting() {
            try {
                return inner.ReadExisting();
            } catch (Exception ex) {
                Logger.Info($"[Serial-RX] {inner.PortName}: read failed: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }
    }
}

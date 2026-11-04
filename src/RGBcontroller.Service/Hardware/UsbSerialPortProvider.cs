using System.IO.Ports;
using Microsoft.Win32;
using RGBcontroller.Core.Config;
using RGBcontroller.Core.Strip;

namespace RGBcontroller.Service.Hardware;

/// <summary>
/// Finds the strip's COM port by USB VID/PID in the device registry (cheap, works as LocalSystem, no WMI),
/// keeping only ports that are present right now, or uses the configured override.
/// </summary>
internal sealed class UsbSerialPortProvider(StripConfig config) : IStripPortProvider
{
    public string? FindPort()
    {
        var present = SerialPort.GetPortNames();
        if (config.PortName is { } fixedPort)
        {
            return present.FirstOrDefault(p => string.Equals(p, fixedPort, StringComparison.OrdinalIgnoreCase));
        }

        var key = $@"SYSTEM\CurrentControlSet\Enum\USB\VID_{config.UsbVendorId.ToUpperInvariant()}&PID_{config.UsbProductId.ToUpperInvariant()}";
        using var usb = Registry.LocalMachine.OpenSubKey(key);
        if (usb is null)
        {
            return null;
        }

        foreach (var instance in usb.GetSubKeyNames())
        {
            using var parameters = usb.OpenSubKey($@"{instance}\Device Parameters");
            if (parameters?.GetValue("PortName") is string name
                && present.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }

    public IStripTransport Open(string portName) => new SerialStripTransport(portName, config.BaudRate);
}

/// <summary>8N1 serial connection; DTR/RTS are irrelevant for the CH340 board (verified).</summary>
internal sealed class SerialStripTransport : IStripTransport
{
    private readonly SerialPort _port;

    public SerialStripTransport(string portName, int baudRate)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            WriteTimeout = 1000,
            ReadTimeout = 500,
        };
        _port.Open();
        PortName = portName;
    }

    public string PortName { get; }

    public void Write(ReadOnlySpan<byte> frame) => _port.BaseStream.Write(frame);

    public void Flush() => _port.BaseStream.Flush();

    public int ReadAvailable(Span<byte> buffer)
    {
        var available = _port.BytesToRead;
        return available == 0 ? 0 : _port.BaseStream.Read(buffer[..Math.Min(available, buffer.Length)]);
    }

    public void Dispose() => _port.Dispose();
}

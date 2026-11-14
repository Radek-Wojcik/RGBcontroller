using RGBcontroller.Core.Ambilight;
using RGBcontroller.Service.Hardware;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RGBcontroller.Agent.Capture;

/// <summary>Receives a mapped, downscaled desktop frame. The image is valid only during the call.</summary>
internal interface IScreenImageSink
{
    void OnImage(in ScreenImage image);
}

internal enum CaptureOutcome
{
    /// <summary>A new desktop image was delivered to the sink.</summary>
    Frame,

    /// <summary>Nothing changed on the screen within the timeout (or only the mouse pointer moved).</summary>
    NoChange,

    /// <summary>
    /// The duplication is gone (DXGI_ERROR_ACCESS_LOST: mode change, secure desktop such as UAC or the lock screen,
    /// exclusive fullscreen) and must be re-created.
    /// </summary>
    Lost,
}

/// <summary>
/// DXGI Desktop Duplication of one monitor with the downscale on the GPU (SPEC 6.2): each new desktop texture is
/// copied into a mip-mapped texture, the GPU generates the mips (box filter averages), and only the mip level about
/// 480 px wide is copied to a staging texture and read by the CPU. A 4K frame never reaches the CPU. With an HDR
/// desktop the frames come as scRGB half floats (DuplicateOutput1); the pipeline tone-maps them.
/// </summary>
internal sealed class DesktopDuplicator : IDisposable
{
    private static readonly Format[] SupportedFormats = [Format.R16G16B16A16_Float, Format.B8G8R8A8_UNorm];

    private const int MaxMapAttempts = 100;
    private static readonly TimeSpan MapRetryInterval = TimeSpan.FromMilliseconds(0.5);

    /// <summary>After the first short retries the wait for the GPU polls this slowly, to cost no CPU while it is busy.</summary>
    private static readonly TimeSpan SlowMapRetryInterval = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// The longest a frame waits for the GPU to finish its copy. A GPU that never answers (a driver reset, a game hogging
    /// it) used to block the capture thread inside the driver for good; now the frame is skipped.
    /// </summary>
    private static readonly TimeSpan MapDeadline = TimeSpan.FromSeconds(1);
    private int _mapTimeouts;
    private readonly HighResolutionTimer _mapWait = new();
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly int _minimumSampleWidth;
    private ID3D11Texture2D? _mips;
    private ID3D11ShaderResourceView? _mipsView;
    private ID3D11Texture2D? _staging;
    private Format _format;
    private int _sourceWidth;
    private int _sourceHeight;
    private int _mipLevel;

    private DesktopDuplicator(
        ID3D11Device device, ID3D11DeviceContext context, IDXGIOutputDuplication duplication, int minimumSampleWidth, MonitorInfo monitor)
    {
        _device = device;
        _context = context;
        _duplication = duplication;
        _minimumSampleWidth = minimumSampleWidth;
        Monitor = monitor;
    }

    public MonitorInfo Monitor { get; }

    /// <summary>Format of the last frame (half floats when the desktop is HDR).</summary>
    public ScreenPixelFormat PixelFormat => _format == Format.R16G16B16A16_Float ? ScreenPixelFormat.Rgba16Float : ScreenPixelFormat.Bgra8;

    public (int Width, int Height) SampleSize => (Math.Max(1, _sourceWidth >> _mipLevel), Math.Max(1, _sourceHeight >> _mipLevel));

    /// <summary>Opens the duplication of <paramref name="monitorName"/> (\\.\DISPLAYn) or of the primary monitor.</summary>
    /// <exception cref="SharpGenException">The output cannot be duplicated right now (secure desktop, no session, driver).</exception>
    /// <exception cref="InvalidOperationException">No such monitor is attached.</exception>
    public static DesktopDuplicator Open(string? monitorName, int minimumSampleWidth)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var description = output.Description;
                        var bounds = description.DesktopCoordinates;
                        var wanted = monitorName is null
                            ? bounds.Left == 0 && bounds.Top == 0
                            : string.Equals(description.DeviceName, monitorName, StringComparison.OrdinalIgnoreCase);
                        if (description.AttachedToDesktop && wanted)
                        {
                            return Open(adapter, output, minimumSampleWidth);
                        }
                    }
                }
            }
        }

        throw new InvalidOperationException($"Monitor {monitorName ?? "(primary)"} is not attached to the desktop.");
    }

    private static DesktopDuplicator Open(IDXGIAdapter1 adapter, IDXGIOutput output, int minimumSampleWidth)
    {
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0],
            out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
        try
        {
            var description = output.Description;
            var hdr = false;
            IDXGIOutputDuplication duplication;
            using (var output6 = output.QueryInterfaceOrNull<IDXGIOutput6>())
            {
                if (output6 is not null)
                {
                    hdr = output6.Description1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
                    duplication = DuplicateOutput1(output6, device!);
                }
                else
                {
                    using var output1 = output.QueryInterface<IDXGIOutput1>();
                    duplication = output1.DuplicateOutput(device!);
                }
            }

            var bounds = description.DesktopCoordinates;
            var monitor = new MonitorInfo(description.DeviceName, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, hdr,
                description.Rotation is ModeRotation.Identity or ModeRotation.Unspecified);
            return new DesktopDuplicator(device!, context!, duplication, minimumSampleWidth, monitor);
        }
        catch
        {
            context?.Dispose();
            device?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// IDXGIOutput5::DuplicateOutput1 called through the COM vtable: the Vortice 3.8.3 wrapper crashes the process with
    /// an access violation (spike R7). Slot 26: IUnknown 3, IDXGIObject 4, IDXGIOutput 12, IDXGIOutput1 4, IDXGIOutput2-4 1 each.
    /// </summary>
    private static unsafe IDXGIOutputDuplication DuplicateOutput1(IDXGIOutput5 output, ID3D11Device device)
    {
        const int Slot = 26;
        var vtable = *(void***)output.NativePointer;
        var duplicateOutput1 = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, Format*, IntPtr*, int>)vtable[Slot];
        IntPtr duplication;
        fixed (Format* formats = SupportedFormats)
        {
            new Result(duplicateOutput1(output.NativePointer, device.NativePointer, 0, (uint)SupportedFormats.Length, formats, &duplication)).CheckError();
        }

        return new IDXGIOutputDuplication(duplication);
    }

    /// <summary>Waits up to <paramref name="timeoutMilliseconds"/> for a new desktop image and hands it to <paramref name="sink"/>.</summary>
    /// <exception cref="SharpGenException">An unexpected DXGI or D3D11 failure (the caller re-creates the duplicator).</exception>
    /// <summary>How many frames were skipped because the GPU did not finish the copy within <see cref="MapDeadline"/>.</summary>
    public int MapTimeouts => Volatile.Read(ref _mapTimeouts);

    public CaptureOutcome TryCapture(uint timeoutMilliseconds, IScreenImageSink sink)
    {
        var result = _duplication.AcquireNextFrame(timeoutMilliseconds, out var info, out var resource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            return CaptureOutcome.NoChange;
        }

        if (result == Vortice.DXGI.ResultCode.AccessLost)
        {
            return CaptureOutcome.Lost;
        }

        result.CheckError();
        try
        {
            if (info.LastPresentTime == 0)
            {
                // Only the pointer moved; the desktop image is unchanged.
                return CaptureOutcome.NoChange;
            }

            using var texture = resource!.QueryInterface<ID3D11Texture2D>();
            EnsureResources(texture.Description);
            _context.CopySubresourceRegion(_mips!, 0, 0, 0, 0, texture, 0);
        }
        finally
        {
            resource?.Dispose();
            _duplication.ReleaseFrame();
        }

        _context.GenerateMips(_mipsView!);
        _context.CopySubresourceRegion(_staging!, 0, 0, 0, 0, _mips!, (uint)_mipLevel);
        if (MapWhenReady() is not { } mapped)
        {
            Interlocked.Increment(ref _mapTimeouts);
            return CaptureOutcome.NoChange;
        }

        try
        {
            var (width, height) = SampleSize;
            unsafe
            {
                var data = new ReadOnlySpan<byte>((void*)mapped.DataPointer, (int)(mapped.RowPitch * height));
                sink.OnImage(new ScreenImage(data, width, height, (int)mapped.RowPitch, PixelFormat));
            }
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }

        return CaptureOutcome.Frame;
    }

    /// <summary>
    /// Maps the staging texture once the GPU has finished the copy. A plain Map blocks by spinning inside the driver
    /// (spike R7: about 2 ms of CPU per frame on the owner's GPU, more than the whole pipeline), so the copy is flushed
    /// and the map retried with DO_NOT_WAIT after short sleeps on a high-resolution timer. A GPU busy with a game can
    /// take longer than the first retries; the polling then slows down, and after <see cref="MapDeadline"/> the frame is skipped.
    /// </summary>
    private MappedSubresource? MapWhenReady()
    {
        _context.Flush();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var attempt = 0; ; attempt++)
        {
            var result = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out var mapped);
            if (result.Success)
            {
                return mapped;
            }

            if (result != Vortice.DXGI.ResultCode.WasStillDrawing)
            {
                result.CheckError();
            }

            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) >= MapDeadline)
            {
                return null;
            }

            _mapWait.Arm(attempt < MaxMapAttempts ? MapRetryInterval : SlowMapRetryInterval);
            _mapWait.WaitOne();
        }
    }

    public void Dispose()
    {
        _mapWait.Dispose();
        ReleaseResources();
        _duplication.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    /// <summary>(Re)creates the mip and staging textures when the desktop size or format changes (HDR toggled, mode change).</summary>
    private void EnsureResources(Texture2DDescription source)
    {
        if (_mips is not null && source.Format == _format && source.Width == _sourceWidth && source.Height == _sourceHeight)
        {
            return;
        }

        ReleaseResources();
        _format = source.Format;
        _sourceWidth = (int)source.Width;
        _sourceHeight = (int)source.Height;
        _mipLevel = 0;
        while ((_sourceWidth >> (_mipLevel + 1)) >= _minimumSampleWidth && (_sourceHeight >> (_mipLevel + 1)) >= 1)
        {
            _mipLevel++;
        }

        _mips = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = source.Width,
            Height = source.Height,
            MipLevels = (uint)_mipLevel + 1,
            ArraySize = 1,
            Format = _format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            MiscFlags = ResourceOptionFlags.GenerateMips,
        });
        _mipsView = _device.CreateShaderResourceView(_mips);
        var (width, height) = SampleSize;
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = _format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
    }

    private void ReleaseResources()
    {
        _staging?.Dispose();
        _mipsView?.Dispose();
        _mips?.Dispose();
        _staging = null;
        _mipsView = null;
        _mips = null;
    }
}

/// <param name="Name">GDI device name, e.g. \\.\DISPLAY1.</param>
/// <param name="Unrotated">False for portrait or flipped monitors (not supported: the edges would be swapped).</param>
internal sealed record MonitorInfo(string Name, int Width, int Height, bool Hdr, bool Unrotated);

using System.Diagnostics;
using System.Runtime.InteropServices;
using ImperiumRDP.Agent.Capture;
using ImperiumRDP.Shared.Protocol;

namespace ImperiumRDP.Agent.Nvenc;

/// <summary>
/// Захват через DXGI Desktop Duplication + аппаратное кодирование H.264 (NVENC).
/// Тот же контракт, что у CaptureEngine (JPEG-тайлы). При сбое в рантайме
/// деградирует на внутренний JPEG-движок, не разрывая сессии.
/// </summary>
public sealed class NvencEngine : ICaptureSource
{
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    public const byte CodecId = 1;
    private const int BufferCount = 4;

    private readonly Action<string> _log;
    private readonly object _subLock = new();
    private List<Action<MsgType, byte[]>> _subs = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private volatile bool _running = true;
    private Thread _thread;

    private volatile int _fps = 60;
    private volatile int _qp = 16;               // 14..32 (меньше = лучше)
    private volatile bool _forceIdr = true;
    private volatile bool _gotAnyFrame;
    private volatile bool _lastHaveFrame;
    private long _frameCounter;
    private int _pendingSlot;

    // D3D
    private ID3D11Device _device;
    private ID3D11DeviceContext _context;
    private IDXGIOutputDuplication _dupl;
    private readonly ID3D11Texture2D[] _inputTex = new ID3D11Texture2D[BufferCount];
    // сырые указатели (RCW-кэш GetObjectForIUnknown опасен: отсоединённый RCW → AV)
    private readonly IntPtr[] _inputPtr = new IntPtr[BufferCount];
    private int _width, _height, _vx, _vy;
    private string _adapterName = "";

    // NVENC
    private NvEncodeApiFunctionList _nv;
    private bool _nvLoaded;
    private IntPtr _encoder;
    private readonly IntPtr[] _bitstream = new IntPtr[BufferCount];
    private readonly IntPtr[] _registered = new IntPtr[BufferCount];
    private readonly IntPtr[] _mappedInput = new IntPtr[BufferCount];
    private NvEncInitializeParams _initParams;
    private NvEncConfig _cfg;
    private IntPtr _cfgNative;

    // курсор: форма передаётся viewer'у один раз при смене, позиция — с каждым кадром
    private byte[] _shapeBuf = new byte[256 * 1024];
    private DxgiOutduplPointerShapeInfo _shapeInfo;
    private int _cursorX, _cursorY;
    private bool _cursorVisible;
    private string _shapeHash = "";
    private byte[] _shapeBgra;                    // конвертированная форма (BGRA)
    private int _shapeW, _shapeH, _shapeHotX, _shapeHotY;

    private CaptureEngine _fallback;              // JPEG-движок при деградации
    private static readonly object _ctorLock = new();

    public int Codec => _fallback != null ? CaptureEngine.CodecId : CodecId;

    /// <summary>Полный виртуальный рабочий стол — для маппинга ввода.</summary>
    public Rectangle ScreenRect => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    private NvencEngine(Action<string> log) { _log = log; }

    /// <summary>null — NVENC недоступен (вызывающий использует JPEG-движок).</summary>
    public static NvencEngine TryCreate(Action<string> log)
    {
        lock (_ctorLock)
        {
            var e = new NvencEngine(log);
            try
            {
                e.CheckStructSizes();
                e.InitGraphics();
                e.InitNvenc();
                log($"NVENC: «{e._adapterName}», вывод {e._width}x{e._height}@({e._vx},{e._vy}), CQP={e._qp}, до {e._fps} FPS");
                return e;
            }
            catch (Exception ex)
            {
                log($"NVENC недоступен ({ex.Message}) — используется JPEG-захват");
                e.DisposeCore();
                return null;
            }
        }
    }

    /// <summary>Страховка: если раскладка структур не совпала с ожидаемой — не рискуем.</summary>
    private void CheckStructSizes()
    {
        int h264 = Marshal.SizeOf<NvEncConfigH264>();
        int cfg = Marshal.SizeOf<NvEncConfig>();
        int pic = Marshal.SizeOf<NvEncPicParams>();
        int init = Marshal.SizeOf<NvEncInitializeParams>();
        int tuningOff = (int)Marshal.OffsetOf<NvEncInitializeParams>(nameof(NvEncInitializeParams.tuningInfo));
        int hintOff = (int)Marshal.OffsetOf<NvEncInitializeParams>(nameof(NvEncInitializeParams.maxMEHintCountsPerBlock));
        if (h264 != 1792 || cfg != 3584 || pic != 3360 || init != 1800 || tuningOff != 136 || hintOff != 104)
            throw new Exception($"раскладка не совпала (h264={h264}, cfg={cfg}, pic={pic}, init={init}, tuning@{tuningOff}, hints@{hintOff})");
    }

    // ---------- инициализация ----------

    private void InitGraphics()
    {
        var factory = D3D.CreateFactory();
        int attachedTotal = 0;
        try
        {
            IDXGIAdapter1 chosen = null;
            IDXGIOutput chosenOutput = null;
            for (uint a = 0; ; a++)
            {
                if (factory.EnumAdapters1(a, out var adapter) != 0) break;
                bool keepAdapter = false;
                try
                {
                    for (uint o = 0; ; o++)
                    {
                        if (adapter.EnumOutputs(o, out var output) != 0) break;
                        try
                        {
                            output.GetDesc(out var desc);
                            if (desc.AttachedToDesktop != 0)
                            {
                                attachedTotal++;
                                bool isPrimary = desc.DesktopCoordinates.Left == 0 && desc.DesktopCoordinates.Top == 0;
                                if (chosenOutput == null || isPrimary)
                                {
                                    // приоритет: основной монитор (начало 0,0), иначе первый подключённый
                                    if (chosenOutput != null && !ReferenceEquals(chosenOutput, output))
                                        Marshal.ReleaseComObject(chosenOutput);
                                    chosenOutput = output;
                                    _vx = desc.DesktopCoordinates.Left;
                                    _vy = desc.DesktopCoordinates.Top;
                                    _width = desc.DesktopCoordinates.Right - _vx;
                                    _height = desc.DesktopCoordinates.Bottom - _vy;
                                    if (!keepAdapter) { chosen = adapter; keepAdapter = true; }
                                }
                            }
                        }
                        finally { if (!ReferenceEquals(chosenOutput, output)) Marshal.ReleaseComObject(output); }
                    }
                }
                finally { if (!keepAdapter) Marshal.ReleaseComObject(adapter); }
            }

            if (attachedTotal == 0 || chosenOutput == null) throw new Exception("нет подключённых мониторов");

            // TODO: захват всех мониторов — создать по duplication на каждый вывод и
            // собрать составной кадр (или кодировать несколько независимых H.264-потоков).
            // См. также фолбэк CaptureEngine (JPEG-тайлы), он поддерживает все мониторы уже сейчас.

            var desc1 = new DxgiAdapterDesc1();
            chosen.GetDesc1(out desc1);
            _adapterName = desc1.Description;

            var adapterPtr = Marshal.GetIUnknownForObject(chosen);
            try
            {
                // с явным адаптером DriverType обязан быть UNKNOWN (0); NVENC требует VIDEO_SUPPORT
                D3D.CreateDevice(adapterPtr, D3D.DriverTypeUnknown, IntPtr.Zero,
                    D3D.CreateDeviceBgraSupport | D3D.CreateDeviceVideoSupport,
                    IntPtr.Zero, 0, D3D.D3D11_SDK_VERSION, out _device, out _, out _context);
            }
            finally { Marshal.Release(adapterPtr); }
            Marshal.ReleaseComObject(chosenOutput);
            Marshal.ReleaseComObject(chosen);
            chosenOutput = null;
        }
        finally { Marshal.ReleaseComObject(factory); }

        if (attachedTotal > 1)
            _log($"NVENC: мониторов {attachedTotal} — H.264 идёт для основного {_width}x{_height}@({_vx},{_vy})");

        int w = _width & ~1, h = _height & ~1;
        if (w < 64 || h < 64) throw new Exception("слишком маленькое разрешение");
        _width = w; _height = h;

        var inputDesc = new D3D11Texture2DDesc
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = D3D.FormatB8G8R8A8Unorm, SampleCount = 1,
            Usage = D3D.UsageDefault, BindFlags = D3D.BindRenderTarget,
        };
        for (int i = 0; i < BufferCount; i++)
        {
            _device.CreateTexture2D(ref inputDesc, IntPtr.Zero, out _inputTex[i]);
            _inputPtr[i] = RawInterfacePtr(_inputTex[i], D3D.IidD3D11Texture2D);
        }

        DuplicateOutput();
    }

    private void DuplicateOutput()
    {
        if (_dupl != null) { try { _dupl.ReleaseFrame(); } catch { } Marshal.ReleaseComObject(_dupl); _dupl = null; }

        var factory = D3D.CreateFactory();
        try
        {
            factory.EnumAdapters1(0, out var adapter);
            try
            {
                adapter.EnumOutputs(0, out var output);
                try
                {
                    var output1 = (IDXGIOutput1)output; // QI через RCW
                    var devPtr = Marshal.GetIUnknownForObject(_device);
                    try
                    {
                        int hr = output1.DuplicateOutput(devPtr, out _dupl);
                        if (hr != 0) throw new Exception($"DuplicateOutput 0x{hr:X8}");
                    }
                    finally { Marshal.Release(devPtr); }
                }
                finally { Marshal.ReleaseComObject(output); }
            }
            finally { Marshal.ReleaseComObject(adapter); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    private void InitNvenc()
    {
        uint maxVer;
        int st = NvEncodeApi.NvEncodeAPIGetMaxSupportedVersion(out maxVer);
        if (st != 0) throw new Exception($"драйвер без NVENC (0x{st:X})");
        _log($"NVENC: драйвер поддерживает API {maxVer >> 4}.{maxVer & 0xF} (raw 0x{maxVer:X8})");
        NvEncodeApi.AdaptToDriver(maxVer);

        _nv = new NvEncodeApiFunctionList
        {
            version = NvEncodeApi.FunctionListVer,
            reserved1 = IntPtr.Zero,
            reserved2 = new IntPtr[275],
        };
        st = NvEncodeApi.NvEncodeAPICreateInstance(ref _nv);
        if (st != 0) throw new Exception($"CreateInstance 0x{st:X}");
        _nvLoaded = true;

        var open = new NvEncOpenEncodeSessionExParams
        {
            version = NvEncodeApi.OpenSessionExVer,
            deviceType = 0, // DIRECTX
            device = D3D11DevicePtr(),
            apiVersion = NvEncodeApi.NvEncApiVersion,
            reserved1 = new uint[253],
            reserved2 = new IntPtr[64],
        };
        try
        {
            st = _nv.nvEncOpenEncodeSessionEx(ref open, out _encoder);
            if (st != 0) throw new Exception($"OpenSession 0x{st:X}");
        }
        finally { Marshal.Release(open.device); }
        try
        {
            int gst = _nv.nvEncGetEncodeGUIDCount(_encoder, out uint gcount);
            _log($"NVENC probe GetEncodeGUIDCount: 0x{gst:X}, кодеков {gcount}");

            var hCodec0 = GCHandle.Alloc(NvEncodeApi.CodecH264, GCHandleType.Pinned);
            try
            {
                var caps = new NvEncCapsParam
                {
                    version = NvEncodeApi.CapsParamVer,
                    capsToQuery = 0, // NV_ENC_CAPS_NUM_MAX_BFRAMES
                    reserved = new uint[62],
                };
                int cst = _nv.nvEncGetEncodeCaps(_encoder, hCodec0.AddrOfPinnedObject(), ref caps, out int capsVal);
                _log($"NVENC probe GetEncodeCaps: 0x{cst:X}, значение {capsVal}");
            }
            finally { hCodec0.Free(); }
        }
        catch (Exception ex) { _log($"NVENC probe: {ex.Message}"); }

        _cfg = BuildConfig(_fps, _qp);
        _cfgNative = Marshal.AllocHGlobal(Marshal.SizeOf<NvEncConfig>());
        Marshal.StructureToPtr(_cfg, _cfgNative, false);

        _initParams = new NvEncInitializeParams
        {
            version = NvEncodeApi.InitializeParamsVer,
            encodeGUID = NvEncodeApi.CodecH264,
            presetGUID = NvEncodeApi.PresetP4,
            encodeWidth = (uint)_width,
            encodeHeight = (uint)_height,
            darWidth = (uint)_width,
            darHeight = (uint)_height,
            frameRateNum = (uint)_fps,
            frameRateDen = 1,
            enablePTD = 1,
            tuningInfo = NvEncodeApi.NV_ENC_TUNING_INFO_LOW_LATENCY,
            maxMEHintCountsPerBlock = new[] { new NvEncExternalMeHintCounts { reserved1 = new uint[3] }, new NvEncExternalMeHintCounts { reserved1 = new uint[3] } },
            reserved1 = new uint[284],
            reserved2 = new IntPtr[64],
            encodeConfig = _cfgNative,
        };

        st = _nv.nvEncInitializeEncoder(_encoder, ref _initParams);
        if (st != 0) throw new Exception($"InitializeEncoder 0x{st:X}");

        for (int i = 0; i < BufferCount; i++)
        {
            var bs = new NvEncCreateBitstreamBuffer
            {
                version = NvEncodeApi.CreateBitstreamVer,
                reserved1 = new uint[58],
                reserved2 = new IntPtr[64],
            };
            st = _nv.nvEncCreateBitstreamBuffer(_encoder, ref bs);
            if (st != 0) throw new Exception($"CreateBitstream 0x{st:X}");
            _bitstream[i] = bs.bitstreamBuffer;

            var reg = new NvEncRegisterResource
            {
                version = NvEncodeApi.RegisterResourceVer,
                resourceType = 0, // DIRECTX
                width = (uint)_width,
                height = (uint)_height,
                pitch = 0,
                subResourceIndex = 0,
                resourceToRegister = _inputPtr[i],
                bufferFormat = NvEncodeApi.NV_ENC_BUFFER_FORMAT_ARGB,
                bufferUsage = 0, // NV_ENC_INPUT_IMAGE
                chromaOffset = new uint[2],
                chromaOffsetIn = new uint[2],
                reserved1 = new uint[244],
                reserved2 = new IntPtr[61],
            };
            st = _nv.nvEncRegisterResource(_encoder, ref reg);
            if (st != 0) throw new Exception($"RegisterResource 0x{st:X}");
            _registered[i] = reg.registeredResource;

            var map = new NvEncMapInputResource
            {
                version = NvEncodeApi.MapInputResourceVer,
                registeredResource = _registered[i],
                reserved1 = new uint[251],
                reserved2 = new IntPtr[63],
            };
            st = _nv.nvEncMapInputResource(_encoder, ref map);
            if (st != 0) throw new Exception($"MapInput 0x{st:X}");
            _mappedInput[i] = map.mappedResource;
        }
    }

    /// <summary>Чистый нативный указатель интерфейса (без RCW-обёрток маршалера).</summary>
    private static IntPtr RawInterfacePtr(object comObject, Guid iid)
    {
        var unk = Marshal.GetIUnknownForObject(comObject);
        try
        {
            Guid g = iid;
            Marshal.QueryInterface(unk, ref g, out IntPtr ptr);
            return ptr;
        }
        finally { Marshal.Release(unk); }
    }

    /// <summary>Настоящий указатель интерфейса ID3D11Device (NVENC ожидает именно его, не IUnknown).</summary>
    private IntPtr D3D11DevicePtr()
    {
        var unk = Marshal.GetIUnknownForObject(_device);
        try
        {
            Guid iid = D3D.IidD3D11Device;
            Marshal.QueryInterface(unk, ref iid, out IntPtr dev);
            return dev;
        }
        finally { Marshal.Release(unk); }
    }

    /// <summary>Берём конфиг пресета у драйвера и переопределяем нужное (как в образцах NVIDIA).</summary>
    private NvEncConfig BuildConfig(int fps, int qp)
    {
        int st = -1;
        NvEncPresetConfig preset = default;

        var hCodec = GCHandle.Alloc(NvEncodeApi.CodecH264, GCHandleType.Pinned);
        var hPreset = GCHandle.Alloc(NvEncodeApi.PresetP4, GCHandleType.Pinned);
        try
        {
            // версия нужна и внешней, и вложенной структуре (как в FFmpeg)
            preset = new NvEncPresetConfig
            {
                version = NvEncodeApi.PresetConfigVer,
                presetCfg = new NvEncConfig { version = NvEncodeApi.ConfigVer },
                reserved1 = new uint[256],
                reserved2 = new IntPtr[64],
            };
            st = _nv.nvEncGetEncodePresetConfigEx(_encoder, hCodec.AddrOfPinnedObject(), hPreset.AddrOfPinnedObject(),
                NvEncodeApi.NV_ENC_TUNING_INFO_LOW_LATENCY, ref preset);
            _log($"NVENC GetPresetConfigEx(P4/low-latency): 0x{st:X}");
        }
        finally { hCodec.Free(); hPreset.Free(); }
        if (st != 0) throw new Exception($"GetPresetConfigEx 0x{st:X}");

        var cfg = preset.presetCfg;
        cfg.version = NvEncodeApi.ConfigVer;
        cfg.gopLength = NvEncodeApi.NVENC_INFINITE_GOPLENGTH;   // IDR только по нашей команде
        cfg.frameIntervalP = 1;                                 // без B-кадров
        cfg.frameFieldMode = NvEncodeApi.NV_ENC_PARAMS_FRAME_FIELD_MODE_FRAME;
        cfg.mvPrecision = NvEncodeApi.NV_ENC_MV_PRECISION_QUARTER_PEL;
        cfg.rcParams.version = NvEncodeApi.RcParamsVer;
        cfg.rcParams.rateControlMode = NvEncodeApi.NV_ENC_PARAMS_RC_CONSTQP;
        cfg.rcParams.constQP = new NvEncQp { qpInterP = (uint)qp, qpInterB = (uint)qp, qpIntra = (uint)qp };
        cfg.encodeCodecConfig.bitfields |= NvEncConfigH264.RepeatSpsPpsBit; // SPS/PPS в каждом IDR
        cfg.encodeCodecConfig.idrPeriod = NvEncodeApi.NVENC_INFINITE_GOPLENGTH;
        cfg.encodeCodecConfig.chromaFormatIDC = 1;
        cfg.encodeCodecConfig.outputBitDepth = 8;
        cfg.encodeCodecConfig.inputBitDepth = 8;
        cfg.encodeCodecConfig.h264VUIParameters.videoSignalTypePresentFlag = 1;
        cfg.encodeCodecConfig.h264VUIParameters.videoFormat = 5;      // UNSPECIFIED
        cfg.encodeCodecConfig.h264VUIParameters.videoFullRangeFlag = 0; // limited
        cfg.encodeCodecConfig.h264VUIParameters.colourDescriptionPresentFlag = 1;
        cfg.encodeCodecConfig.h264VUIParameters.colourPrimaries = 1;  // BT.709
        cfg.encodeCodecConfig.h264VUIParameters.transferCharacteristics = 1;
        cfg.encodeCodecConfig.h264VUIParameters.colourMatrix = 1;
        return cfg;
    }

    // ---------- публичный контракт (как у CaptureEngine) ----------

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "NvencEngine", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public IDisposable Subscribe(Action<MsgType, byte[]> handler)
    {
        if (_fallback != null) return _fallback.Subscribe(handler);
        lock (_subLock) { _subs = new List<Action<MsgType, byte[]>>(_subs) { handler }; }
        _forceIdr = true;
        _wake.Set();
        PublishScreenInfo();
        return new Unsub(this, handler);
    }

    private sealed class Unsub : IDisposable
    {
        private NvencEngine _e; private readonly Action<MsgType, byte[]> _h;
        public Unsub(NvencEngine e, Action<MsgType, byte[]> h) { _e = e; _h = h; }
        public void Dispose()
        {
            lock (_e._subLock) { var list = new List<Action<MsgType, byte[]>>(_e._subs); list.Remove(_h); _e._subs = list; }
            _e = null;
        }
    }

    public void SetOptions(int fps, int quality)
    {
        if (_fallback != null) { _fallback.SetOptions(fps, quality); return; }
        if (fps is >= 1 and <= 144) _fps = fps;
        int newQp = Math.Clamp(36 - quality / 3, 14, 32);
        if (newQp != _qp)
        {
            _qp = newQp;
            TryReconfigure(fps, newQp);
        }
    }

    public void RequestKeyframe()
    {
        if (_fallback != null) { _fallback.RequestKeyframe(); return; }
        _forceIdr = true;
        _wake.Set();
    }

    private void TryReconfigure(int fps, int qp)
    {
        try
        {
            _cfg = BuildConfig(fps, qp);
            Marshal.StructureToPtr(_cfg, _cfgNative, false);
            var reinit = _initParams;
            reinit.frameRateNum = (uint)fps;
            reinit.encodeConfig = _cfgNative;
            var rc = new NvEncReconfigureParams
            {
                version = NvEncodeApi.ReconfigureParamsVer,
                reInitEncodeParams = reinit,
                bitfields = 2, // forceIDR
            };
            int st = _nv.nvEncReconfigureEncoder(_encoder, ref rc);
            if (st != 0) _log($"NVENC reconfigure 0x{st:X}");
            _forceIdr = true;
        }
        catch (Exception ex) { _log($"NVENC reconfigure: {ex.Message}"); }
    }

    private void PublishScreenInfo()
    {
        var info = new MScreenInfo { Vx = _vx, Vy = _vy, Vw = _width, Vh = _height, Codec = CodecId };
        byte[] payload = info.Encode();
        List<Action<MsgType, byte[]>> subs;
        lock (_subLock) subs = _subs;
        foreach (var s in subs) { try { s(MsgType.ScreenInfo, payload); } catch { } }
    }

    // ---------- главный цикл ----------

    private void Loop()
    {
        long lastEncode = 0;

        while (_running)
        {
            try
            {
                if (Volatile.Read(ref _subs).Count == 0)
                {
                    _wake.Wait(300); _wake.Reset();
                    continue;
                }

                long frameIntervalTicks = Math.Max(Stopwatch.Frequency / Math.Max(1, _fps), 1000);
                int hr = _dupl.AcquireNextFrame(100, out var info, out IntPtr desktopRes);
                if (hr == D3D.DxgiErrorWaitTimeout)
                {
                    // рабочий стол статичен: ключевой кадр всё равно нужен новому зрителю
                    if (_forceIdr && _lastHaveFrame)
                    {
                        EncodeAndPublish();
                    }
                    continue;
                }
                if (hr == D3D.DxgiErrorAccessLost)
                {
                    Thread.Sleep(300);
                    DuplicateOutput();
                    _forceIdr = true;
                    continue;
                }
                if (hr != 0) throw new Exception($"AcquireNextFrame 0x{hr:X}");

                _gotAnyFrame = true;
                try
                {
                    if (desktopRes != IntPtr.Zero)
                    {
                        Guid iidTex = D3D.IidD3D11Texture2D;
                        int qi = Marshal.QueryInterface(desktopRes, ref iidTex, out IntPtr texPtr);
                        Marshal.Release(desktopRes);
                        if (qi == 0 && texPtr != IntPtr.Zero)
                        {
                            try
                            {
                                // GPU→GPU: кадр сразу в текстуру кодера, CPU не участвует.
                                // Слот выбирается здесь же; EncodeAndPublish кодирует последний записанный.
                                _pendingSlot = (int)(Interlocked.Increment(ref _frameCounter) % BufferCount);
                                _context.CopyResource(_inputPtr[_pendingSlot], texPtr);
                            }
                            finally { Marshal.Release(texPtr); }
                        }
                    }

                    UpdateCursor(info);

                    long now = Stopwatch.GetTimestamp();
                    bool due = now - lastEncode >= frameIntervalTicks || _forceIdr;
                    if (due && (_gotAnyFrame || _lastHaveFrame))
                    {
                        lastEncode = now;
                        EncodeAndPublish();
                        PublishCursorState();
                        _lastHaveFrame = true;
                    }
                }
                finally
                {
                    _dupl.ReleaseFrame();
                }
            }
            catch (Exception ex)
            {
                _log($"NVENC цикл: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                if (!RecoverOrFail()) return;
            }
        }
    }

    private void UpdateCursor(DxgiOutduplFrameInfo info)
    {
        _cursorVisible = info.PointerPosition.Visible != 0;
        _cursorX = info.PointerPosition.X - _vx;
        _cursorY = info.PointerPosition.Y - _vy;
        if (!_cursorVisible) return;

        try
        {
            var shape = _shapeInfo;
            var handle = GCHandle.Alloc(_shapeBuf, GCHandleType.Pinned);
            try
            {
                int hr = _dupl.GetFramePointerShape((uint)_shapeBuf.Length, handle.AddrOfPinnedObject(), out uint required, ref shape);
                if (hr == 0)
                {
                    if (required > (uint)_shapeBuf.Length) _shapeBuf = new byte[required];
                    _shapeInfo = shape;
                }
            }
            finally { handle.Free(); }
        }
        catch { /* форма курсора не критична */ }
    }

    /// <summary>Позиция курсора — маленькое сообщение с каждым кадром; рисует viewer.</summary>
    private void PublishCursorState()
    {
        PublishToSubs(MsgType.CursorState, new MCursorState
        {
            X = _cursorX,
            Y = _cursorY,
            Visible = _cursorVisible,
        }.Encode());

        // форма курсора меняется редко — шлём только при изменении
        var si = _shapeInfo;
        string hash = si.Type > 0 ? $"{si.Type}:{si.Width}x{si.Height}:{si.Pitch}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(_shapeBuf.AsSpan(0, (int)Math.Min((long)si.Pitch * si.Height, _shapeBuf.Length))))[..16]}" : "";
        if (hash != _shapeHash && si.Width > 0 && si.Height > 0)
        {
            _shapeHash = hash;
            ConvertShapeToBgra();
            if (_shapeBgra != null)
                PublishToSubs(MsgType.CursorShape, new MCursorShape
                {
                    Width = _shapeW,
                    Height = _shapeH,
                    HotX = _shapeHotX,
                    HotY = _shapeHotY,
                    Bgra = _shapeBgra,
                }.Encode());
        }
    }

    private void ConvertShapeToBgra()
    {
        var si = _shapeInfo;
        int w = (int)si.Width, h = (int)si.Height;
        if (w <= 0 || h <= 0 || w > 256 || h > 256) { _shapeBgra = null; return; }
        var bgra = new byte[w * h * 4];
        switch (si.Type)
        {
            case 1: // цветной ARGB — как есть
                for (int y = 0; y < h; y++)
                    Buffer.BlockCopy(_shapeBuf, y * (int)si.Pitch, bgra, y * w * 4, w * 4);
                break;
            case 4: // masked color: альфа 0xFF = прозрачный
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        long s = (long)y * si.Pitch + x * 4;
                        long d = ((long)y * w + x) * 4;
                        if (s + 3 >= _shapeBuf.Length) continue;
                        if (_shapeBuf[s + 3] == 0xFF) { bgra[d + 3] = 0; continue; }   // прозрачный
                        bgra[d] = _shapeBuf[s]; bgra[d + 1] = _shapeBuf[s + 1];
                        bgra[d + 2] = _shapeBuf[s + 2]; bgra[d + 3] = 255;
                    }
                break;
            case 2: // монохром AND/XOR → приближение: чёрный/белый/инверсия→белый/прозрачный
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        long andOff = y * (long)si.Pitch + (x >> 3);
                        long xorOff = (long)h * si.Pitch + andOff;
                        if (xorOff >= _shapeBuf.Length) continue;
                        int bit = 0x80 >> (x & 7);
                        bool a = (_shapeBuf[andOff] & bit) != 0;
                        bool x2 = (_shapeBuf[xorOff] & bit) != 0;
                        long d = ((long)y * w + x) * 4;
                        if (a && x2) { bgra[d + 3] = 0; }                       // прозрачный
                        else if (a) { bgra[d] = bgra[d + 1] = bgra[d + 2] = 0; bgra[d + 3] = 255; }
                        else { bgra[d] = bgra[d + 1] = bgra[d + 2] = 255; bgra[d + 3] = 255; }
                    }
                break;
            default:
                _shapeBgra = null;
                return;
        }
        _shapeBgra = bgra;
        _shapeW = w; _shapeH = h;
        _shapeHotX = si.HotSpotX; _shapeHotY = si.HotSpotY;
    }

    private void PublishToSubs(MsgType type, byte[] payload)
    {
        List<Action<MsgType, byte[]>> subs;
        lock (_subLock) subs = _subs;
        foreach (var s in subs) { try { s(type, payload); } catch { } }
    }

    private void EncodeAndPublish()
    {
        int idx = _pendingSlot;

        var pic = new NvEncPicParams
        {
            version = NvEncodeApi.PicParamsVer,
            inputWidth = (uint)_width,
            inputHeight = (uint)_height,
            inputPitch = (uint)_width,
            encodePicFlags = _forceIdr ? NvEncodeApi.NV_ENC_PIC_FLAG_FORCEIDR : 0,
            inputBuffer = _mappedInput[idx],
            outputBitstream = _bitstream[idx],
            bufferFmt = NvEncodeApi.NV_ENC_BUFFER_FORMAT_ARGB,
            pictureStruct = NvEncodeApi.NV_ENC_PIC_STRUCT_FRAME,
            codecPicParams = new NvEncPicParamsH264
            {
                timeCode = new uint[8],
                h264ExtPicParams = new uint[32],
                reserved = new uint[202],
                reserved2 = new IntPtr[61],
            },
            codecUnionPad = new uint[2],
            meHintCountsPerBlock = new[] { new NvEncExternalMeHintCounts { reserved1 = new uint[3] }, new NvEncExternalMeHintCounts { reserved1 = new uint[3] } },
            reserved2 = new uint[7],
            reserved5 = new IntPtr[2],
            meHintRefPicDist = new ushort[2],
            reserved3 = new uint[284],
            reserved6 = new IntPtr[57],
        };

        int st = _nv.nvEncEncodePicture(_encoder, ref pic);
        _forceIdr = false;
        if (st != 0) throw new Exception($"EncodePicture 0x{st:X}");

        var lockBs = new NvEncLockBitstream
        {
            version = NvEncodeApi.LockBitstreamVer,
            outputBitstream = _bitstream[idx],
            reserved1 = new uint[219],
            reserved2 = new IntPtr[63],
            reservedInternal = new uint[8],
        };
        st = _nv.nvEncLockBitstream(_encoder, ref lockBs);
        if (st != 0) throw new Exception($"LockBitstream 0x{st:X}");
        try
        {
            byte[] data = new byte[lockBs.bitstreamSizeInBytes];
            Marshal.Copy(lockBs.bitstreamBufferPtr, data, 0, data.Length);

            var msg = new MVideoData
            {
                Pts = Stopwatch.GetTimestamp() * 10_000_000L / Stopwatch.Frequency,
                Key = lockBs.pictureType is 2 or 3, // I или IDR
                Data = data,
            };
            byte[] payload = msg.Encode();
            List<Action<MsgType, byte[]>> subs;
            lock (_subLock) subs = _subs;
            foreach (var s in subs) { try { s(MsgType.H264Data, payload); } catch { } }
        }
        finally
        {
            _nv.nvEncUnlockBitstream(_encoder, _bitstream[idx]);
        }
    }

    // ---------- восстановление и деградация ----------

    private int _failStreak;

    private bool RecoverOrFail()
    {
        if (++_failStreak >= 5)
        {
            _log("NVENC: серия сбоев — переключаюсь на JPEG-захват");
            DegradeToJpeg();
            return false;
        }
        try
        {
            Thread.Sleep(500);
            DisposeNvenc();
            Thread.Sleep(200);
            InitNvenc();
            _forceIdr = true;
            _failStreak = 0;
            _log("NVENC: восстановлено после сбоя");
            return true;
        }
        catch (Exception ex2)
        {
            _log($"NVENC восстановление не удалось: {ex2.Message}");
            DegradeToJpeg();
            return false;
        }
    }

    private void DegradeToJpeg()
    {
        if (_fallback != null) return;
        DisposeNvenc();
        _fallback = new CaptureEngine(_log);
        _fallback.SetOptions(_fps, 60);
        _fallback.Start();
        List<Action<MsgType, byte[]>> subs;
        lock (_subLock) subs = _subs;
        foreach (var s in subs) _fallback.Subscribe(s);
        _log("Активен JPEG-режим (фолбэк)");
    }

    // ---------- освобождение ----------

    private void DisposeNvenc()
    {
        if (_encoder != IntPtr.Zero)
        {
            if (_nvLoaded)
            {
                foreach (var m in _mappedInput) { if (m != IntPtr.Zero) _nv.nvEncUnmapInputResource(_encoder, m); }
                foreach (var r in _registered) { if (r != IntPtr.Zero) _nv.nvEncUnregisterResource(_encoder, r); }
                try { _nv.nvEncDestroyEncoder(_encoder); } catch { }
            }
            _encoder = IntPtr.Zero;
            Array.Clear(_mappedInput); Array.Clear(_registered); Array.Clear(_bitstream);
        }
        if (_cfgNative != IntPtr.Zero) { Marshal.FreeHGlobal(_cfgNative); _cfgNative = IntPtr.Zero; }
    }

    private void DisposeCore()
    {
        _running = false;
        _wake.Set();
        try { _thread?.Join(1500); } catch { }
        DisposeNvenc();
        _fallback?.Dispose();
        if (_dupl != null) { try { _dupl.ReleaseFrame(); } catch { } Marshal.ReleaseComObject(_dupl); _dupl = null; }
        foreach (var p in _inputPtr) if (p != IntPtr.Zero) Marshal.Release(p);
        foreach (var t in _inputTex) if (t != null) Marshal.ReleaseComObject(t);
        if (_context != null) Marshal.ReleaseComObject(_context);
        if (_device != null) Marshal.ReleaseComObject(_device);
    }

    public void Dispose()
    {
        lock (_ctorLock) { DisposeCore(); }
    }
}

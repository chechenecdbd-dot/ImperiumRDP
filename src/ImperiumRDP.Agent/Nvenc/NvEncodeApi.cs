using System.Runtime.InteropServices;

namespace ImperiumRDP.Agent.Nvenc;

/// <summary>
/// Порт публичного заголовка NvEncodeAPI.h (nv-codec-headers, API 13.1) — только используемое подмножество.
/// Проверено по оригиналу: https://github.com/FFmpeg/nv-codec-headers
/// </summary>
internal static class NvEncodeApi
{
    /// <summary>Версия API, под которую собран порт структур.</summary>
    public const uint CompiledVersion = 13u | (1u << 24);

    /// <summary>Фактическая версия (адаптируется под драйвер в пределах мажора 13).</summary>
    public static uint NvEncApiVersion = CompiledVersion;

    public static uint StructVersion(uint ver) =>
        NvEncApiVersion | (ver << 16) | (0x7u << 28);

    public const uint NVENC_INFINITE_GOPLENGTH = 0xffffffff;

    // ---- версии структур ----
    public static uint FunctionListVer;
    public static uint OpenSessionExVer;
    public static uint CapsParamVer;
    public static uint CreateBitstreamVer;
    public static uint RcParamsVer;
    public static uint RegisterResourceVer;
    public static uint MapInputResourceVer;
    public static uint PresetConfigVer;
    public static uint InitializeParamsVer;
    public static uint ReconfigureParamsVer;
    public static uint ConfigVer;
    public static uint PicParamsVer;
    public static uint LockBitstreamVer;

    /// <summary>Адаптация версий структур под драйвер (формат GetMaxSupportedVersion: major&lt;&lt;4 | minor).</summary>
    public static void AdaptToDriver(uint driverMax)
    {
        uint driverMajor = driverMax >> 4;
        uint driverMinor = driverMax & 0xF;
        if (driverMajor < 13) throw new Exception($"NVENC драйвера {driverMajor}.{driverMinor} ниже 13.0 — обновите драйвер NVIDIA");
        // если драйвер новее нашего порта — используем свою версию (обратная совместимость NVENC)
        uint minor = Math.Min(driverMinor, 1u);
        NvEncApiVersion = 13u | (minor << 24);

        FunctionListVer = StructVersion(2);
        OpenSessionExVer = StructVersion(1);
        CapsParamVer = StructVersion(1);
        CreateBitstreamVer = StructVersion(1);
        RcParamsVer = StructVersion(1);
        RegisterResourceVer = StructVersion(5);
        MapInputResourceVer = StructVersion(4);
        PresetConfigVer = StructVersion(5) | (1u << 31);
        InitializeParamsVer = StructVersion(7) | (1u << 31);
        ReconfigureParamsVer = StructVersion(2) | (1u << 31);
        ConfigVer = StructVersion(9) | (1u << 31);
        PicParamsVer = StructVersion(7) | (1u << 31);
        LockBitstreamVer = StructVersion(2) | (1u << 31);
    }

    // ---- коды ошибок NVENCSTATUS ----
    public const int NV_ENC_SUCCESS = 0;
    public const int NV_ENC_ERR_NO_ENCODE_DEVICE = 1;
    public const int NV_ENC_ERR_INVALID_VERSION = 14;
    public const int NV_ENC_ERR_NEED_MORE_INPUT = 17;
    public const int NV_ENC_ERR_GENERIC = 21;

    // ---- флаги картинок ----
    public const uint NV_ENC_PIC_FLAG_FORCEIDR = 0x2;
    public const uint NV_ENC_PIC_FLAG_OUTPUT_SPSPPS = 0x4;

    // ---- GUID ----
    public static readonly Guid CodecH264 = new(0x6bc82762, 0x4e63, 0x4ca4, 0xaa, 0x85, 0x1e, 0x50, 0xf3, 0x21, 0xf6, 0xbf);
    public static readonly Guid ProfileHighH264 = new(0xe7cbc309, 0x4f7a, 0x4b89, 0xaf, 0x2a, 0xd5, 0x37, 0xc9, 0x2b, 0xe3, 0x10);
    public static readonly Guid PresetP4 = new(0x90a7b826, 0xdf06, 0x4862, 0xb9, 0xd2, 0xcd, 0x6d, 0x73, 0xa0, 0x86, 0x81);
    public static readonly Guid PresetP3 = new(0x36850110, 0x3a07, 0x441f, 0x94, 0xd5, 0x36, 0x70, 0x63, 0x1f, 0x91, 0xf6);
    public static readonly Guid PresetP5 = new(0x21c6e6b4, 0x297a, 0x4cba, 0x99, 0x8f, 0xb6, 0xcb, 0xde, 0x72, 0xad, 0xe3);

    public const int NV_ENC_TUNING_INFO_LOW_LATENCY = 2;
    public const int NV_ENC_PARAMS_RC_CONSTQP = 0x0;
    public const int NV_ENC_PARAMS_FRAME_FIELD_MODE_FRAME = 0x01;
    public const int NV_ENC_MV_PRECISION_QUARTER_PEL = 0x03;
    public const uint NV_ENC_BUFFER_FORMAT_ARGB = 0x01000000;
    public const int NV_ENC_PIC_STRUCT_FRAME = 0x01;

    // ---- загрузка nvEncodeAPI64.dll ----
    private const string DllName = "nvEncodeAPI64.dll";

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int NvEncGetMaxSupportedVersionDelegate(out uint version);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int NvEncodeApiCreateInstanceDelegate(ref NvEncodeApiFunctionList functionList);

    [DllImport(DllName, EntryPoint = "NvEncodeAPIGetMaxSupportedVersion", CallingConvention = CallingConvention.StdCall)]
    public static extern int NvEncodeAPIGetMaxSupportedVersion(out uint version);

    [DllImport(DllName, EntryPoint = "NvEncodeAPICreateInstance", CallingConvention = CallingConvention.StdCall)]
    public static extern int NvEncodeAPICreateInstance(ref NvEncodeApiFunctionList functionList);
}

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
public delegate int PNVENCSTATUS_VOID_PTR(IntPtr encoder);

/// <summary>Таблица функций NVENC — порядок полей критичен (порядок из заголовка).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NvEncodeApiFunctionList
{
    public uint version;
    public uint reserved;
    public IntPtr nvEncOpenEncodeSession;            // устаревший, не вызываем
    public PNVENCGETENCODEGUIDCOUNT nvEncGetEncodeGUIDCount;
    public IntPtr nvEncGetEncodeProfileGUIDCount;
    public IntPtr nvEncGetEncodeProfileGUIDs;
    public IntPtr nvEncGetEncodeGUIDs;
    public IntPtr nvEncGetInputFormatCount;
    public IntPtr nvEncGetInputFormats;
    public PNVENCGETENCODECAPS nvEncGetEncodeCaps;
    public IntPtr nvEncGetEncodePresetCount;
    public IntPtr nvEncGetEncodePresetGUIDs;
    public PNVENCGETENCODEPRESETCONFIG nvEncGetEncodePresetConfig;
    public PNVENCINITIALIZEENCODER nvEncInitializeEncoder;
    public IntPtr nvEncCreateInputBuffer;
    public IntPtr nvEncDestroyInputBuffer;
    public PNVENCCREATEBITSTREAMBUFFER nvEncCreateBitstreamBuffer;
    public IntPtr nvEncDestroyBitstreamBuffer;
    public PNVENCENCODEPICTURE nvEncEncodePicture;
    public PNVENCLOCKBITSTREAM nvEncLockBitstream;
    public PNVENCUNLOCKBITSTREAM nvEncUnlockBitstream;
    public IntPtr nvEncLockInputBuffer;
    public IntPtr nvEncUnlockInputBuffer;
    public IntPtr nvEncGetEncodeStats;
    public IntPtr nvEncGetSequenceParams;
    public IntPtr nvEncRegisterAsyncEvent;
    public IntPtr nvEncUnregisterAsyncEvent;
    public PNVENCMAPINPUTRESOURCE nvEncMapInputResource;
    public PNVENCUNMAPINPUTRESOURCE nvEncUnmapInputResource;
    public PNVENCDESTROYENCODER nvEncDestroyEncoder;
    public IntPtr nvEncInvalidateRefFrames;
    public PNVENCOPENENCODESESSIONEX nvEncOpenEncodeSessionEx;
    public PNVENCREGISTERRESOURCE nvEncRegisterResource;
    public PNVENCUNREGISTERRESOURCE nvEncUnregisterResource;
    public PNVENCRECONFIGUREENCODER nvEncReconfigureEncoder;
    public IntPtr reserved1;
    public IntPtr nvEncCreateMVBuffer;
    public IntPtr nvEncDestroyMVBuffer;
    public IntPtr nvEncRunMotionEstimationOnly;
    public PNVENCGETLASTERRORSTRING nvEncGetLastErrorString;
    public IntPtr nvEncSetIOCudaStreams;
    public PNVENCGETENCODEPRESETCONFIGEX nvEncGetEncodePresetConfigEx;
    public IntPtr nvEncGetSequenceParamEx;
    public IntPtr nvEncRestoreEncoderState;
    public IntPtr nvEncLookaheadPicture;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 275)]
    public IntPtr[] reserved2;
}

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
public delegate int PNVENCOPENENCODESESSIONEX(ref NvEncOpenEncodeSessionExParams p, out IntPtr encoder);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
public delegate int PNVENCINITIALIZEENCODER(IntPtr encoder, ref NvEncInitializeParams p);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
public delegate int PNVENCINITIALIZEENCODER_RAW(IntPtr encoder, IntPtr p);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
public delegate int PNVENCCREATEBITSTREAMBUFFER(IntPtr encoder, ref NvEncCreateBitstreamBuffer p);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
public delegate int PNVENCENCODEPICTURE(IntPtr encoder, ref NvEncPicParams p);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
public delegate int PNVENCLOCKBITSTREAM(IntPtr encoder, ref NvEncLockBitstream p);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
public delegate int PNVENCUNLOCKBITSTREAM(IntPtr encoder, IntPtr bitstreamBuffer);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
public delegate int PNVENCMAPINPUTRESOURCE(IntPtr encoder, ref NvEncMapInputResource p);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
public delegate int PNVENCREGISTERRESOURCE(IntPtr encoder, ref NvEncRegisterResource p);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
public delegate int PNVENCGETENCODEPRESETCONFIGEX(IntPtr encoder, IntPtr encodeGuid, IntPtr presetGuid, int tuningInfo, ref NvEncPresetConfig presetConfig);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
public delegate IntPtr PNVENCGETLASTERRORSTRING(IntPtr encoder);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
public delegate int PNVENCGETENCODEGUIDCOUNT(IntPtr encoder, out uint encodeGUIDCount);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
public delegate int PNVENCGETENCODEPRESETCONFIG(IntPtr encoder, IntPtr encodeGuid, IntPtr presetGuid, ref NvEncPresetConfig presetConfig);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
public delegate int PNVENCGETENCODECAPS(IntPtr encoder, IntPtr encodeGuid, ref NvEncCapsParam capsParam, out int capsVal);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
public delegate int PNVENCUNMAPINPUTRESOURCE(IntPtr encoder, IntPtr mappedInputBuffer);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
public delegate int PNVENCDESTROYENCODER(IntPtr encoder);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
public delegate int PNVENCUNREGISTERRESOURCE(IntPtr encoder, IntPtr registeredResource);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
public delegate int PNVENCRECONFIGUREENCODER(IntPtr encoder, ref NvEncReconfigureParams p);

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NvEncOpenEncodeSessionExParams
{
    public uint version;
    public int deviceType;          // NV_ENC_DEVICE_TYPE_DIRECTX = 0
    public IntPtr device;
    public IntPtr reserved;
    public uint apiVersion;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 253)]
    public uint[] reserved1;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public IntPtr[] reserved2;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NvEncCreateBitstreamBuffer
{
    public uint version;
    public uint size;
    public int memoryHeap;
    public uint reserved;
    public IntPtr bitstreamBuffer;      // [out]
    public IntPtr bitstreamBufferPtr;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 58)]
    public uint[] reserved1;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public IntPtr[] reserved2;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NvEncRegisterResource
{
    public uint version;
    public int resourceType;            // NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX = 0
    public uint width;
    public uint height;
    public uint pitch;                  // 0 для DirectX
    public uint subResourceIndex;
    public IntPtr resourceToRegister;   // ID3D11Texture2D
    public IntPtr registeredResource;   // [out]
    public uint bufferFormat;           // NV_ENC_BUFFER_FORMAT_ARGB
    public int bufferUsage;             // NV_ENC_INPUT_IMAGE = 0
    public IntPtr pInputFencePoint;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
    public uint[] chromaOffset;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
    public uint[] chromaOffsetIn;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 244)]
    public uint[] reserved1;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 61)]
    public IntPtr[] reserved2;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NvEncMapInputResource
{
    public uint version;
    public uint subResourceIndex;
    public IntPtr inputResource;
    public IntPtr registeredResource;
    public IntPtr mappedResource;       // [out] — inputBuffer для EncodePicture
    public uint mappedBufferFmt;        // [out]
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 251)]
    public uint[] reserved1;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 63)]
    public IntPtr[] reserved2;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NvEncQp
{
    public uint qpInterP;
    public uint qpInterB;
    public uint qpIntra;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NvEncRcParams
{
    public uint version;
    public int rateControlMode;
    public NvEncQp constQP;
    public uint averageBitRate;
    public uint maxBitRate;
    public uint vbvBufferSize;
    public uint vbvInitialDelay;
    public uint bitfields;              // все :1 флаги + aqStrength:4 + reserved:15 = 32 бита
    public NvEncQp minQP;
    public NvEncQp maxQP;
    public NvEncQp initialRCQP;
    public uint temporallayerIdxMask;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public byte[] temporalLayerQP;
    public byte targetQuality;
    public byte targetQualityLSB;
    public ushort lookaheadDepth;
    public byte lowDelayKeyFrameScale;
    public sbyte yDcQPIndexOffset;
    public sbyte uDcQPIndexOffset;
    public sbyte vDcQPIndexOffset;
    public int qpMapMode;               // NV_ENC_QP_MAP_DISABLED = 0
    public int multiPass;
    public uint alphaLayerBitrateRatio;
    public sbyte cbQPIndexOffset;
    public sbyte crQPIndexOffset;
    public ushort reserved2;
    public int lookaheadLevel;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 7)]
    public byte[] viewBitrateRatios;
    public byte reserved3;
    public uint reserved1;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NvEncConfigH264VuiParameters
{
    public uint overscanInfoPresentFlag;
    public uint overscanInfo;
    public uint videoSignalTypePresentFlag;
    public int videoFormat;             // 5 = UNSPECIFIED
    public uint videoFullRangeFlag;
    public uint colourDescriptionPresentFlag;
    public int colourPrimaries;         // 1 = BT709
    public int transferCharacteristics; // 1 = BT709
    public int colourMatrix;            // 1 = BT709
    public uint chromaSampleLocationFlag;
    public uint chromaSampleLocationTop;
    public uint chromaSampleLocationBot;
    public uint bitstreamRestrictionFlag;
    public uint timingInfoPresentFlag;
    public uint numUnitInTicks;
    public uint timeScale;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)]
    public uint[] reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NvEncConfigH264
{
    public uint bitfields;              // 22 однобитовых + reserved:10 = 32 бита (repeatSPSPPS = бит 12)
    public uint level;                  // 0 = AUTOSELECT
    public uint idrPeriod;
    public uint separateColourPlaneFlag;
    public uint disableDeblockingFilterIDC;
    public uint numTemporalLayers;
    public uint spsId;
    public uint ppsId;
    public int adaptiveTransformMode;   // 0 = AUTOSELECT
    public int fmoMode;                 // 0 = AUTOSELECT
    public int bdirectMode;             // 0 = AUTOSELECT
    public int entropyCodingMode;       // 0 = AUTOSELECT
    public int stereoMode;              // 0 = NONE
    public uint intraRefreshPeriod;
    public uint intraRefreshCnt;
    public uint maxNumRefFrames;
    public uint sliceMode;
    public uint sliceModeData;
    public NvEncConfigH264VuiParameters h264VUIParameters;
    public uint ltrNumFrames;
    public uint ltrTrustMode;
    public uint chromaFormatIDC;        // 1 = yuv420
    public uint maxTemporalLayers;
    public int useBFramesAsRef;         // 0 = DISABLED
    public int numRefL0;                // 0 = AUTOSELECT
    public int numRefL1;
    public int outputBitDepth;          // 8
    public int inputBitDepth;           // 8
    public int tfLevel;                 // 0
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 264)]
    public uint[] reserved1;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public IntPtr[] reserved2;

    public const uint RepeatSpsPpsBit = 1u << 12; // бит 12 (0-индексация) в первом слове
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NvEncConfig
{
    public uint version;
    public Guid profileGUID;
    public uint gopLength;
    public int frameIntervalP;
    public uint monoChromeEncoding;
    public int frameFieldMode;
    public int mvPrecision;
    public NvEncRcParams rcParams;
    public NvEncConfigH264 encodeCodecConfig; //union: H264 — максимальный член
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 278)]
    public uint[] reserved;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public IntPtr[] reserved2;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NvEncInitializeParams
{
    public uint version;
    public Guid encodeGUID;
    public Guid presetGUID;
    public uint encodeWidth;
    public uint encodeHeight;
    public uint darWidth;
    public uint darHeight;
    public uint frameRateNum;
    public uint frameRateDen;
    public uint enableEncodeAsync;      // 0
    public uint enablePTD;              // 1
    public uint bitfields;              // 12×:1 + splitEncodeMode:4 + reserved:19 = 32 бита
    public uint privDataSize;
    public uint reserved;
    public IntPtr privData;
    public IntPtr encodeConfig;         // → NvEncConfig
    public uint maxEncodeWidth;
    public uint maxEncodeHeight;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2, ArraySubType = UnmanagedType.Struct)]
    public NvEncExternalMeHintCounts[] maxMEHintCountsPerBlock;
    public int tuningInfo;
    public uint bufferFormat;
    public uint numStateBuffers;
    public int outputStatsLevel;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 284)]
    public uint[] reserved1;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public IntPtr[] reserved2;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NvEncExternalMeHintCounts
{
    public uint bitfields;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public uint[] reserved1;    // по заголовку: структура = 16 байт, не 4!
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NvEncReconfigureParams
{
    public uint version;
    public uint reserved;
    public NvEncInitializeParams reInitEncodeParams;
    public uint bitfields;              // resetEncoder:1 + forceIDR:1 + reserved:30
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NvEncPresetConfig
{
    public uint version;
    public uint reserved;
    public NvEncConfig presetCfg;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
    public uint[] reserved1;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public IntPtr[] reserved2;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NvEncPicParamsH264
{
    public uint displayPOCSyntax;
    public uint reserved3;
    public uint refPicFlag;
    public uint colourPlaneId;
    public uint forceIntraRefreshWithFrameCnt;
    public uint bitfields;              // constrainedFrame:1..reserved:28 = 32
    public IntPtr sliceTypeData;
    public uint sliceTypeArrayCnt;
    public uint seiPayloadArrayCnt;
    public IntPtr seiPayloadArray;
    public uint sliceMode;
    public uint sliceModeData;
    public uint ltrMarkFrameIdx;
    public uint ltrUseFrameBitmap;
    public uint ltrUsageMode;
    public uint forceIntraSliceCount;
    public IntPtr forceIntraSliceIdx;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public uint[] h264ExtPicParams;     // union NV_ENC_PIC_PARAMS_H264_EXT = 32 uint
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public uint[] timeCode;             // NV_ENC_TIME_CODE = 1 + 3×2 + 1 uint = 8 uint (32 байта)
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 202)]
    public uint[] reserved;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 61)]
    public IntPtr[] reserved2;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NvEncPicParams
{
    public uint version;
    public uint inputWidth;
    public uint inputHeight;
    public uint inputPitch;
    public uint encodePicFlags;
    public uint frameIdx;
    public ulong inputTimeStamp;
    public ulong inputDuration;
    public IntPtr inputBuffer;
    public IntPtr outputBitstream;
    public IntPtr completionEvent;
    public uint bufferFmt;              // NV_ENC_BUFFER_FORMAT_ARGB
    public int pictureStruct;           // FRAME
    public int pictureType;             // 0 (PTD решает сам)
    public NvEncPicParamsH264 codecPicParams; // union: H264 (1536 байт)
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
    public uint[] codecUnionPad;        // до размера union (AV1 = 1544 байта)
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2, ArraySubType = UnmanagedType.Struct)]
    public NvEncExternalMeHintCounts[] meHintCountsPerBlock;
    public IntPtr meExternalHints;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 7)]
    public uint[] reserved2;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
    public IntPtr[] reserved5;
    public IntPtr qpDeltaMap;
    public uint qpDeltaMapSize;
    public uint reservedBitFields;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
    public ushort[] meHintRefPicDist;
    public int diffPicNumHint;
    public IntPtr alphaBuffer;
    public IntPtr meExternalSbHints;
    public uint meSbHintsCount;
    public uint stateBufferIdx;
    public IntPtr outputReconBuffer;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 284)]
    public uint[] reserved3;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 57)]
    public IntPtr[] reserved6;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NvEncLockBitstream
{
    public uint version;
    public uint bitfields;              // doNotWait:1 + ltrFrame:1 + getRCStats:1 + reserved:29
    public IntPtr outputBitstream;
    public IntPtr sliceOffsets;
    public uint frameIdx;
    public uint hwEncodeStatus;
    public uint numSlices;
    public uint bitstreamSizeInBytes;
    public ulong outputTimeStamp;
    public ulong outputDuration;
    public IntPtr bitstreamBufferPtr;   // [out]
    public int pictureType;             // [out]
    public int pictureStruct;
    public uint frameAvgQP;
    public uint frameSatd;
    public uint ltrFrameIdx;
    public uint ltrFrameBitmap;
    public uint temporalId;
    public uint intraMBCount;
    public uint interMBCount;
    public int averageMVX;
    public int averageMVY;
    public uint alphaLayerSizeInBytes;
    public uint outputStatsPtrSize;
    public uint reserved;
    public IntPtr outputStatsPtr;
    public uint frameIdxDisplay;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 219)]
    public uint[] reserved1;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 63)]
    public IntPtr[] reserved2;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public uint[] reservedInternal;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NvEncCapsParam
{
    public uint version;
    public int capsToQuery;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 62)]
    public uint[] reserved;
}

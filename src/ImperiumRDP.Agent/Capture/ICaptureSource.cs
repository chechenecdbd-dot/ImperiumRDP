using System.Drawing;
using ImperiumRDP.Shared.Protocol;

namespace ImperiumRDP.Agent.Capture;

/// <summary>Общий контракт движков захвата (JPEG-тайлы / NVENC H.264).</summary>
public interface ICaptureSource : IDisposable
{
    int Codec { get; }                       // 0 = JPEG-тайлы, 1 = H.264
    Rectangle ScreenRect { get; }            // полный виртуальный рабочий стол (для ввода)
    void Start();
    IDisposable Subscribe(Action<MsgType, byte[]> handler);
    void SetOptions(int fps, int quality);
    void RequestKeyframe();
}

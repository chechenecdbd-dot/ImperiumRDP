@echo off
rem Building ImperiumRDP.msi: self-contained publish + WiX.
rem Required: .NET 8 SDK, WiX 5 (dotnet tool install --global wix --version 5.0.2),
rem            ffmpeg.exe in the PATH (or next to the script).
setlocal
cd /d "%~dp0"

echo [1/4] Publication (self-contained, single file)...
dotnet publish src\ImperiumRDP.Agent  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o publish\agent  || exit /b 1
dotnet publish src\ImperiumRDP.Viewer -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o publish\viewer || exit /b 1
dotnet publish src\ImperiumRDP.GUI    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o publish\app    || exit /b 1

echo [2/4] ffmpeg.exe...
if exist ffmpeg.exe (
    copy /y ffmpeg.exe publish\tools\ffmpeg.exe >nul 2>&1 || (mkdir publish\tools 2>nul & copy /y ffmpeg.exe publish\tools\ffmpeg.exe)
) else (
    for /f "delims=" %%F in ('where ffmpeg 2^>nul') do (
        if not exist publish\tools mkdir publish\tools
        copy /y "%%F" publish\tools\ffmpeg.exe >nul
        goto ffmpeg_done
    )
    echo ERROR: ffmpeg.exe not found — place it next to the script.
    exit /b 1
)
:ffmpeg_done

echo [3/4] Removing duplicates (the native DLLs and runtimeconfig are located in the same installation folder)...
del /q publish\app\*.runtimeconfig.json 2>nul
del /q publish\agent\D3DCompiler_47_cor3.dll 2>nul
del /q publish\agent\PenImc_cor3.dll 2>nul
del /q publish\agent\PresentationNative_cor3.dll 2>nul
del /q publish\agent\vcruntime140_cor3.dll 2>nul
del /q publish\agent\wpfgfx_cor3.dll 2>nul

echo [4/4] WiX...
wix build -arch x64 -culture ru-ru -ext WixToolset.UI.wixext -o ImperiumRDP.msi installer\ImperiumRDP.Install.wxs || exit /b 1

echo.
echo Ready: ImperiumRDP.msi
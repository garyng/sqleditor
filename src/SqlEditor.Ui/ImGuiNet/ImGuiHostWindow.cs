using System.Diagnostics;
using ImGuiNET;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;

namespace SqlEditor.Ui.ImGuiNet;

public class ImGuiHostWindow : IDisposable
{
    // ref: https://github.com/ImGuiNET/ImGui.NET/blob/f04c11e97b82bfe485fca4f799f46fe7ddcf1813/src/ImGui.NET.SampleProgram/Program.cs

    private readonly Sdl2Window _window;
    private readonly GraphicsDevice _gd;
    private readonly CommandList _cl;
    private readonly ImGuiController _controller;
    private readonly ImGuiHostWindowOptions _options;
    private readonly RgbaFloat _clearColor;
    private readonly Func<double, Task> _render;

    public ImGuiHostWindow(ImGuiHostWindowOptions options)
    {
        _options = options;
        _clearColor = new RgbaFloat(options.ClearColor.X, options.ClearColor.Y, options.ClearColor.Z, 1f);
        _render = options.Render;

        VeldridStartup.CreateWindowAndGraphicsDevice(
            options.WindowCreateInfo,
            new GraphicsDeviceOptions(true, null, true, ResourceBindingModel.Improved, true, true),
            out _window,
            out _gd);
        _window.Resized += () =>
        {
            _gd.MainSwapchain.Resize((uint)_window.Width, (uint)_window.Height);
            _controller.WindowResized(_window.Width, _window.Height);
        };
        _cl = _gd.ResourceFactory.CreateCommandList();
        _controller = new ImGuiController(_gd, _gd.MainSwapchain.Framebuffer.OutputDescription, _window.Width, _window.Height, io =>
        {
            AddTtfFont(io, options.FontInfo);
        });
    }

    private void AddTtfFont(ImGuiIOPtr io, FontInfo? fontInfo)
    {
        if (fontInfo == null) return;
        io.Fonts.AddFontFromFileTTF(fontInfo.TtfPath, fontInfo.Size);
    }

    public async Task Run(CancellationToken token = default)
    {
        var stopwatch = Stopwatch.StartNew();
        while (_window.Exists && !token.IsCancellationRequested)
        {
            float deltaTime = stopwatch.ElapsedTicks / (float)Stopwatch.Frequency;
            stopwatch.Restart();

            InputSnapshot snapshot = _window.PumpEvents();
            if (!_window.Exists) { break; }
            _controller.Update(deltaTime, snapshot);

            await _render(deltaTime);

            _cl.Begin();
            _cl.SetFramebuffer(_gd.MainSwapchain.Framebuffer);
            _cl.ClearColorTarget(0, _clearColor);
            _controller.Render(_gd, _cl);
            _cl.End();
            _gd.SubmitCommands(_cl);
            _gd.SwapBuffers(_gd.MainSwapchain);
        }
    }

    public void Dispose()
    {
        _gd.WaitForIdle();
        _controller.Dispose();
        _gd.Dispose();
        _cl.Dispose();
    }
}
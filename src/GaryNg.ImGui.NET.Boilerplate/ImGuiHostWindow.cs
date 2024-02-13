using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;

namespace GaryNg.ImGui.NET.Boilerplate;

public static class ImGuiServiceExtensions
{
    public static IServiceCollection AddImGuiNET(this IServiceCollection services, Action<ImGuiHostWindowOptions, IServiceProvider>? configure = null)
    {
        services.TryAddSingleton<IImGuiHostWindow>(provider =>
        {
            var options = new ImGuiHostWindowOptions();
            configure?.Invoke(options, provider);
            var window = new ImGuiHostWindow(options);
            return window;
        });
        return services;
    }
}

public class ImGuiHostWindowOptions
{
    public int X { get; set; } = 50;
    public int Y { get; set; } = 50;
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 720;
    public WindowState WindowInitialState { get; set; } = WindowState.Normal;
    public string WindowTitle { get; set; } = "GaryNg.ImGui.NET.Boilerplate";
    public WindowCreateInfo WindowCreateInfo => new(X, Y, WindowWidth, WindowHeight, WindowInitialState, WindowTitle);
    public Vector4 ClearColor { get; set; } = new(0.45f, 0.55f, 0.6f, 1f);
    public Func<double, Task> RenderFunc { get; set; } = _ => Task.CompletedTask;
    public Action<ImGuiIOPtr> ConfigureIo { get; set; } = _ => { };
}

public interface IImGuiHostWindow : IDisposable
{
    Task Run(CancellationToken token = default);
}

public class ImGuiHostWindow : IImGuiHostWindow
{
    // ref: https://github.com/ImGuiNET/ImGui.NET/blob/f04c11e97b82bfe485fca4f799f46fe7ddcf1813/src/ImGui.NET.SampleProgram/Program.cs

    private readonly Sdl2Window _window;
    private readonly GraphicsDevice _gd;
    private readonly CommandList _cl;
    private readonly ImGuiController _controller;
    private readonly RgbaFloat _clearColor;
    private readonly Func<double, Task> _render;

    public ImGuiHostWindow(ImGuiHostWindowOptions options)
    {
        _clearColor = new RgbaFloat(options.ClearColor.X, options.ClearColor.Y, options.ClearColor.Z, 1f);
        _render = options.RenderFunc;

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
        _controller = new ImGuiController(_gd, _gd.MainSwapchain.Framebuffer.OutputDescription, _window.Width, _window.Height, options.ConfigureIo);
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
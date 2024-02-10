using System.Collections;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using ImGuiNET;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;
using Microsoft.Extensions.DependencyInjection;
using static SqlEditor.Ui.MainView;

namespace SqlEditor.Ui;

public record FontInfo(string TtfPath, float Size);

public record ImGuiHostWindowOptions(
	WindowCreateInfo WindowCreateInfo,
	Vector4 ClearColor,
	FontInfo? FontInfo,
	Func<double, Task> Render
);

public class Program
{
	static async Task Main(string[] args)
	{
		var host = Host.CreateDefaultBuilder(args)
			.ConfigureServices(s =>
			{
				s.AddSingleton<MainView>();
			})
			.Build();

		var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
		var mainView = host.Services.GetRequiredService<MainView>();

		await host.StartAsync();

		using var window = new ImGuiHostWindow(new ImGuiHostWindowOptions(
			new WindowCreateInfo(50, 50, 1280, 720, WindowState.Normal, "sqleditor"),
			new Vector4(0.45f, 0.55f, 0.6f, 1f),
			null, // File.Exists(config.FontTtfPath) ? new(config.FontTtfPath, config.FontSize) : null
			_ => mainView.Render()
		));

		await window.Run(lifetime.ApplicationStopping);

		await host.StopAsync();
	}
}

public interface IView
{
	Task Render();
}

public class MainView : IView
{
	private bool _showDemoWindow = true;

	public MainView()
	{
		_rowsEnumerable = Generate();
		_rowsCachedEnumerable = new CachedEnumerable<Row>(Generate());
		_rowsList = Generate().Take(1000).ToList();
	}

	public record Row(int Idx, Guid Id, string Timestamp)
	{
	}

	private IEnumerable<Row> Generate()
	{
		var i = 0;
		while (true)
		{
			yield return new(i, Guid.NewGuid(), DateTime.Now.ToString("o"));
			i++;
		}
	}

	private int _page = 0;
	private IEnumerable<Row> _rowsEnumerable;
	private IEnumerable<Row> _rowsCachedEnumerable;
	private IList<Row> _rowsList;

	// ref: https://stackoverflow.com/a/62578573
	public class CachedEnumerable<T> : IEnumerable<T>, IDisposable
	{
		private readonly IEnumerator<T> enumerator;
		private readonly List<T> cache = new List<T>();

		public CachedEnumerable(IEnumerable<T> enumerable) : this(enumerable.GetEnumerator()) { }

		public CachedEnumerable(IEnumerator<T> enumerator)
			=> this.enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));

		public IEnumerator<T> GetEnumerator()
		{
			int index = 0;
			while (true)
			{
				if (index < cache.Count)
				{
					yield return cache[index];
					index++;
				}
				else if (enumerator.MoveNext())
				{
					cache.Add(enumerator.Current);
				}
				else
					yield break;
			}
		}

		public void Dispose() => enumerator.Dispose();

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	public async Task Render()
	{
		var dockId = ImGui.DockSpaceOverViewport(ImGui.GetMainViewport(), ImGuiDockNodeFlags.AutoHideTabBar | ImGuiDockNodeFlags.NoDockingSplit);
		
		{
			ImGui.SetNextWindowDockID(dockId);
			// disable scrollbar because weirdly the child is slightly bigger than the viewport
			// todo: is this still required?
			ImGui.Begin("Main", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoResize);

			if (ImGui.BeginTabBar("main"))
			{
				if (ImGui.BeginTabItem("Query"))
				{
					ImGui.SeparatorText("start of table");

					var outerSize = new Vector2(0, ImGui.GetTextLineHeightWithSpacing() * 8);
					if (ImGui.BeginTable("items", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg
							| ImGuiTableFlags.Resizable
							| ImGuiTableFlags.ScrollY, outerSize))
					{
						ImGui.TableSetupScrollFreeze(0, 1);
						ImGui.TableSetupColumn("Idx");
						ImGui.TableSetupColumn("Id");
						ImGui.TableSetupColumn("Timestamp");
						ImGui.TableHeadersRow();

						ImGuiListClipperPtr ptr;
						unsafe
						{
							var clipper = new ImGuiListClipper();
							ptr = new ImGuiListClipperPtr(&clipper);
						}
						ptr.Begin(1000);

						while (ptr.Step())
						{
							for (int row = ptr.DisplayStart; row < ptr.DisplayEnd; row++)
							{
								//var current = _rowsEnumerable.ElementAt(row);
								//var current = _rowsList.ElementAt(row);
								var current = _rowsCachedEnumerable.ElementAt(row);

								ImGui.TableNextRow();

								ImGui.TableNextColumn();
								ImGui.Text(current.Idx.ToString());

								ImGui.TableNextColumn();
								ImGui.Text(current.Id.ToString());

								ImGui.TableNextColumn();
								ImGui.Text(current.Timestamp);
							}
						}

						ImGui.EndTable();

						if (ImGui.Button("<"))
						{
							_page = Math.Min(0, _page - 1);
						}
						ImGui.SameLine();
						if (ImGui.Button(">"))
						{
							_page++;
						}
						ImGui.SameLine();
						ImGui.Text($"{_page}");
					}

					ImGui.SeparatorText("end of table");

					// todo: how to render large data in table?

					ImGui.EndTabItem();
				}
				ImGui.EndTabBar();
			}

			ImGui.End();
		}

		if (_showDemoWindow)
		{
			ImGui.SetNextWindowDockID(dockId, ImGuiCond.Appearing);
			ImGui.ShowDemoWindow(ref _showDemoWindow);
		}
	}
}

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

/// <summary>
/// A modified version of Veldrid.ImGui's ImGuiRenderer.
/// Manages input for ImGui and handles rendering ImGui's DrawLists with Veldrid.
/// </summary>
// ref: https://github.com/ImGuiNET/ImGui.NET/blob/f04c11e97b82bfe485fca4f799f46fe7ddcf1813/src/ImGui.NET.SampleProgram/ImGuiController.cs
public class ImGuiController : IDisposable
{
	private GraphicsDevice _gd;
	private bool _frameBegun;

	// Veldrid objects
	private DeviceBuffer _vertexBuffer;
	private DeviceBuffer _indexBuffer;
	private DeviceBuffer _projMatrixBuffer;
	private Texture _fontTexture;
	private TextureView _fontTextureView;
	private Shader _vertexShader;
	private Shader _fragmentShader;
	private ResourceLayout _layout;
	private ResourceLayout _textureLayout;
	private Pipeline _pipeline;
	private ResourceSet _mainResourceSet;
	private ResourceSet _fontTextureResourceSet;

	private IntPtr _fontAtlasID = (IntPtr)1;
	private bool _controlDown;
	private bool _shiftDown;
	private bool _altDown;
	private bool _winKeyDown;

	private int _windowWidth;
	private int _windowHeight;
	private Vector2 _scaleFactor = Vector2.One;

	// Image trackers
	private readonly Dictionary<TextureView, ResourceSetInfo> _setsByView = new();
	private readonly Dictionary<Texture, TextureView> _autoViewsByTexture = new();
	private readonly Dictionary<IntPtr, ResourceSetInfo> _viewsById = new();
	private readonly List<IDisposable> _ownedResources = new();
	private int _lastAssignedID = 100;

	/// <summary>
	/// Constructs a new ImGuiController.
	/// </summary>
	public ImGuiController(GraphicsDevice gd, OutputDescription outputDescription, int width, int height, Action<ImGuiIOPtr> configureIo)
	{
		_gd = gd;
		_windowWidth = width;
		_windowHeight = height;

		ImGui.CreateContext();
		var io = ImGui.GetIO();
		io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
		io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard |
			ImGuiConfigFlags.DockingEnable;
		io.Fonts.Flags |= ImFontAtlasFlags.NoBakedLines;
		configureIo(io);
		CreateDeviceResources(gd, outputDescription);
		SetPerFrameImGuiData(1f / 60f);
		ImGui.NewFrame();
		_frameBegun = true;
	}

	public void WindowResized(int width, int height)
	{
		_windowWidth = width;
		_windowHeight = height;
	}

	public void DestroyDeviceObjects()
	{
		Dispose();
	}

	public void CreateDeviceResources(GraphicsDevice gd, OutputDescription outputDescription)
	{
		_gd = gd;
		ResourceFactory factory = gd.ResourceFactory;
		_vertexBuffer = factory.CreateBuffer(new BufferDescription(10000, BufferUsage.VertexBuffer | BufferUsage.Dynamic));
		_vertexBuffer.Name = "ImGui.NET Vertex Buffer";
		_indexBuffer = factory.CreateBuffer(new BufferDescription(2000, BufferUsage.IndexBuffer | BufferUsage.Dynamic));
		_indexBuffer.Name = "ImGui.NET Index Buffer";
		RecreateFontDeviceTexture(gd);

		_projMatrixBuffer = factory.CreateBuffer(new BufferDescription(64, BufferUsage.UniformBuffer | BufferUsage.Dynamic));
		_projMatrixBuffer.Name = "ImGui.NET Projection Buffer";

		byte[] vertexShaderBytes = LoadEmbeddedShaderCode(gd.ResourceFactory, "imgui-vertex", ShaderStages.Vertex);
		byte[] fragmentShaderBytes = LoadEmbeddedShaderCode(gd.ResourceFactory, "imgui-frag", ShaderStages.Fragment);
		_vertexShader = factory.CreateShader(new ShaderDescription(ShaderStages.Vertex, vertexShaderBytes, gd.BackendType == GraphicsBackend.Metal ? "VS" : "main"));
		_fragmentShader = factory.CreateShader(new ShaderDescription(ShaderStages.Fragment, fragmentShaderBytes, gd.BackendType == GraphicsBackend.Metal ? "FS" : "main"));

		VertexLayoutDescription[] vertexLayouts = new VertexLayoutDescription[]
		{
				new VertexLayoutDescription(
					new VertexElementDescription("in_position", VertexElementSemantic.Position, VertexElementFormat.Float2),
					new VertexElementDescription("in_texCoord", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
					new VertexElementDescription("in_color", VertexElementSemantic.Color, VertexElementFormat.Byte4_Norm))
		};

		_layout = factory.CreateResourceLayout(new ResourceLayoutDescription(
			new ResourceLayoutElementDescription("ProjectionMatrixBuffer", ResourceKind.UniformBuffer, ShaderStages.Vertex),
			new ResourceLayoutElementDescription("MainSampler", ResourceKind.Sampler, ShaderStages.Fragment)));
		_textureLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
			new ResourceLayoutElementDescription("MainTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment)));

		GraphicsPipelineDescription pd = new GraphicsPipelineDescription(
			BlendStateDescription.SingleAlphaBlend,
			new DepthStencilStateDescription(false, false, ComparisonKind.Always),
			new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.Clockwise, false, true),
			PrimitiveTopology.TriangleList,
			new ShaderSetDescription(vertexLayouts, new[] { _vertexShader, _fragmentShader }),
			new ResourceLayout[] { _layout, _textureLayout },
			outputDescription,
			ResourceBindingModel.Default);
		_pipeline = factory.CreateGraphicsPipeline(ref pd);

		_mainResourceSet = factory.CreateResourceSet(new ResourceSetDescription(_layout,
			_projMatrixBuffer,
			gd.PointSampler));

		_fontTextureResourceSet = factory.CreateResourceSet(new ResourceSetDescription(_textureLayout, _fontTextureView));
	}

	/// <summary>
	/// Gets or creates a handle for a texture to be drawn with ImGui.
	/// Pass the returned handle to Image() or ImageButton().
	/// </summary>
	public IntPtr GetOrCreateImGuiBinding(ResourceFactory factory, TextureView textureView)
	{
		if (!_setsByView.TryGetValue(textureView, out ResourceSetInfo rsi))
		{
			ResourceSet resourceSet = factory.CreateResourceSet(new ResourceSetDescription(_textureLayout, textureView));
			rsi = new ResourceSetInfo(GetNextImGuiBindingID(), resourceSet);

			_setsByView.Add(textureView, rsi);
			_viewsById.Add(rsi.ImGuiBinding, rsi);
			_ownedResources.Add(resourceSet);
		}

		return rsi.ImGuiBinding;
	}

	private IntPtr GetNextImGuiBindingID()
	{
		int newID = _lastAssignedID++;
		return (IntPtr)newID;
	}

	/// <summary>
	/// Gets or creates a handle for a texture to be drawn with ImGui.
	/// Pass the returned handle to Image() or ImageButton().
	/// </summary>
	public IntPtr GetOrCreateImGuiBinding(ResourceFactory factory, Texture texture)
	{
		if (!_autoViewsByTexture.TryGetValue(texture, out TextureView textureView))
		{
			textureView = factory.CreateTextureView(texture);
			_autoViewsByTexture.Add(texture, textureView);
			_ownedResources.Add(textureView);
		}

		return GetOrCreateImGuiBinding(factory, textureView);
	}

	/// <summary>
	/// Retrieves the shader texture binding for the given helper handle.
	/// </summary>
	public ResourceSet GetImageResourceSet(IntPtr imGuiBinding)
	{
		if (!_viewsById.TryGetValue(imGuiBinding, out ResourceSetInfo tvi))
		{
			throw new InvalidOperationException("No registered ImGui binding with id " + imGuiBinding.ToString());
		}

		return tvi.ResourceSet;
	}

	public void ClearCachedImageResources()
	{
		foreach (IDisposable resource in _ownedResources)
		{
			resource.Dispose();
		}

		_ownedResources.Clear();
		_setsByView.Clear();
		_viewsById.Clear();
		_autoViewsByTexture.Clear();
		_lastAssignedID = 100;
	}

	private byte[] LoadEmbeddedShaderCode(ResourceFactory factory, string name, ShaderStages stage)
	{
		switch (factory.BackendType)
		{
			case GraphicsBackend.Direct3D11:
				{
					string resourceName = name + ".hlsl.bytes";
					return GetEmbeddedResourceBytes(resourceName);
				}
			case GraphicsBackend.OpenGL:
				{
					string resourceName = name + ".glsl";
					return GetEmbeddedResourceBytes(resourceName);
				}
			case GraphicsBackend.Vulkan:
				{
					string resourceName = name + ".spv";
					return GetEmbeddedResourceBytes(resourceName);
				}
			case GraphicsBackend.Metal:
				{
					string resourceName = name + ".metallib";
					return GetEmbeddedResourceBytes(resourceName);
				}
			default:
				throw new NotImplementedException();
		}
	}

	private byte[] GetEmbeddedResourceBytes(string resourceName)
	{
		Assembly assembly = typeof(ImGuiController).Assembly;
		using (Stream s = assembly.GetManifestResourceStream(resourceName))
		{
			byte[] ret = new byte[s.Length];
			s.Read(ret, 0, (int)s.Length);
			return ret;
		}
	}

	/// <summary>
	/// Recreates the device texture used to render text.
	/// </summary>
	public void RecreateFontDeviceTexture(GraphicsDevice gd)
	{
		ImGuiIOPtr io = ImGui.GetIO();
		// Build
		IntPtr pixels;
		int width, height, bytesPerPixel;
		io.Fonts.GetTexDataAsRGBA32(out pixels, out width, out height, out bytesPerPixel);
		// Store our identifier
		io.Fonts.SetTexID(_fontAtlasID);

		_fontTexture = gd.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
			(uint)width,
			(uint)height,
			1,
			1,
			PixelFormat.R8_G8_B8_A8_UNorm,
			TextureUsage.Sampled));
		_fontTexture.Name = "ImGui.NET Font Texture";
		gd.UpdateTexture(
			_fontTexture,
			pixels,
			(uint)(bytesPerPixel * width * height),
			0,
			0,
			0,
			(uint)width,
			(uint)height,
			1,
			0,
			0);
		_fontTextureView = gd.ResourceFactory.CreateTextureView(_fontTexture);

		io.Fonts.ClearTexData();
	}

	/// <summary>
	/// Renders the ImGui draw list data.
	/// This method requires a <see cref="GraphicsDevice"/> because it may create new DeviceBuffers if the size of vertex
	/// or index data has increased beyond the capacity of the existing buffers.
	/// A <see cref="CommandList"/> is needed to submit drawing and resource update commands.
	/// </summary>
	public void Render(GraphicsDevice gd, CommandList cl)
	{
		if (_frameBegun)
		{
			_frameBegun = false;
			ImGui.Render();
			RenderImDrawData(ImGui.GetDrawData(), gd, cl);
		}
	}

	/// <summary>
	/// Updates ImGui input and IO configuration state.
	/// </summary>
	public void Update(float deltaSeconds, InputSnapshot snapshot)
	{
		if (_frameBegun)
		{
			ImGui.Render();
		}

		SetPerFrameImGuiData(deltaSeconds);
		UpdateImGuiInput(snapshot);

		_frameBegun = true;
		ImGui.NewFrame();
	}

	/// <summary>
	/// Sets per-frame data based on the associated window.
	/// This is called by Update(float).
	/// </summary>
	private void SetPerFrameImGuiData(float deltaSeconds)
	{
		ImGuiIOPtr io = ImGui.GetIO();
		io.DisplaySize = new Vector2(
			_windowWidth / _scaleFactor.X,
			_windowHeight / _scaleFactor.Y);
		io.DisplayFramebufferScale = _scaleFactor;
		io.DeltaTime = deltaSeconds; // DeltaTime is in seconds.
	}

	private bool TryMapKey(Key key, out ImGuiKey result)
	{
		ImGuiKey KeyToImGuiKeyShortcut(Key keyToConvert, Key startKey1, ImGuiKey startKey2)
		{
			int changeFromStart1 = (int)keyToConvert - (int)startKey1;
			return startKey2 + changeFromStart1;
		}

		result = key switch
		{
			>= Key.F1 and <= Key.F24 => KeyToImGuiKeyShortcut(key, Key.F1, ImGuiKey.F1),
			>= Key.Keypad0 and <= Key.Keypad9 => KeyToImGuiKeyShortcut(key, Key.Keypad0, ImGuiKey.Keypad0),
			>= Key.A and <= Key.Z => KeyToImGuiKeyShortcut(key, Key.A, ImGuiKey.A),
			>= Key.Number0 and <= Key.Number9 => KeyToImGuiKeyShortcut(key, Key.Number0, ImGuiKey._0),
			Key.ShiftLeft or Key.ShiftRight => ImGuiKey.ModShift,
			Key.ControlLeft or Key.ControlRight => ImGuiKey.ModCtrl,
			Key.AltLeft or Key.AltRight => ImGuiKey.ModAlt,
			Key.WinLeft or Key.WinRight => ImGuiKey.ModSuper,
			Key.Menu => ImGuiKey.Menu,
			Key.Up => ImGuiKey.UpArrow,
			Key.Down => ImGuiKey.DownArrow,
			Key.Left => ImGuiKey.LeftArrow,
			Key.Right => ImGuiKey.RightArrow,
			Key.Enter => ImGuiKey.Enter,
			Key.Escape => ImGuiKey.Escape,
			Key.Space => ImGuiKey.Space,
			Key.Tab => ImGuiKey.Tab,
			Key.BackSpace => ImGuiKey.Backspace,
			Key.Insert => ImGuiKey.Insert,
			Key.Delete => ImGuiKey.Delete,
			Key.PageUp => ImGuiKey.PageUp,
			Key.PageDown => ImGuiKey.PageDown,
			Key.Home => ImGuiKey.Home,
			Key.End => ImGuiKey.End,
			Key.CapsLock => ImGuiKey.CapsLock,
			Key.ScrollLock => ImGuiKey.ScrollLock,
			Key.PrintScreen => ImGuiKey.PrintScreen,
			Key.Pause => ImGuiKey.Pause,
			Key.NumLock => ImGuiKey.NumLock,
			Key.KeypadDivide => ImGuiKey.KeypadDivide,
			Key.KeypadMultiply => ImGuiKey.KeypadMultiply,
			Key.KeypadSubtract => ImGuiKey.KeypadSubtract,
			Key.KeypadAdd => ImGuiKey.KeypadAdd,
			Key.KeypadDecimal => ImGuiKey.KeypadDecimal,
			Key.KeypadEnter => ImGuiKey.KeypadEnter,
			Key.Tilde => ImGuiKey.GraveAccent,
			Key.Minus => ImGuiKey.Minus,
			Key.Plus => ImGuiKey.Equal,
			Key.BracketLeft => ImGuiKey.LeftBracket,
			Key.BracketRight => ImGuiKey.RightBracket,
			Key.Semicolon => ImGuiKey.Semicolon,
			Key.Quote => ImGuiKey.Apostrophe,
			Key.Comma => ImGuiKey.Comma,
			Key.Period => ImGuiKey.Period,
			Key.Slash => ImGuiKey.Slash,
			Key.BackSlash or Key.NonUSBackSlash => ImGuiKey.Backslash,
			_ => ImGuiKey.None
		};

		return result != ImGuiKey.None;
	}

	private void UpdateImGuiInput(InputSnapshot snapshot)
	{
		ImGuiIOPtr io = ImGui.GetIO();
		io.AddMousePosEvent(snapshot.MousePosition.X, snapshot.MousePosition.Y);
		io.AddMouseButtonEvent(0, snapshot.IsMouseDown(MouseButton.Left));
		io.AddMouseButtonEvent(1, snapshot.IsMouseDown(MouseButton.Right));
		io.AddMouseButtonEvent(2, snapshot.IsMouseDown(MouseButton.Middle));
		io.AddMouseButtonEvent(3, snapshot.IsMouseDown(MouseButton.Button1));
		io.AddMouseButtonEvent(4, snapshot.IsMouseDown(MouseButton.Button2));
		io.AddMouseWheelEvent(0f, snapshot.WheelDelta);
		for (int i = 0; i < snapshot.KeyCharPresses.Count; i++)
		{
			io.AddInputCharacter(snapshot.KeyCharPresses[i]);
		}

		for (int i = 0; i < snapshot.KeyEvents.Count; i++)
		{
			KeyEvent keyEvent = snapshot.KeyEvents[i];
			if (TryMapKey(keyEvent.Key, out ImGuiKey imguikey))
			{
				io.AddKeyEvent(imguikey, keyEvent.Down);
			}
		}
	}

	private void RenderImDrawData(ImDrawDataPtr draw_data, GraphicsDevice gd, CommandList cl)
	{
		uint vertexOffsetInVertices = 0;
		uint indexOffsetInElements = 0;

		if (draw_data.CmdListsCount == 0)
		{
			return;
		}

		uint totalVBSize = (uint)(draw_data.TotalVtxCount * Unsafe.SizeOf<ImDrawVert>());
		if (totalVBSize > _vertexBuffer.SizeInBytes)
		{
			gd.DisposeWhenIdle(_vertexBuffer);
			_vertexBuffer = gd.ResourceFactory.CreateBuffer(new BufferDescription((uint)(totalVBSize * 1.5f), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
		}

		uint totalIBSize = (uint)(draw_data.TotalIdxCount * sizeof(ushort));
		if (totalIBSize > _indexBuffer.SizeInBytes)
		{
			gd.DisposeWhenIdle(_indexBuffer);
			_indexBuffer = gd.ResourceFactory.CreateBuffer(new BufferDescription((uint)(totalIBSize * 1.5f), BufferUsage.IndexBuffer | BufferUsage.Dynamic));
		}

		for (int i = 0; i < draw_data.CmdListsCount; i++)
		{
			ImDrawListPtr cmd_list = draw_data.CmdLists[i];

			cl.UpdateBuffer(
				_vertexBuffer,
				vertexOffsetInVertices * (uint)Unsafe.SizeOf<ImDrawVert>(),
				cmd_list.VtxBuffer.Data,
				(uint)(cmd_list.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>()));

			cl.UpdateBuffer(
				_indexBuffer,
				indexOffsetInElements * sizeof(ushort),
				cmd_list.IdxBuffer.Data,
				(uint)(cmd_list.IdxBuffer.Size * sizeof(ushort)));

			vertexOffsetInVertices += (uint)cmd_list.VtxBuffer.Size;
			indexOffsetInElements += (uint)cmd_list.IdxBuffer.Size;
		}

		// Setup orthographic projection matrix into our constant buffer
		ImGuiIOPtr io = ImGui.GetIO();
		Matrix4x4 mvp = Matrix4x4.CreateOrthographicOffCenter(
			0f,
			io.DisplaySize.X,
			io.DisplaySize.Y,
			0.0f,
			-1.0f,
			1.0f);

		_gd.UpdateBuffer(_projMatrixBuffer, 0, ref mvp);

		cl.SetVertexBuffer(0, _vertexBuffer);
		cl.SetIndexBuffer(_indexBuffer, IndexFormat.UInt16);
		cl.SetPipeline(_pipeline);
		cl.SetGraphicsResourceSet(0, _mainResourceSet);

		draw_data.ScaleClipRects(io.DisplayFramebufferScale);

		// Render command lists
		int vtx_offset = 0;
		int idx_offset = 0;
		for (int n = 0; n < draw_data.CmdListsCount; n++)
		{
			ImDrawListPtr cmd_list = draw_data.CmdLists[n];
			for (int cmd_i = 0; cmd_i < cmd_list.CmdBuffer.Size; cmd_i++)
			{
				ImDrawCmdPtr pcmd = cmd_list.CmdBuffer[cmd_i];
				if (pcmd.UserCallback != IntPtr.Zero)
				{
					throw new NotImplementedException();
				}
				else
				{
					if (pcmd.TextureId != IntPtr.Zero)
					{
						if (pcmd.TextureId == _fontAtlasID)
						{
							cl.SetGraphicsResourceSet(1, _fontTextureResourceSet);
						}
						else
						{
							cl.SetGraphicsResourceSet(1, GetImageResourceSet(pcmd.TextureId));
						}
					}

					cl.SetScissorRect(
						0,
						(uint)pcmd.ClipRect.X,
						(uint)pcmd.ClipRect.Y,
						(uint)(pcmd.ClipRect.Z - pcmd.ClipRect.X),
						(uint)(pcmd.ClipRect.W - pcmd.ClipRect.Y));

					cl.DrawIndexed(pcmd.ElemCount, 1, pcmd.IdxOffset + (uint)idx_offset, (int)pcmd.VtxOffset + vtx_offset, 0);
				}
			}
			vtx_offset += cmd_list.VtxBuffer.Size;
			idx_offset += cmd_list.IdxBuffer.Size;
		}
	}

	/// <summary>
	/// Frees all graphics resources used by the renderer.
	/// </summary>
	public void Dispose()
	{
		_vertexBuffer.Dispose();
		_indexBuffer.Dispose();
		_projMatrixBuffer.Dispose();
		_fontTexture.Dispose();
		_fontTextureView.Dispose();
		_vertexShader.Dispose();
		_fragmentShader.Dispose();
		_layout.Dispose();
		_textureLayout.Dispose();
		_pipeline.Dispose();
		_mainResourceSet.Dispose();

		foreach (IDisposable resource in _ownedResources)
		{
			resource.Dispose();
		}
	}

	private struct ResourceSetInfo
	{
		public readonly IntPtr ImGuiBinding;
		public readonly ResourceSet ResourceSet;

		public ResourceSetInfo(IntPtr imGuiBinding, ResourceSet resourceSet)
		{
			ImGuiBinding = imGuiBinding;
			ResourceSet = resourceSet;
		}
	}
}
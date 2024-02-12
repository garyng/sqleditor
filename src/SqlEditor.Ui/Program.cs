using ImGuiNET;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Collections;
using System.Numerics;
using System.Text;
using SqlEditor.Ui.ImGuiNet;
using Veldrid;
using Veldrid.StartupUtilities;
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

// refs: 
// - https://stackoverflow.com/questions/1537043/caching-ienumerable
// - https://stackoverflow.com/questions/12427097/is-there-an-ienumerable-implementation-that-only-iterates-over-its-source-e-g
// - https://stackoverflow.com/questions/58541336/thread-safe-cached-enumerator-lock-with-yield
// code from: https://github.com/dotnet/reactive/blob/2305a5b0e58b41326e952ca91f004e7c3e5d0bff/Ix.NET/Source/System.Interactive/System/Linq/Operators/Memoize.cs#L114C30-L114C44
// this exposes the internal cache buffer publicly
public sealed class MemoizedBuffer<T> : IBuffer<T>
{
	public IList<T> Buffer => _buffer;

	private readonly object _gate = new();
	private readonly IList<T> _buffer;
	private readonly IEnumerator<T> _source;

	private bool _disposed;
	private Exception? _error;
	private bool _stopped;

	public MemoizedBuffer(IEnumerator<T> source)
	{
		_source = source;
		_buffer = new List<T>();
	}

	public IEnumerator<T> GetEnumerator()
	{
		if (_disposed)
			throw new ObjectDisposedException("");

		return GetEnumerator_();
	}

	IEnumerator IEnumerable.GetEnumerator()
	{
		if (_disposed)
			throw new ObjectDisposedException("");

		return GetEnumerator();
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (!_disposed)
			{
				_source.Dispose();
				_buffer.Clear();
			}

			_disposed = true;
		}
	}

	private IEnumerator<T> GetEnumerator_()
	{
		var i = 0;

		try
		{
			while (true)
			{
				if (_disposed)
					throw new ObjectDisposedException("");

				var hasValue = default(bool);
				var current = default(T)!;

				lock (_gate)
				{
					if (i >= _buffer.Count)
					{
						if (!_stopped)
						{
							try
							{
								hasValue = _source.MoveNext();
								if (hasValue)
									current = _source.Current;
							}
							catch (Exception ex)
							{
								_stopped = true;
								_error = ex;

								_source.Dispose();
							}
						}

						if (_stopped)
						{
							if (_error != null)
								throw _error;
							else
								break;
						}

						if (hasValue)
						{
							_buffer.Add(current);
						}
					}
					else
					{
						hasValue = true;
					}
				}

				if (hasValue)
					yield return _buffer[i];
				else
					break;

				i++;
			}
		}
		finally
		{
			//if (_buffer != null)
			//	_buffer.Done(i + 1);
		}
	}
}

public class RunningIndexEnumerator<T, U> : IEnumerable<T>
	where T : RunningIndexEnumerator<T, U>.IAssignIndex
{
	public interface IAssignIndex
	{
		public record OnIndexAssignedParam(long Index, U Value);
		public long AssignIndex(long startingIndex, Action<OnIndexAssignedParam> onIndexAssigned);
	}
	
	private readonly IEnumerator<T> _source;
	private readonly Action<IAssignIndex.OnIndexAssignedParam> _onIndexAssigned;

	public RunningIndexEnumerator(IEnumerator<T> source, Action<IAssignIndex.OnIndexAssignedParam> onIndexAssigned)
	{
		_source = source;
		_onIndexAssigned = onIndexAssigned;
	}

	public IEnumerator<T> GetEnumerator()
	{
		long index = 0;
		while (_source.MoveNext())
		{
			var row = _source.Current;
			index = row.AssignIndex(index, _onIndexAssigned);
			yield return row;
		}
	}

	IEnumerator IEnumerable.GetEnumerator()
	{
		return GetEnumerator();
	}
}

public record Header(int Idx, string Name);
public record Column(object? Data)
{
	public long CellIdx { get; set; }
	public int HeaderIdx { get; set; }

	public override string? ToString() => Data?.ToString();
}
public record Row(List<Column> Columns) : RunningIndexEnumerator<Row, Column>.IAssignIndex
{
	public Row(params object[] values)
		: this(values.Select((x, i) => new Column(x)
		{
			HeaderIdx = i
		}).ToList())
	{
	}

	public long AssignIndex(long startingIndex, Action<RunningIndexEnumerator<Row, Column>.IAssignIndex.OnIndexAssignedParam> onIndexAssigned)
	{
		foreach (var column in Columns)
		{
			column.CellIdx = startingIndex;
			onIndexAssigned(new(startingIndex, column));
			startingIndex++;
		}
		return startingIndex;
	}
}

public record Table(Dictionary<int, Header> Headers)
{
	public MemoizedBuffer<Row> Rows { get; }
	public IList<Row> RowsBuffered => Rows.Buffer;

	// todo: maybe remove this if not used
	public Dictionary<long, Column> ColumnByCellIdx { get; }

	public Table(Dictionary<int, Header> headers,
		IEnumerable<Row> rows) : this(headers)
	{
		ColumnByCellIdx = new();
		Rows = new MemoizedBuffer<Row>(
			new RunningIndexEnumerator<Row, Column>(rows.GetEnumerator(), assigned =>
				{
					ColumnByCellIdx[assigned.Index] = assigned.Value;
				})
				.GetEnumerator()
		);
	}

	public IEnumerable<(Row row, List<(Header header, Column column)> selectedColumns)> Selections(SelectionState selectionState)
	{
		return selectionState switch
		{
			SelectionState.All => RowsBuffered.Select(x => (x, GetColumnsHeaders(x.Columns))),
			SelectionState.HasRanges hasRanges => RowsBuffered
				.Select(row => (
					row,
					cols: GetColumnsHeaders(row.Columns
						.Where(x => hasRanges.IsSelected(x.CellIdx)))))
				.Where(x => x.cols.Any())
				.ToList(),
			SelectionState.None => Array.Empty<(Row, List<(Header, Column)>)>(),
			_ => throw new ArgumentOutOfRangeException(nameof(selectionState))
		};
	}

	private (Header header, Column column) GetColumnHeader(Column column) => (Headers[column.HeaderIdx], column);
	private List<(Header header, Column column)> GetColumnsHeaders(IEnumerable<Column> columns) => columns.Select(GetColumnHeader).ToList();
}

public class MainView : IView
{
	private bool _showDemoWindow = true;

	public MainView()
	{
		_table = GenerateTable();
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

	private Table GenerateTable()
	{
		var rows = new MemoizedBuffer<Row>(Generate().GetEnumerator());
		var headers = new[]
		{
			new Header(0, "Idx"),
			new Header(1, "Guid"),
			new Header(2, "Timestamp"),
		}.ToDictionary(x => x.Idx, x => x);

		return new(headers, rows);
	}

	private int _page = 1;
	private Table _table;
	private string _generated = "";
	
	public abstract record SelectionState
	{
		public record HasRanges(Dictionary<long, bool> IsSelectedByIndex) : SelectionState
		{
			protected override SelectionState HandleRequest(ImGuiSelectionRequestPtr request)
			{
				if (request.Type == ImGuiSelectionRequestType.SetRange)
				{
					for (var x = request.RangeFirstItem; x <= request.RangeLastItem; x++)
					{
						IsSelectedByIndex[x] = request.RangeSelected;
					}
					return this;
				}
				return base.HandleRequest(request);
			}

			public override bool IsSelected(long index)
			{
				if (IsSelectedByIndex.TryGetValue(index, out var isSelected))
				{
					return isSelected;
				}
				return false;
			}
		}
		public record None : SelectionState
		{
			public override bool IsSelected(long index)
			{
				return false;
			}
		}

		public record All : SelectionState
		{
			public override bool IsSelected(long index)
			{
				return true;
			}
		}

		protected virtual SelectionState HandleRequest(ImGuiSelectionRequestPtr request)
		{
			switch (request.Type)
			{
				case ImGuiSelectionRequestType.None:
					break;
				case ImGuiSelectionRequestType.Clear:
					return new None();
				case ImGuiSelectionRequestType.SelectAll:
					return new All();
				case ImGuiSelectionRequestType.SetRange:
				{
					var hasRanges = new HasRanges(new());
					for (var x = request.RangeFirstItem; x <= request.RangeLastItem; x++)
					{
						hasRanges.IsSelectedByIndex[x] = request.RangeSelected;
					}

					return hasRanges;
				}
				default:
					throw new ArgumentOutOfRangeException();
			}

			return this;
		}

		public SelectionState HandleRequests(ImGuiMultiSelectIOPtr io)
		{
			var finalState = this;
			for (int i = 0; i < io.Requests.Size; i++)
			{
				var current = io.Requests[i];
				finalState = finalState.HandleRequest(current);
			}

			return finalState;
		}

		public abstract bool IsSelected(long index);
	}

	private SelectionState _selectionState = new SelectionState.None();

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

					{
						// multiselect doesn't work with ImGuiTableFlags.ScrollY,
						// so we create a child manually
						var outerSize = new Vector2(0, ImGui.GetTextLineHeightWithSpacing() * 8);
						ImGui.BeginChild("items child", outerSize);

						// this must be after BeginChild otherwise BoxSelect wont work
						var msIo = ImGui.BeginMultiSelect(ImGuiMultiSelectFlags.BoxSelect);
						_selectionState = _selectionState.HandleRequests(msIo);

						if (ImGui.BeginTable("items", _table.Headers.Count, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg
								| ImGuiTableFlags.Resizable))
						{
							ImGui.TableSetupScrollFreeze(0, 1);
							foreach (var header in _table.Headers.Values)
							{
								ImGui.TableSetupColumn(header.Name);
							}
							ImGui.TableHeadersRow();

							ImGuiListClipperPtr ptr;
							unsafe
							{
								var clipper = new ImGuiListClipper();
								ptr = new ImGuiListClipperPtr(&clipper);
							}
							ptr.Begin(1000);
							var skip = 1000 * (_page - 1);

							if (msIo.RangeSrcItem > 0)
							{
								ptr.IncludeItemByIndex((int)msIo.RangeSrcItem);
							}

							while (ptr.Step())
							{
								for (int row = ptr.DisplayStart + skip; row < ptr.DisplayEnd + skip; row++)
								{
									var current = _table.Rows.ElementAt(row);

									ImGui.TableNextRow();

									foreach (var column in current.Columns)
									{
										ImGui.TableNextColumn();
										ImGui.SetNextItemSelectionUserData(column.CellIdx);
										var isSelected = _selectionState.IsSelected(column.CellIdx);
										ImGui.Selectable(column.Data?.ToString() ?? "NULL", isSelected);
									}
								}
							}

							ImGui.EndTable();
						}

						msIo = ImGui.EndMultiSelect();
						_selectionState = _selectionState.HandleRequests(msIo);

						ImGui.EndChild();

						if (ImGui.Button("<"))
						{
							_page = Math.Max(1, _page - 1);
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

					{
						if (ImGui.Button("Generate"))
						{
							_generated = string.Join(Environment.NewLine, _table.Selections(_selectionState)
								.Select(x =>
								{
									var header = string.Join(", ", x.selectedColumns.Select(x => x.header.Name));
									var values = string.Join(", ", x.selectedColumns.Select(x => x.column));
									return $"{header}; {values}";
								}));
						}
						ImGui.Text(_generated);
					}

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
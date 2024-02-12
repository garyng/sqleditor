using FluentAssertions;
using System.Collections;

namespace SqlEditor.Tests
{
	public class RunningIndexEnumeratorTests
	{
		public interface IAssignIndex
		{
			public long AssignIndex(long startingIndex);
		}

		public record Row1(List<Col1> Cols) : IAssignIndex
		{
			public long AssignIndex(long startingIndex)
			{
				foreach (var col in Cols)
				{
					col.IndexInTable = startingIndex;
					startingIndex++;
				}
				return startingIndex;
			}
		}
		public record Col1(string Data)
		{
			public long IndexInTable { get; set; }
		}

		[Test]
		public void Generate_IndexInTable()
		{
			var rows = new List<Row1>
			{
				new(new List<Col1>
				{
					new("a")
				}),
				new(new List<Col1>
				{
					new("b")

				}),
				new(new List<Col1>
				{
					new("c")
				})
			};
			var rows2 = new RunningIndexEnumerator<Row1>(rows.GetEnumerator());
			var lastIndex = rows.SelectMany(x => x.Cols).Count() - 1;
			rows2.Last().Cols.Last().IndexInTable
				.Should().Be(lastIndex);
		}

		public class RunningIndexEnumerator<T> : IEnumerable<T>
			where T : IAssignIndex
		{
			private readonly IEnumerator<T> _source;

			public RunningIndexEnumerator(IEnumerator<T> source)
			{
				_source = source;
			}

			public IEnumerator<T> GetEnumerator()
			{
				long index = 0;
				while (_source.MoveNext())
				{
					var row = _source.Current;
					index = row.AssignIndex(index);
					yield return row;
				}
			}

			IEnumerator IEnumerable.GetEnumerator()
			{
				return GetEnumerator();
			}
		}

	}
}

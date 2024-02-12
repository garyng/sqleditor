using Dapper;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.EntityFrameworkCore.SqlServer.Design.Internal;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework.Internal;
using System.Data;
using System.Data.Common;
using System.Reflection;

namespace SqlEditor.Tests;

public class Tests
{
	[SetUp]
	public void Setup()
	{
	}

	[Test]
	public void Test1()
	{
		var services = new ServiceCollection()
			.AddEntityFrameworkDesignTimeServices();

		new SqlServerDesignTimeServices()
			.ConfigureDesignTimeServices(services);

		var provider = services.BuildServiceProvider();
		var modelFactory = provider.GetRequiredService<IDatabaseModelFactory>();

		var connectionString = "Data Source=localhost,21433;User ID=sa;Password=Pass@word123!;Initial Catalog=Tests";
		var dbModelOptions = new DatabaseModelFactoryOptions();
		var model = modelFactory.Create(connectionString, dbModelOptions);

		// todo: how to get each column's runtime type?
		// - the model returned only has the stored type of each column...
		// todo: check linqtodb on how it materialize query result into class

		// todo: maybe use dapper?

		// todo: find query builders
		// - https://github.com/sqlkata/querybuilder
		// - https://github.com/CollaboratingPlatypus/PetaPoco/wiki/Building-SQL-Queries
		// - https://www.nuget.org/packages/Dapper.SqlBuilder/
	}

	[Test]
	public async Task Test2()
	{
		var connectionString = "Data Source=localhost,21433;User ID=sa;Password=Pass@word123!;Initial Catalog=Tests";
		using DbConnection connection = new SqlConnection(connectionString);

		var tables = await connection.QueryAsync<string>(
			"""
			select TABLE_SCHEMA + '.' + TABLE_NAME
			from INFORMATION_SCHEMA.TABLES
			""");

		var schema = await connection.QueryAsync(
			"""
			select *
			from INFORMATION_SCHEMA.COLUMNS
			where TABLE_CATALOG = 'Tests'
				and TABLE_SCHEMA = 'dbo'
				and TABLE_NAME = 'Table'
			""", x => new
			{
				x.TABLE_CATALOG,
				x.TABLE_SCHEMA,
				x.TABLE_NAME,
				x.COLUMN_NAME,
				x.DATA_TYPE
			});

		var results2 = await connection.QueryAsync("select * from [dbo].[Table]");
		var results3 = results2.Select(x => x as IDictionary<string, object>)
			.Where(x => x is not null)
			.Select(x => x!.Select(y => new
			{
				// note: this will fail if the value is null
				y.Key,
				Value = y.Value is DateTimeOffset ? "this is datetimeoffset" : y.Value,
				Type = y.Value.GetType()
			}))
			.ToList();
	}

	[Test]
	public async Task CheckHowDapperDeserializeToObject()
	{
		var connectionString = "Data Source=localhost,21433;User ID=sa;Password=Pass@word123!;Initial Catalog=Tests";
		using DbConnection connection = new SqlConnection(connectionString);

		var results = (await connection.QueryAsync<TestTable>("select * from [dbo].[Table]")).AsList();

		// note: this is very complex... there is dynamic il generation for deserializer
	}
}

public class TestTable
{
	public string Id { get; set; }
	public string NChar10 { get; set; }
	public string NVarcharMax { get; set; }
	public DateTimeOffset DateTimeOffset7 { get; set; }
	public decimal Decimal18 { get; set; }
	public Guid Guid { get; set; }
}

public static class DapperExtensions
{
	public static async Task<IEnumerable<T>> QueryAsync<T>(this IDbConnection cnn, string sql, Func<dynamic, T> map, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
	{
		var result = await cnn.QueryAsync(sql, param, transaction, commandTimeout, commandType);
		return result.Select<dynamic, T>(x => map(x));
	}
}

public class Playground2
{
	private string _connectionString;
	private SqlConnection _connection;

	[OneTimeSetUp]
	public async Task TestBaseOneTimeSetUp()
	{
		// todo: extract out connection string
		_connectionString = "Data Source=localhost,21433;User ID=sa;Password=Pass@word123!;Initial Catalog=Tests";
		await TestConnection(_connectionString);
	}

	private async Task TestConnection(string connectionString, CancellationToken cancellationToken = default)
	{
		try
		{
			await using var connection = new SqlConnection(connectionString);
			await connection.OpenAsync(cancellationToken);
			var command = new SqlCommand("select 1", connection);
			var result = await command.ExecuteScalarAsync(cancellationToken);
			result.Should().Be(1);
		}
		catch (Exception)
		{
			Assert.Fail();
		}
	}

	[SetUp]
	public async Task TestBaseSetUp()
	{
		_connection = new SqlConnection(_connectionString);
		await _connection.OpenAsync();
		// todo: create test db and table
	}

	[TearDown]
	public async Task TestBaseTearDown()
	{
		await _connection.CloseAsync();
	}

	public record Dummy;

	[Test]
	public async Task PrintMemberNamesInMapper()
	{
		Dapper.SqlMapper.SetTypeMap(typeof(Dummy), new NoOpTypeMap<Dummy>());

		var result = await _connection.QueryAsync<Dummy>(
			"""
			select *
			from [dbo].[Table]
			where Id = 4
			""");
	}

	public record Schema(string ColumnName, Type DataType, bool AllowDBNull, Type ProviderSpecificDataType);

	//[TestCase("""
	//  select *
	//  from [dbo].[Table]
	//  where Id = 999
	//  """)]
	// multiple tables with same column names
	[TestCase("""
	  select *
	  from [dbo].[Table] t
	  	join [dbo].[Table1] t1 on t.Id = t1.Id
	  """)]
	public async Task GetSchemaAndData(string sql)
	{
		await using var reader = await _connection.ExecuteReaderAsync(sql);
		// todo: what about those with joins and has multiple column with the same name?
		// SQL-CLR type mapping: https://learn.microsoft.com/en-us/dotnet/framework/data/adonet/sql/linq/sql-clr-type-mapping
		var schemaTable = await reader.GetSchemaTableAsync();
		var schema = schemaTable
			.AsEnumerable()
			.Select(x => new Schema(
				x.Field<string>(nameof(Schema.ColumnName))!,
				x.Field<Type>(nameof(Schema.DataType))!,
				x.Field<bool>(nameof(Schema.AllowDBNull)),
				x.Field<Type>(nameof(Schema.ProviderSpecificDataType))!
			));

		schema.Should()
			.NotBeEmpty();

		var parser = reader.GetRowParser<dynamic>();
		
		async IAsyncEnumerable<IDictionary<string, object>> ReadAll()
		{
			while (await reader.ReadAsync())
			{
				yield return parser(reader);
			}
		}

		var data = await ReadAll().ToListAsync();
		data.Should()
			.BeEmpty();

		// note: this seems to work properly
	}
}

public sealed class NoOpTypeMap<T> : FallbackTypeMapper
{
	public NoOpTypeMap() : base(new SqlMapper.ITypeMap[]
	{
		new NoOpTypeMap(),
		new DefaultTypeMap(typeof(T))
	})
	{
	}
}

public class NoOpTypeMap : SqlMapper.ITypeMap
{
	public ConstructorInfo? FindConstructor(string[] names, Type[] types)
	{
		return null;
	}

	public ConstructorInfo? FindExplicitConstructor()
	{
		return null;
	}

	public SqlMapper.IMemberMap? GetConstructorParameter(ConstructorInfo constructor, string columnName)
	{
		return null;
	}

	public SqlMapper.IMemberMap? GetMember(string columnName)
	{
		Console.WriteLine(columnName);
		return null;
	}
}

public class FallbackTypeMapper : SqlMapper.ITypeMap
{
	// from:
	// - https://github.com/DapperLib/Dapper/issues/360#issuecomment-151469721
	// - https://stackoverflow.com/a/12615036
	// - https://gist.github.com/kalebpederson/5460509

	private readonly IEnumerable<SqlMapper.ITypeMap> _mappers;

	public FallbackTypeMapper(IEnumerable<SqlMapper.ITypeMap> mappers)
	{
		_mappers = mappers;
	}


	public ConstructorInfo? FindConstructor(string[] names, Type[] types)
	{
		foreach (var mapper in _mappers)
		{
			try
			{
				var result = mapper.FindConstructor(names, types);
				if (result is not null)
				{
					return result;
				}
			}
			catch (NotImplementedException)
			{
			}
		}

		return null;
	}

	public SqlMapper.IMemberMap? GetConstructorParameter(ConstructorInfo constructor, string columnName)
	{
		foreach (var mapper in _mappers)
		{
			try
			{
				var result = mapper.GetConstructorParameter(constructor, columnName);
				if (result != null)
				{
					return result;
				}
			}
			catch (NotImplementedException)
			{
			}
		}

		return null;
	}

	public SqlMapper.IMemberMap? GetMember(string columnName)
	{
		foreach (var mapper in _mappers)
		{
			try
			{
				var result = mapper.GetMember(columnName);
				if (result != null)
				{
					return result;
				}
			}
			catch (NotImplementedException)
			{
			}
		}

		return null;
	}


	public ConstructorInfo? FindExplicitConstructor()
	{
		return _mappers
			.Select(mapper => mapper.FindExplicitConstructor())
			.FirstOrDefault(result => result != null);
	}
}
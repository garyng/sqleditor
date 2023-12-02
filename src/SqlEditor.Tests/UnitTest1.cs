using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Dapper;
using FakeItEasy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.EntityFrameworkCore.SqlServer.Design.Internal;
using Microsoft.Extensions.DependencyInjection;

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
				x.COLUMN_NAME
			});

		var results2 = await connection.QueryAsync("select * from [dbo].[Table]");
		var results3 = results2.Select(x => x as IDictionary<string, object>)
			.Where(x => x is not null)
			.Select(x => x!.Select(y => new
			{
				// note: this will fail if the value is null
				y.Key,
				y.Value,
				Type = y.Value.GetType()
			}))
			.ToList();
	}
}

public static class DapperExtensions
{
	public static async Task<IEnumerable<T>> QueryAsync<T>(this IDbConnection cnn, string sql, Func<dynamic, T> map, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
	{
		var result = await cnn.QueryAsync(sql, param, transaction, commandTimeout, commandType);
		return result.Select<dynamic, T>(x => map(x));
	}
}
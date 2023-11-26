using FakeItEasy;
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

	}
}
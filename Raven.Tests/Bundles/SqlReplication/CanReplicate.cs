﻿// -----------------------------------------------------------------------
//  <copyright file="CanReplicate.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using Raven.Abstractions.Data;
using Raven.Abstractions.Logging;
using Raven.Client.Embedded;
using Raven.Database.Bundles.SqlReplication;
using Raven.Database.Util;
using Raven.Tests.MailingList;
using Xunit;

namespace Raven.Tests.Bundles.SqlReplication
{
	using System.Diagnostics;
	using Database.Extensions;
	using Database.Tasks;

	public class CanReplicate : RavenTest
	{
		protected override void ModifyConfiguration(Database.Config.InMemoryRavenConfiguration configuration)
		{
			configuration.Settings["Raven/ActiveBundles"] = "sqlReplication";
		}

		private const string defaultScript = @"
var orderData = {
	Id: documentId,
	OrderLinesCount: this.OrderLines.length,
	TotalCost: 0
};
replicateToOrders(orderData);

for (var i = 0; i < this.OrderLines.length; i++) {
	var line = this.OrderLines[i];
	orderData.TotalCost += line.Cost;
	replicateToOrderLines({
		OrderId: documentId,
		Qty: line.Quantity,
		Product: line.Product,
		Cost: line.Cost
	});
}";

		private void CreateRdbmsSchema(string tablePrefix = null)
		{
			var providerFactory = DbProviderFactories.GetFactory(FactIfSqlServerIsAvailable.ConnectionStringSettings.ProviderName);
			using (var con = providerFactory.CreateConnection())
			{
				con.ConnectionString = FactIfSqlServerIsAvailable.ConnectionStringSettings.ConnectionString;
				con.Open();

				using (var dbCommand = con.CreateCommand())
				{
					dbCommand.CommandText = @"
IF OBJECT_ID('Orders') is not null 
	DROP TABLE [dbo].[Orders]
IF OBJECT_ID('OrderLines') is not null 
	DROP TABLE [dbo].[OrderLines]
";
					if (tablePrefix != null)
					{
						dbCommand.CommandText = dbCommand.CommandText.Replace("Orders", "Orders" + tablePrefix).Replace("OrderLines", "OrderLines" + tablePrefix);
					}

					dbCommand.ExecuteNonQuery();

					dbCommand.CommandText = @"
CREATE TABLE [dbo].[OrderLines]
(
	[Id] int identity primary key,
	[OrderId] [nvarchar](50) NOT NULL,
	[Qty] [int] NOT NULL,
	[Product] [nvarchar](255) NOT NULL,
	[Cost] [int] NOT NULL
)

CREATE TABLE [dbo].[Orders]
(
	[Id] [nvarchar](50) NOT NULL,
	[OrderLinesCount] [int] NOT NULL,
	[TotalCost] [int] NOT NULL
)
";
					if (tablePrefix != null)
					{
						dbCommand.CommandText = dbCommand.CommandText.Replace("[Orders]", "[Orders" + tablePrefix + "]").Replace("[OrderLines]", "[OrderLines" + tablePrefix + "]");
					}

					dbCommand.ExecuteNonQuery();
				}
			}
		}

		[FactIfSqlServerIsAvailable]
		public void SimpleTransformation()
		{
			CreateRdbmsSchema();
			using (var store = NewDocumentStore())
			{
				var eventSlim = new ManualResetEventSlim(false);
				store.DocumentDatabase.StartupTasks.OfType<SqlReplicationTask>()
					.First().AfterReplicationCompleted += successCount =>
					{
						if (successCount != 0)
							eventSlim.Set();
					};

				using (var session = store.OpenSession())
				{
					session.Store(new Order
					{
						OrderLines = new List<OrderLine>
						{
							new OrderLine{Cost = 3, Product = "Milk", Quantity = 3},
							new OrderLine{Cost = 4, Product = "Bear", Quantity = 2},
						}
					});
					session.SaveChanges();
				}

				SetupSqlReplication(store, defaultScript);

				eventSlim.Wait(TimeSpan.FromMinutes(5));

				var providerFactory = DbProviderFactories.GetFactory(FactIfSqlServerIsAvailable.ConnectionStringSettings.ProviderName);
				using (var con = providerFactory.CreateConnection())
				{
					con.ConnectionString = FactIfSqlServerIsAvailable.ConnectionStringSettings.ConnectionString;
					con.Open();

					using (var dbCommand = con.CreateCommand())
					{
						dbCommand.CommandText = " SELECT COUNT(*) FROM Orders";
						Assert.Equal(1, dbCommand.ExecuteScalar());
						dbCommand.CommandText = " SELECT COUNT(*) FROM OrderLines";
						Assert.Equal(2, dbCommand.ExecuteScalar());
					}
				}
			}
		}

		[FactIfSqlServerIsAvailable]
		public void MultipleScriptsRunOverTheSameCollection()
		{
			CreateRdbmsSchema();
			CreateRdbmsSchema("2");

			using (var store = NewDocumentStore())
			{
				var eventSlim = new ManualResetEventSlim(false);

				int testCount = 1000;
				store.DocumentDatabase.StartupTasks.OfType<SqlReplicationTask>()
					.First().AfterReplicationCompleted += successCount =>
					{
						if (GetOrdersCount() == testCount)
						{
							if (GetOrdersCount("2") == testCount)
								eventSlim.Set();
						}
					};

				using (var session = store.OpenSession())
				{
					for (int i = 0; i < testCount; i++)
					{
						session.Store(new Order
						{
							OrderLines = new List<OrderLine>
							{
								new OrderLine{Cost = 3, Product = "Milk", Quantity = 3},
								new OrderLine{Cost = 4, Product = "Bear", Quantity = 2},
							}
						});
					}
					
					session.SaveChanges();
				}

				using (var session = store.OpenSession())
				{
					session.Store(new SqlReplicationConfig
					{
						Id = "Raven/SqlReplication/Configuration/OrdersAndLines",
						Name = "OrdersAndLines",
						ConnectionString = FactIfSqlServerIsAvailable.ConnectionStringSettings.ConnectionString,
						FactoryName = FactIfSqlServerIsAvailable.ConnectionStringSettings.ProviderName,
						RavenEntityName = "Orders",
						SqlReplicationTables =
						{
							new SqlReplicationTable
							{
								TableName = "Orders",
								DocumentKeyColumn = "Id"
							},
							new SqlReplicationTable
							{
								TableName = "OrderLines",
								DocumentKeyColumn = "OrderId"
							},
						},
						Script = defaultScript
					});

					session.Store(new SqlReplicationConfig
					{
						Id = "Raven/SqlReplication/Configuration/OrdersAndLines2",
						Name = "OrdersAndLines2",
						ConnectionString = FactIfSqlServerIsAvailable.ConnectionStringSettings.ConnectionString,
						FactoryName = FactIfSqlServerIsAvailable.ConnectionStringSettings.ProviderName,
						RavenEntityName = "Orders",
						SqlReplicationTables =
						{
							new SqlReplicationTable
							{
								TableName = "Orders2",
								DocumentKeyColumn = "Id"
							},
							new SqlReplicationTable
							{
								TableName = "OrderLines2",
								DocumentKeyColumn = "OrderId"
							},
						},
						Script = defaultScript.Replace("replicateToOrders", "replicateToOrders2").Replace("replicateToOrderLines", "replicateToOrderLines2")
					});

					session.SaveChanges();
				}

				eventSlim.Wait(TimeSpan.FromMinutes(5));

				Assert.Null(store.DocumentDatabase.Get(Constants.RavenAlerts, null));
				Assert.Equal(testCount, GetOrdersCount());
				Assert.Equal(2 * testCount, GetOrderLinesCount());

				Assert.Equal(testCount, GetOrdersCount("2"));
				Assert.Equal(2 * testCount, GetOrderLinesCount("2"));
			}
		}

		[FactIfSqlServerIsAvailable]
		public void ReplicateMultipleBatches()
		{
			CreateRdbmsSchema();
			using (var store = NewDocumentStore())
			{
				var eventSlim = new ManualResetEventSlim(false);

				int testCount = 5000;
				store.DocumentDatabase.StartupTasks.OfType<SqlReplicationTask>()
					.First().AfterReplicationCompleted += successCount =>
					{
						if (GetOrdersCount() == testCount)
							eventSlim.Set();
					};

				using (var session = store.BulkInsert())
				{
					for (int i = 0; i < testCount; i++)
					{
						session.Store(new Order
						              {
							              OrderLines = new List<OrderLine>
							                           {
								                           new OrderLine {Cost = 3, Product = "Milk", Quantity = 3},
								                           new OrderLine {Cost = 4, Product = "Bear", Quantity = 2},
							                           }

						              });
					}
				}

				SetupSqlReplication(store, defaultScript);

				eventSlim.Wait(TimeSpan.FromMinutes(5));

				Assert.Equal(testCount, GetOrdersCount());
				Assert.Equal(2 * testCount, GetOrderLinesCount());

			}
		}

		private static int GetOrdersCount(string tableSuffix = "")
		{
			var providerFactory =
					DbProviderFactories.GetFactory(FactIfSqlServerIsAvailable.ConnectionStringSettings.ProviderName);
			using (var con = providerFactory.CreateConnection())
			{
				con.ConnectionString = FactIfSqlServerIsAvailable.ConnectionStringSettings.ConnectionString;
				con.Open();

				using (var dbCommand = con.CreateCommand())
				{
					dbCommand.CommandText = " SELECT COUNT(*) FROM Orders" + tableSuffix;
					return (int) dbCommand.ExecuteScalar();
				}
			}
		}

		private static int GetOrderLinesCount(string tableSuffix = "")
		{
			var providerFactory =
					DbProviderFactories.GetFactory(FactIfSqlServerIsAvailable.ConnectionStringSettings.ProviderName);
			using (var con = providerFactory.CreateConnection())
			{
				con.ConnectionString = FactIfSqlServerIsAvailable.ConnectionStringSettings.ConnectionString;
				con.Open();

				using (var dbCommand = con.CreateCommand())
				{
					dbCommand.CommandText = " SELECT COUNT(*) FROM OrderLines" + tableSuffix;
					return (int)dbCommand.ExecuteScalar();
				}
			}
		}

		protected override void CreateDefaultIndexes(Client.IDocumentStore documentStore)
		{

		}

		[FactIfSqlServerIsAvailable]
		public void CanUpdateToBeNoItemsInChildTable()
		{
			CreateRdbmsSchema();
			using (var store = NewDocumentStore())
			{
				var eventSlim = new ManualResetEventSlim(false);
				store.DocumentDatabase.StartupTasks.OfType<SqlReplicationTask>()
					 .First().AfterReplicationCompleted += successCount =>
					 {
						 if (successCount != 0)
							 eventSlim.Set();
					 };

				using (var session = store.OpenSession())
				{
					session.Store(new Order
					{
						OrderLines = new List<OrderLine>
						{
							new OrderLine{Cost = 3, Product = "Milk", Quantity = 3},
							new OrderLine{Cost = 4, Product = "Bear", Quantity = 2},
						}
					});
					session.SaveChanges();
				}

				SetupSqlReplication(store, defaultScript);

				eventSlim.Wait(TimeSpan.FromMinutes(5));

				AssertCounts(1, 2);

				eventSlim.Reset();

				using (var session = store.OpenSession())
				{
					session.Load<Order>(1).OrderLines.Clear();
					session.SaveChanges();
				}

				eventSlim.Wait(TimeSpan.FromMinutes(5));

				AssertCounts(1, 0);

			}
		}

		[FactIfSqlServerIsAvailable]
		public void CanDelete()
		{
			CreateRdbmsSchema();
			using (var store = NewDocumentStore())
			{
				var eventSlim = new ManualResetEventSlim(false);
				store.DocumentDatabase.StartupTasks.OfType<SqlReplicationTask>()
					 .First().AfterReplicationCompleted += successCount =>
					 {
						 if (successCount != 0)
							 eventSlim.Set();
					 };

				using (var session = store.OpenSession())
				{
					session.Store(new Order
					{
						OrderLines = new List<OrderLine>
						{
							new OrderLine{Cost = 3, Product = "Milk", Quantity = 3},
							new OrderLine{Cost = 4, Product = "Bear", Quantity = 2},
						}
					});
					session.SaveChanges();
				}

				SetupSqlReplication(store, defaultScript);

				eventSlim.Wait(TimeSpan.FromMinutes(5));

				AssertCounts(1, 2);

				eventSlim.Reset();

				store.DatabaseCommands.Delete("orders/1", null);

				eventSlim.Wait(TimeSpan.FromMinutes(5));

				AssertCounts(0, 0);

			}
		}

		[FactIfSqlServerIsAvailable]
		public void WillLog()
		{
			LogManager.RegisterTarget<DatabaseMemoryTarget>();

			CreateRdbmsSchema();
			using (var store = NewDocumentStore())
			{
				var eventSlim = new ManualResetEventSlim(false);
				store.DocumentDatabase.StartupTasks.OfType<SqlReplicationTask>()
					 .First().AfterReplicationCompleted += successCount =>
					 {
						 if (successCount != 0)
							 eventSlim.Set();
					 };

				using (var session = store.OpenSession())
				{
					session.Store(new Order());
					session.SaveChanges();
				}

				SetupSqlReplication(store, @"output ('Tralala');asdfsadf
var nameArr = this.StepName.split('.');");

				eventSlim.Wait(TimeSpan.FromMinutes(5));
				
				var databaseMemoryTarget = LogManager.GetTarget<DatabaseMemoryTarget>();
				var foo = databaseMemoryTarget[Constants.SystemDatabase].WarnLog.First(x=>x.LoggerName == typeof(SqlReplicationTask).FullName);
				Assert.Equal("Could not parse SQL Replication script for OrdersAndLines", foo.FormattedMessage);
			}
		}

		private static void AssertCounts(int ordersCount, int orderLineCounts)
		{
			var providerFactory = DbProviderFactories.GetFactory(FactIfSqlServerIsAvailable.ConnectionStringSettings.ProviderName);
			using (var con = providerFactory.CreateConnection())
			{
				con.ConnectionString = FactIfSqlServerIsAvailable.ConnectionStringSettings.ConnectionString;
				con.Open();

				using (var dbCommand = con.CreateCommand())
				{
					dbCommand.CommandText = "SELECT COUNT(*) FROM Orders";
					Assert.Equal(ordersCount, dbCommand.ExecuteScalar());
					dbCommand.CommandText = "SELECT COUNT(*) FROM OrderLines";
					Assert.Equal(orderLineCounts, dbCommand.ExecuteScalar());
				}
			}
		}

		private static void SetupSqlReplication(EmbeddableDocumentStore store, string script)
		{
			using (var session = store.OpenSession())
			{
				session.Store(new SqlReplicationConfig
				{
					Id = "Raven/SqlReplication/Configuration/OrdersAndLines",
					Name = "OrdersAndLines",
					ConnectionString = FactIfSqlServerIsAvailable.ConnectionStringSettings.ConnectionString,
					FactoryName = FactIfSqlServerIsAvailable.ConnectionStringSettings.ProviderName,
					RavenEntityName = "Orders",
					SqlReplicationTables =
					{
						new SqlReplicationTable {TableName = "Orders", DocumentKeyColumn = "Id"},
						new SqlReplicationTable {TableName = "OrderLines", DocumentKeyColumn = "OrderId"},
					},
					Script = script
				});
				session.SaveChanges();
			}
		}

		public class Order
		{
			public string Id { get; set; }
			public List<OrderLine> OrderLines { get; set; }
		}

		public class OrderLine
		{
			public string Product { get; set; }
			public int Quantity { get; set; }
			public int Cost { get; set; }
		}
	}
}
using FluentAssertions;
using SqlDrift.Core.Extractor;
using SqlDrift.Core.Models;
using Xunit;

namespace SqlDrift.Core.Tests;

public class ScriptDomExtractorTests
{
    private readonly ScriptDomExtractor _extractor = new("dbo");

    [Fact]
    public void ExtractFromScript_Should_Parse_Table_With_Columns_And_Constraints()
    {
        var sql = """
            CREATE TABLE [dbo].[Customers] (
                [Id] INT IDENTITY(1,1) NOT NULL,
                [CustomerCode] VARCHAR(20) NOT NULL,
                [FullName] NVARCHAR(100) NOT NULL,
                [Email] NVARCHAR(255) NULL,
                [Balance] DECIMAL(18, 2) NOT NULL DEFAULT (0.00),
                [CreatedAt] DATETIME2(7) NOT NULL DEFAULT (SYSUTCDATETIME()),
                [IsActive] BIT NOT NULL DEFAULT (1),
                CONSTRAINT [PK_Customers] PRIMARY KEY CLUSTERED ([Id]),
                CONSTRAINT [UQ_Customers_Code] UNIQUE NONCLUSTERED ([CustomerCode]),
                CONSTRAINT [CK_Customers_Balance] CHECK ([Balance] >= 0)
            );
            """;

        var snapshot = _extractor.ExtractFromScript(sql, "TestDb");

        snapshot.Should().NotBeNull();
        snapshot.Tables.Should().ContainKey("dbo.Customers");

        var table = snapshot.Tables["dbo.Customers"];
        table.Schema.Should().Be("dbo");
        table.TableName.Should().Be("Customers");
        table.Columns.Should().HaveCount(7);

        // Check columns
        var idCol = table.Columns.First(c => c.ColumnName == "Id");
        idCol.IsIdentity.Should().BeTrue();
        idCol.IsNullable.Should().BeFalse();
        idCol.DataType.Should().Be("int");

        var emailCol = table.Columns.First(c => c.ColumnName == "Email");
        emailCol.IsNullable.Should().BeTrue();
        emailCol.DataType.Should().Be("nvarchar");
        emailCol.FormattedDataType.Should().Be("NVARCHAR(255)");

        var balanceCol = table.Columns.First(c => c.ColumnName == "Balance");
        balanceCol.Precision.Should().Be(18);
        balanceCol.Scale.Should().Be(2);
        balanceCol.FormattedDataType.Should().Be("DECIMAL(18,2)");
        balanceCol.DefaultValue.Should().Be("0.00");

        // Primary key
        var pk = table.Indexes.FirstOrDefault(i => i.IsPrimaryKey);
        pk.Should().NotBeNull();
        pk!.IndexName.Should().Be("PK_Customers");
        pk.KeyColumns.Should().ContainSingle().Which.Should().Be("Id");

        // Unique constraint / index
        var uq = table.Indexes.FirstOrDefault(i => i.IndexName == "UQ_Customers_Code");
        uq.Should().NotBeNull();
        uq!.IsUnique.Should().BeTrue();
        uq.KeyColumns.Should().ContainSingle().Which.Should().Be("CustomerCode");

        // Check constraint
        table.CheckConstraints.Should().ContainSingle();
        var ck = table.CheckConstraints.First();
        ck.ConstraintName.Should().Be("CK_Customers_Balance");
        ck.Definition.Should().Contain("[Balance] >= 0");
    }

    [Fact]
    public void ExtractFromScript_Should_Parse_ForeignKeys_And_Indexes()
    {
        var sql = """
            CREATE TABLE [dbo].[Orders] (
                [OrderId] INT NOT NULL,
                [CustomerId] INT NOT NULL,
                [OrderDate] DATETIME2 NOT NULL,
                [TotalAmount] DECIMAL(12,2) NOT NULL,
                CONSTRAINT [PK_Orders] PRIMARY KEY CLUSTERED ([OrderId])
            );
            GO

            ALTER TABLE [dbo].[Orders]
            ADD CONSTRAINT [FK_Orders_Customers] FOREIGN KEY ([CustomerId])
            REFERENCES [dbo].[Customers] ([Id])
            ON DELETE CASCADE;
            GO

            CREATE NONCLUSTERED INDEX [IX_Orders_CustomerDate]
            ON [dbo].[Orders] ([CustomerId], [OrderDate])
            INCLUDE ([TotalAmount])
            WHERE [TotalAmount] > 0;
            """;

        var snapshot = _extractor.ExtractFromScript(sql, "TestDb");

        snapshot.Tables.Should().ContainKey("dbo.Orders");
        var ordersTable = snapshot.Tables["dbo.Orders"];

        // Foreign key
        ordersTable.ForeignKeys.Should().ContainSingle();
        var fk = ordersTable.ForeignKeys.First();
        fk.ConstraintName.Should().Be("FK_Orders_Customers");
        fk.ReferencedTable.Should().Be("Customers");
        fk.ParentColumns.Should().ContainSingle().Which.Should().Be("CustomerId");
        fk.ReferencedColumns.Should().ContainSingle().Which.Should().Be("Id");
        fk.OnDeleteAction.Should().Be("CASCADE");

        // Index
        var index = ordersTable.Indexes.FirstOrDefault(i => i.IndexName == "IX_Orders_CustomerDate");
        index.Should().NotBeNull();
        index!.IsPrimaryKey.Should().BeFalse();
        index.IsClustered.Should().BeFalse();
        index.KeyColumns.Should().Equal("CustomerId", "OrderDate");
        index.IncludedColumns.Should().Equal("TotalAmount");
        index.FilterDefinition.Should().Contain("[TotalAmount] > 0");
    }

    [Fact]
    public void ExtractFromScript_Should_Parse_Programmable_Objects()
    {
        var sql = """
            CREATE PROCEDURE [dbo].[usp_GetOrderSummary]
                @CustomerId INT
            AS
            BEGIN
                SET NOCOUNT ON;
                SELECT OrderId, TotalAmount FROM dbo.Orders WHERE CustomerId = @CustomerId;
            END;
            GO

            CREATE VIEW [dbo].[vw_HighValueOrders]
            AS
                SELECT OrderId, CustomerId, TotalAmount
                FROM dbo.Orders
                WHERE TotalAmount > 1000.00;
            GO

            CREATE FUNCTION [dbo].[fn_CalculateTax](@Amount DECIMAL(18,2))
            RETURNS DECIMAL(18,2)
            AS
            BEGIN
                RETURN @Amount * 0.0825;
            END;
            GO

            CREATE TRIGGER [dbo].[trg_Orders_Audit]
            ON [dbo].[Orders]
            AFTER INSERT
            AS
            BEGIN
                SET NOCOUNT ON;
            END;
            """;

        var snapshot = _extractor.ExtractFromScript(sql, "TestDb");

        snapshot.ProgrammableObjects.Should().HaveCount(4);

        snapshot.ProgrammableObjects.Should().ContainKey("dbo.usp_GetOrderSummary");
        var proc = snapshot.ProgrammableObjects["dbo.usp_GetOrderSummary"];
        proc.ObjectType.Should().Be(DatabaseObjectType.StoredProcedure);

        snapshot.ProgrammableObjects.Should().ContainKey("dbo.vw_HighValueOrders");
        var view = snapshot.ProgrammableObjects["dbo.vw_HighValueOrders"];
        view.ObjectType.Should().Be(DatabaseObjectType.View);

        snapshot.ProgrammableObjects.Should().ContainKey("dbo.fn_CalculateTax");
        var fn = snapshot.ProgrammableObjects["dbo.fn_CalculateTax"];
        fn.ObjectType.Should().Be(DatabaseObjectType.UserDefinedFunction);

        snapshot.ProgrammableObjects.Should().ContainKey("dbo.trg_Orders_Audit");
        var trg = snapshot.ProgrammableObjects["dbo.trg_Orders_Audit"];
        trg.ObjectType.Should().Be(DatabaseObjectType.Trigger);
    }
}

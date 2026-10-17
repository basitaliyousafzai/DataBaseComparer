using FluentAssertions;
using SqlDrift.Core.Extractor;
using SqlDrift.Core.Models;
using Xunit;

namespace SqlDrift.Core.Tests;

public class SnapshotSerializerTests
{
    [Fact]
    public async Task Serialize_And_Deserialize_Should_RoundTrip_Accurately()
    {
        var tables = new Dictionary<string, TableModel>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbo.Users"] = new TableModel(
                "dbo",
                "Users",
                [
                    new ColumnModel("Id", "int", 4, 10, 0, false, true),
                    new ColumnModel("Email", "nvarchar", 200, 0, 0, false, false, "'default@domain.com'")
                ],
                [
                    new IndexModel("PK_Users", true, true, true, null, ["Id"], [])
                ],
                [
                    new ForeignKeyModel("FK_Users_Roles", "dbo", "Users", ["RoleId"], "dbo", "Roles", ["Id"])
                ],
                [
                    new CheckConstraintModel("CK_Users_Email", "[Email] LIKE '%@%'")
                ]
            )
        };

        var procs = new Dictionary<string, ProgrammableObjectModel>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbo.usp_GetUsers"] = new ProgrammableObjectModel(
                DatabaseObjectType.StoredProcedure,
                "dbo",
                "usp_GetUsers",
                "CREATE PROCEDURE [dbo].[usp_GetUsers] AS SELECT 1;",
                "ABC123HASH"
            )
        };

        var snapshot = new DatabaseSchemaSnapshot("ProdDb", DateTime.UtcNow, tables, procs);

        var json = SnapshotSerializer.Serialize(snapshot);
        json.Should().NotBeNullOrWhiteSpace();

        var restored = SnapshotSerializer.Deserialize(json);
        restored.DatabaseName.Should().Be("ProdDb");
        restored.Tables.Should().ContainKey("dbo.Users");
        restored.ProgrammableObjects.Should().ContainKey("dbo.usp_GetUsers");

        var restoredTable = restored.Tables["dbo.Users"];
        restoredTable.Columns.Should().HaveCount(2);
        restoredTable.Indexes.Should().HaveCount(1);
        restoredTable.ForeignKeys.Should().HaveCount(1);
        restoredTable.CheckConstraints.Should().HaveCount(1);

        // Test File IO
        var tempFile = Path.Combine(Path.GetTempPath(), $"sqldrift_test_{Guid.NewGuid():N}.json");
        try
        {
            await SnapshotSerializer.SaveSnapshotAsync(snapshot, tempFile);
            File.Exists(tempFile).Should().BeTrue();

            var loadedFromFile = await SnapshotSerializer.LoadSnapshotAsync(tempFile);
            loadedFromFile.DatabaseName.Should().Be("ProdDb");
            loadedFromFile.Tables.Should().ContainKey("dbo.Users");
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}

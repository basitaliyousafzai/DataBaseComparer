using FluentAssertions;
using SqlDrift.Cli;
using Xunit;

namespace SqlDrift.Core.Tests;

public class CliCommandTests
{
    [Fact]
    public async Task Cli_Snapshot_And_Compare_Should_Work_EndToEnd()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sqldrift_cli_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var sourceSqlFile = Path.Combine(tempDir, "source.sql");
            var targetSqlFile = Path.Combine(tempDir, "target.sql");
            var snapshotFile = Path.Combine(tempDir, "snapshot.json");
            var migrationFile = Path.Combine(tempDir, "migration.sql");
            var reportFile = Path.Combine(tempDir, "report.md");

            await File.WriteAllTextAsync(sourceSqlFile, """
                CREATE TABLE [dbo].[Customers] (
                    [Id] INT IDENTITY(1,1) NOT NULL,
                    [Name] NVARCHAR(100) NOT NULL,
                    [Email] NVARCHAR(255) NULL,
                    CONSTRAINT [PK_Customers] PRIMARY KEY CLUSTERED ([Id])
                );
                """);

            await File.WriteAllTextAsync(targetSqlFile, """
                CREATE TABLE [dbo].[Customers] (
                    [Id] INT IDENTITY(1,1) NOT NULL,
                    [Name] NVARCHAR(100) NOT NULL,
                    CONSTRAINT [PK_Customers] PRIMARY KEY CLUSTERED ([Id])
                );
                """);

            // 1. Test snapshot command
            var snapshotExitCode = await Program.Main(["snapshot", sourceSqlFile, "-o", snapshotFile]);
            snapshotExitCode.Should().Be(0);
            File.Exists(snapshotFile).Should().BeTrue();

            // 2. Test compare command with markdown output
            var compareExitCode = await Program.Main(["compare", sourceSqlFile, targetSqlFile, "-f", "markdown", "-o", reportFile]);
            compareExitCode.Should().Be(0);
            File.Exists(reportFile).Should().BeTrue();
            var reportContent = await File.ReadAllTextAsync(reportFile);
            reportContent.Should().Contain("Email");

            // 3. Test generate command
            var generateExitCode = await Program.Main(["generate", sourceSqlFile, targetSqlFile, "-o", migrationFile]);
            generateExitCode.Should().Be(0);
            File.Exists(migrationFile).Should().BeTrue();
            var migrationSql = await File.ReadAllTextAsync(migrationFile);
            migrationSql.Should().Contain("ALTER TABLE [dbo].[Customers] ADD [Email]");
            migrationSql.Should().Contain("BEGIN TRANSACTION");

            // 4. Test ci command with --fail-on-drift
            var ciFailExitCode = await Program.Main(["ci", sourceSqlFile, targetSqlFile, "--fail-on-drift"]);
            ciFailExitCode.Should().Be(1);

            // 5. Test ci command when identical
            var ciSuccessExitCode = await Program.Main(["ci", sourceSqlFile, sourceSqlFile, "--fail-on-drift"]);
            ciSuccessExitCode.Should().Be(0);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}

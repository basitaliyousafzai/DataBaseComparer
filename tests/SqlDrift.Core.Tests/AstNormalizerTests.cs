using FluentAssertions;
using SqlDrift.Core.Extractor;
using Xunit;

namespace SqlDrift.Core.Tests;

public class AstNormalizerTests
{
    [Fact]
    public void NormalizeScript_Should_Ignore_Comments_And_Whitespace()
    {
        var script1 = """
            -- Stored Procedure definition
            CREATE PROCEDURE [dbo].[GetActiveUsers]
                @MinAge INT = 18
            AS
            BEGIN
                /* Multi-line comment here */
                SELECT [Id], [Username], [Email]
                FROM [dbo].[Users]
                WHERE [Age] >= @MinAge AND [IsActive] = 1;
            END
            """;

        var script2 = """
            CREATE PROCEDURE [dbo].[GetActiveUsers] @MinAge INT = 18 AS BEGIN
            SELECT [Id], [Username], [Email] FROM [dbo].[Users] WHERE [Age] >= @MinAge AND [IsActive] = 1;
            END
            """;

        var areEquivalent = AstNormalizer.AreScriptsEquivalent(script1, script2);
        areEquivalent.Should().BeTrue();
    }

    [Fact]
    public void NormalizeScript_Should_Detect_Structural_Differences()
    {
        var script1 = """
            CREATE PROCEDURE [dbo].[GetUsers]
            AS
            BEGIN
                SELECT Id, Username FROM dbo.Users;
            END
            """;

        var script2 = """
            CREATE PROCEDURE [dbo].[GetUsers]
            AS
            BEGIN
                SELECT Id, Username, Email FROM dbo.Users;
            END
            """;

        var areEquivalent = AstNormalizer.AreScriptsEquivalent(script1, script2);
        areEquivalent.Should().BeFalse();
    }

    [Fact]
    public void ComputeNormalizedHash_Should_Be_Deterministic()
    {
        var script = "CREATE VIEW [dbo].[vw_Customers] AS SELECT CustomerId, Name FROM Customers;";
        var hash1 = AstNormalizer.ComputeNormalizedHash(script);
        var hash2 = AstNormalizer.ComputeNormalizedHash(script);

        hash1.Should().NotBeNullOrWhiteSpace();
        hash1.Should().Be(hash2);
    }
}

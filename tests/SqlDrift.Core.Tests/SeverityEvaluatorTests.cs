using FluentAssertions;
using SqlDrift.Core.Diff;
using SqlDrift.Core.Models;
using Xunit;

namespace SqlDrift.Core.Tests;

public class SeverityEvaluatorTests
{
    [Theory]
    [InlineData("bigint", "int", true)]       // Target bigint, Source int -> Narrowing
    [InlineData("int", "smallint", true)]     // Target int, Source smallint -> Narrowing
    [InlineData("smallint", "tinyint", true)] // Target smallint, Source tinyint -> Narrowing
    [InlineData("nvarchar", "varchar", true)] // Target nvarchar, Source varchar -> Narrowing
    [InlineData("int", "bigint", false)]      // Target int, Source bigint -> Widening
    [InlineData("tinyint", "int", false)]     // Target tinyint, Source int -> Widening
    [InlineData("varchar", "nvarchar", false)]// Target varchar, Source nvarchar -> Widening
    public void IsTypeNarrowing_Should_Detect_Integer_And_Type_Conversions(string targetType, string sourceType, bool expectedNarrowing)
    {
        var targetCol = new ColumnModel("Col", targetType, 100, 18, 0, true, false);
        var sourceCol = new ColumnModel("Col", sourceType, 100, 18, 0, true, false);

        var isNarrowing = SeverityEvaluator.IsTypeNarrowing(sourceCol, targetCol);
        isNarrowing.Should().Be(expectedNarrowing);
    }

    [Theory]
    [InlineData(100, 50, true)]   // Target 100, Source 50 -> Narrowing
    [InlineData(-1, 50, true)]    // Target MAX (-1), Source 50 -> Narrowing
    [InlineData(50, 100, false)]  // Target 50, Source 100 -> Widening
    [InlineData(50, -1, false)]   // Target 50, Source MAX (-1) -> Widening
    [InlineData(100, 100, false)] // Same length
    public void IsTypeNarrowing_Should_Detect_String_Length_Narrowing(int targetLen, int sourceLen, bool expectedNarrowing)
    {
        var targetCol = new ColumnModel("Col", "varchar", targetLen, 0, 0, true, false);
        var sourceCol = new ColumnModel("Col", "varchar", sourceLen, 0, 0, true, false);

        var isNarrowing = SeverityEvaluator.IsTypeNarrowing(sourceCol, targetCol);
        isNarrowing.Should().Be(expectedNarrowing);
    }

    [Fact]
    public void EvaluateColumnDrift_Should_Mark_Null_To_NotNull_As_Breaking()
    {
        var targetCol = new ColumnModel("Notes", "nvarchar", 200, 0, 0, true, false);  // Currently nullable
        var sourceCol = new ColumnModel("Notes", "nvarchar", 200, 0, 0, false, false); // Wants NOT NULL

        var severity = SeverityEvaluator.EvaluateColumnDrift(DriftType.Modified, sourceCol, targetCol);
        severity.Should().Be(DriftSeverity.Breaking);
    }

    [Fact]
    public void EvaluateColumnDrift_Should_Mark_NotNull_To_Null_As_Medium()
    {
        var targetCol = new ColumnModel("Notes", "nvarchar", 200, 0, 0, false, false); // Currently NOT NULL
        var sourceCol = new ColumnModel("Notes", "nvarchar", 200, 0, 0, true, false);  // Wants NULL (safe widening of nullability)

        var severity = SeverityEvaluator.EvaluateColumnDrift(DriftType.Modified, sourceCol, targetCol);
        severity.Should().Be(DriftSeverity.Medium);
    }

    [Fact]
    public void EvaluateTableDrift_Should_Mark_Missing_Table_As_Breaking()
    {
        var severity = SeverityEvaluator.EvaluateTableDrift(DriftType.Missing);
        severity.Should().Be(DriftSeverity.Breaking);
    }
}

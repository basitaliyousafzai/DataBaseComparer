using FluentAssertions;
using SqlDrift.Core.Planner;
using Xunit;

namespace SqlDrift.Core.Tests;

public class DependencyGraphTests
{
    [Fact]
    public void TopologicalSort_Should_Order_Linear_Dependencies_Correctly()
    {
        var graph = new DependencyGraph();

        // Dependency: Countries -> States -> Cities -> Addresses
        graph.AddEdge("dbo.Countries", "dbo.States");
        graph.AddEdge("dbo.States", "dbo.Cities");
        graph.AddEdge("dbo.Cities", "dbo.Addresses");

        var (sorted, hasCycle, cycleNodes) = graph.TopologicalSort();

        hasCycle.Should().BeFalse();
        cycleNodes.Should().BeEmpty();
        sorted.Should().Equal("dbo.Countries", "dbo.States", "dbo.Cities", "dbo.Addresses");
    }

    [Fact]
    public void TopologicalSort_Should_Order_Diamond_DAG_Correctly()
    {
        var graph = new DependencyGraph();

        // Base -> (Left, Right) -> Join
        graph.AddEdge("Base", "Left");
        graph.AddEdge("Base", "Right");
        graph.AddEdge("Left", "Join");
        graph.AddEdge("Right", "Join");

        var (sorted, hasCycle, cycleNodes) = graph.TopologicalSort();

        hasCycle.Should().BeFalse();
        cycleNodes.Should().BeEmpty();

        // Base must be first, Join must be last
        sorted.First().Should().Be("Base");
        sorted.Last().Should().Be("Join");
        sorted.IndexOf("Left").Should().BeGreaterThan(sorted.IndexOf("Base"));
        sorted.IndexOf("Right").Should().BeGreaterThan(sorted.IndexOf("Base"));
        sorted.IndexOf("Join").Should().BeGreaterThan(sorted.IndexOf("Left"));
        sorted.IndexOf("Join").Should().BeGreaterThan(sorted.IndexOf("Right"));
    }

    [Fact]
    public void TopologicalSort_Should_Detect_Circular_Dependencies()
    {
        var graph = new DependencyGraph();

        // Cyclic foreign keys: TableA <-> TableB
        graph.AddEdge("dbo.TableA", "dbo.TableB");
        graph.AddEdge("dbo.TableB", "dbo.TableA");

        var (sorted, hasCycle, cycleNodes) = graph.TopologicalSort();

        hasCycle.Should().BeTrue();
        cycleNodes.Should().BeEquivalentTo(["dbo.TableA", "dbo.TableB"]);
    }

    [Fact]
    public void TopologicalSort_Should_Handle_Independent_And_Cyclic_Components()
    {
        var graph = new DependencyGraph();

        // Independent valid node
        graph.AddNode("dbo.Standalone");

        // Valid dependency
        graph.AddEdge("dbo.Parent", "dbo.Child");

        // Cycle in separate subgraph
        graph.AddEdge("dbo.Cyclic1", "dbo.Cyclic2");
        graph.AddEdge("dbo.Cyclic2", "dbo.Cyclic3");
        graph.AddEdge("dbo.Cyclic3", "dbo.Cyclic1");

        var (sorted, hasCycle, cycleNodes) = graph.TopologicalSort();

        hasCycle.Should().BeTrue();
        cycleNodes.Should().BeEquivalentTo(["dbo.Cyclic1", "dbo.Cyclic2", "dbo.Cyclic3"]);
        sorted.Should().Contain("dbo.Standalone");
        sorted.Should().Contain("dbo.Parent");
        sorted.Should().Contain("dbo.Child");
    }
}

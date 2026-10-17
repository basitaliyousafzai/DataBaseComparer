using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlDrift.Core.Models;

namespace SqlDrift.Core.Planner;

public record MigrationStep(
    string Id,
    DatabaseObjectType ObjectType,
    string Schema,
    string Name,
    string Action, // "CREATE_TABLE", "ALTER_COLUMN", "ADD_INDEX", "ADD_FK", "CREATE_OR_ALTER_PROC", "DROP_..."
    int Phase,     // 1 to 9
    string SqlScript,
    DriftSeverity Severity,
    List<string> DependsOnIds
);

public class DependencyGraph
{
    private readonly Dictionary<string, HashSet<string>> _adjacencyList = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _inDegree = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _nodes = new(StringComparer.OrdinalIgnoreCase);

    public void AddNode(string node)
    {
        if (_nodes.Add(node))
        {
            _adjacencyList[node] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _inDegree[node] = 0;
        }
    }

    /// <summary>
    /// Adds a directed edge: dependency -> dependent (i.e. 'dependency' must be executed before 'dependent').
    /// </summary>
    public void AddEdge(string dependency, string dependent)
    {
        AddNode(dependency);
        AddNode(dependent);

        if (_adjacencyList[dependency].Add(dependent))
        {
            _inDegree[dependent]++;
        }
    }

    /// <summary>
    /// Kahn's Algorithm for topological sorting.
    /// Returns sorted list of nodes and detects if a cycle exists.
    /// </summary>
    public (List<string> Sorted, bool HasCycle, List<string> CycleNodes) TopologicalSort()
    {
        var inDegreeCopy = new Dictionary<string, int>(_inDegree, StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        foreach (var node in _nodes)
        {
            if (inDegreeCopy[node] == 0)
            {
                queue.Enqueue(node);
            }
        }

        var sorted = new List<string>();

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            sorted.Add(current);

            foreach (var neighbor in _adjacencyList[current])
            {
                inDegreeCopy[neighbor]--;
                if (inDegreeCopy[neighbor] == 0)
                {
                    queue.Enqueue(neighbor);
                }
            }
        }

        if (sorted.Count != _nodes.Count)
        {
            // Cycle detected! Collect nodes that couldn't be resolved
            var cycleNodes = _nodes.Except(sorted, StringComparer.OrdinalIgnoreCase).ToList();
            return (sorted, true, cycleNodes);
        }

        return (sorted, false, []);
    }
}

public static class ScriptDependencyFinder
{
    public static HashSet<string> FindReferencedObjects(string script, string defaultSchema = "dbo")
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(script))
            return referenced;

        var parser = new TSql160Parser(true);
        using var reader = new StringReader(script);
        var fragment = parser.Parse(reader, out _);
        if (fragment == null)
            return referenced;

        var visitor = new ReferenceVisitor(defaultSchema, referenced);
        fragment.Accept(visitor);
        return referenced;
    }

    private sealed class ReferenceVisitor : TSqlFragmentVisitor
    {
        private readonly string _defaultSchema;
        private readonly HashSet<string> _references;

        public ReferenceVisitor(string defaultSchema, HashSet<string> references)
        {
            _defaultSchema = defaultSchema;
            _references = references;
        }

        public override void ExplicitVisit(NamedTableReference node)
        {
            if (node.SchemaObject != null)
            {
                var schema = node.SchemaObject.SchemaIdentifier?.Value ?? _defaultSchema;
                var table = node.SchemaObject.BaseIdentifier.Value;
                _references.Add($"{schema}.{table}");
            }
            base.ExplicitVisit(node);
        }
    }
}

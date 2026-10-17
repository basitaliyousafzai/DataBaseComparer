using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace SqlDrift.Core.Diff;

public record LineDiff(
    int? SourceLineNumber,
    int? TargetLineNumber,
    string Text,
    ChangeType Type
);

public record SideBySideDiffResult(
    List<LineDiff> OldLines,
    List<LineDiff> NewLines
);

public static class TextDiffService
{
    private static readonly ISideBySideDiffBuilder SideBySideBuilder = new SideBySideDiffBuilder(new Differ());
    private static readonly IInlineDiffBuilder InlineBuilder = new InlineDiffBuilder(new Differ());

    public static SideBySideDiffResult BuildSideBySideDiff(string oldText, string newText)
    {
        var model = SideBySideBuilder.BuildDiffModel(oldText ?? string.Empty, newText ?? string.Empty);

        var oldLines = model.OldText.Lines.Select(l => new LineDiff(
            l.Position,
            null,
            l.Text ?? string.Empty,
            l.Type
        )).ToList();

        var newLines = model.NewText.Lines.Select(l => new LineDiff(
            null,
            l.Position,
            l.Text ?? string.Empty,
            l.Type
        )).ToList();

        return new SideBySideDiffResult(oldLines, newLines);
    }

    public static List<LineDiff> BuildInlineDiff(string oldText, string newText)
    {
        var model = InlineBuilder.BuildDiffModel(oldText ?? string.Empty, newText ?? string.Empty);
        return model.Lines.Select(l => new LineDiff(
            l.Position,
            l.Position,
            l.Text ?? string.Empty,
            l.Type
        )).ToList();
    }
}

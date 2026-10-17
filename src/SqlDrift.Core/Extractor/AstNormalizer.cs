using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlDrift.Core.Extractor;

public static class AstNormalizer
{
    private static readonly Sql160ScriptGenerator Generator = new(new SqlScriptGeneratorOptions
    {
        KeywordCasing = KeywordCasing.Uppercase,
        IncludeSemicolons = true,
        AlignClauseBodies = false,
        NewLineBeforeFromClause = false,
        NewLineBeforeGroupByClause = false,
        NewLineBeforeHavingClause = false,
        NewLineBeforeJoinClause = false,
        NewLineBeforeOffsetClause = false,
        NewLineBeforeOrderByClause = false,
        NewLineBeforeOutputClause = false,
        NewLineBeforeWhereClause = false
    });

    public static string NormalizeScript(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return string.Empty;

        var parser = new TSql160Parser(true);
        using var reader = new StringReader(script);
        var fragment = parser.Parse(reader, out var errors);

        if (errors == null || errors.Count == 0)
        {
            Generator.GenerateScript(fragment, out var normalized);
            return NormalizeWhitespace(normalized);
        }

        // Fallback if full parse encounters syntax variations
        return NormalizeFallback(script);
    }

    public static string ComputeNormalizedHash(string script)
    {
        var normalized = NormalizeScript(script);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    public static bool AreScriptsEquivalent(string scriptA, string scriptB)
    {
        var hashA = ComputeNormalizedHash(scriptA);
        var hashB = ComputeNormalizedHash(scriptB);
        return string.Equals(hashA, hashB, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWhitespace(string input)
    {
        var noComments = Regex.Replace(input, @"/\*.*?\*/", "", RegexOptions.Singleline);
        noComments = Regex.Replace(noComments, @"--.*?$", "", RegexOptions.Multiline);
        var condensed = Regex.Replace(noComments, @"\s+", " ").Trim();
        return condensed;
    }

    private static string NormalizeFallback(string script)
    {
        var normalized = NormalizeWhitespace(script);
        return normalized.ToLowerInvariant();
    }
}

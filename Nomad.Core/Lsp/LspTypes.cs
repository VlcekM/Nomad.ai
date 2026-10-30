using System.Text;
using System.Text.Json;

namespace Nomad.Core.Lsp;

/// <summary>How bad a diagnostic is (the protocol's numbers).</summary>
public enum LspSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4,
}

/// <summary>An error or warning a language server found in a file: where (0-based), how bad, and what it says.</summary>
public sealed record LspDiagnostic(int Line, int Column, int EndLine, int EndColumn, LspSeverity Severity, string Message, string? Code);

/// <summary>A change to a file's text: the range replaced (0-based; columns in UTF-16 units) and what replaces it.</summary>
public sealed record LspTextEdit(int Line, int Column, int EndLine, int EndColumn, string NewText)
{
    /// <summary>The edits made to a text (all against the original; the protocol says they never overlap).</summary>
    public static string Apply(string text, IEnumerable<LspTextEdit> edits)
    {
        var starts = LineStarts(text);
        var builder = new StringBuilder(text);
        foreach (var (from, to, insert) in edits
                     .Select(e => (From: Offset(text, starts, e.Line, e.Column), To: Offset(text, starts, e.EndLine, e.EndColumn), e.NewText))
                     .OrderByDescending(e => e.From).ThenByDescending(e => e.To))
        {
            builder.Remove(from, Math.Max(0, to - from)).Insert(from, insert);
        }

        return builder.ToString();
    }

    /// <summary>Where each line starts in a text (after a '\n').</summary>
    public static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    /// <summary>A line and column's offset in a text, kept inside the line (before its "\r\n").</summary>
    public static int Offset(string text, List<int> starts, int line, int column)
    {
        if (line >= starts.Count)
        {
            return text.Length;
        }

        var start = starts[Math.Max(0, line)];
        var end = line + 1 < starts.Count ? starts[line + 1] - 1 : text.Length;
        if (line + 1 < starts.Count && end > start && text[end - 1] == '\r')
        {
            end--;
        }

        return Math.Clamp(start + column, start, end);
    }
}

/// <summary>
/// A completion a language server offered: its label, kind (the protocol's CompletionItemKind), the text that filters
/// and sorts it, what it inserts (and over which range, if the server says), and the item as sent, to ask for more
/// (its documentation, the usings it adds) when it is chosen.
/// </summary>
public sealed record LspCompletion(string Label, int Kind, string FilterText, string SortText, string InsertText, LspTextEdit? Edit, string? Detail, bool Preselect, string Raw);

/// <summary>A completion list: its items, and whether typing more should ask again rather than filter these.</summary>
public sealed record LspCompletionList(IReadOnlyList<LspCompletion> Items, bool IsIncomplete);

/// <summary>A completion chosen and resolved: the usings and such it adds besides itself, and its documentation.</summary>
public sealed record LspResolvedCompletion(IReadOnlyList<LspTextEdit> AdditionalEdits, string? Documentation, string? Detail);

internal static class LspJson
{
    public static LspTextEdit? Edit(JsonElement edit)
    {
        if (edit.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // An InsertReplaceEdit has "insert" and "replace" ranges: the replacing one is the one meant here.
        if (!edit.TryGetProperty("range", out var range) && !edit.TryGetProperty("replace", out range))
        {
            return null;
        }

        var (line, column, endLine, endColumn) = Range(range);
        return new LspTextEdit(line, column, endLine, endColumn, edit.TryGetProperty("newText", out var text) ? text.GetString() ?? "" : "");
    }

    public static (int Line, int Column, int EndLine, int EndColumn) Range(JsonElement range)
    {
        var start = range.GetProperty("start");
        var end = range.GetProperty("end");
        return (start.GetProperty("line").GetInt32(), start.GetProperty("character").GetInt32(), end.GetProperty("line").GetInt32(), end.GetProperty("character").GetInt32());
    }

    public static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Documentation is a string or MarkupContent.
    public static string? Markup(JsonElement element, string name) =>
        element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) ? null
        : value.ValueKind == JsonValueKind.String ? value.GetString()
        : String(value, "value");
}

/// <summary>One overload a signature help shows: its declaration, its documentation, and where each parameter is in the declaration.</summary>
public sealed record LspSignature(string Label, string? Documentation, IReadOnlyList<(int Start, int End, string? Documentation)> Parameters, int? ActiveParameter);

/// <summary>The overloads of the call the caret is in, the one the server thinks fits, and the argument the caret is at.</summary>
public sealed record LspSignatureHelp(IReadOnlyList<LspSignature> Signatures, int ActiveSignature, int ActiveParameter);

/// <summary>
/// A fix or refactoring offered at a place (a code action): its title, kind ("quickfix", "refactor.extract"…), whether
/// it is the one to pick, its edits if already known (else resolved when chosen), the choices it groups, and itself as sent.
/// </summary>
public sealed record LspCodeAction(string Title, string? Kind, bool IsPreferred, IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>>? Edits, IReadOnlyList<LspCodeAction> Nested, string Raw);

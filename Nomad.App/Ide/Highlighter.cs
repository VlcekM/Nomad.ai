using TextMateSharp.Grammars;
using TextMateSharp.Registry;

namespace Nomad.App.Ide;

/// <summary>What a piece of code is, for its color: each kind has a theme color (CodeKeywordBrush and so on).</summary>
public enum CodeKind : byte
{
    Plain,
    Keyword,
    Type,
    Function,
    String,
    Number,
    Comment,
    Tag,
    Attribute,

    /// <summary>Keywords that steer the flow (if, return, for, import), which Visual Studio colors apart.</summary>
    Control,
}

/// <summary>A run of a line, by index into its text, and its kind.</summary>
public readonly record struct CodeSpan(int Start, int Length, CodeKind Kind);

/// <summary>
/// Syntax highlighting with VS Code's TextMate grammars (bundled, about 50 languages): a grammar names each piece of a
/// line by scopes ("keyword.control.cs", "string.quoted.double"), and <see cref="KindOf"/> maps those to our own few
/// kinds, so the colors are the app theme's. One registry for the app, on the UI thread.
/// </summary>
public static class Highlighter
{
    /// <summary>Longer lines aren't tokenized (a minified file's), and a line gets this long at most.</summary>
    public const int LineLimit = 2000;

    // Generous: a grammar compiles its patterns on first use, inside the first lines it tokenizes.
    private static readonly TimeSpan LineTimeout = TimeSpan.FromMilliseconds(750);

    private static RegistryOptions? _options;
    private static Registry? _registry;
    private static Dictionary<string, string>? _byName;
    private static readonly Dictionary<string, IGrammar?> Grammars = [];

    // Code fence names and file endings the grammars don't list themselves.
    private static readonly Dictionary<string, string> Extra = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c#"] = "csharp",
        ["cs"] = "csharp",
        ["f#"] = "fsharp",
        ["shell"] = "shellscript",
        ["console"] = "shellscript",
        ["pwsh"] = "powershell",
        ["psm1"] = "powershell",
        ["jsonl"] = "json",
        ["yml"] = "yaml",
        ["md"] = "markdown",
        ["html5"] = "html",
        ["cshtml"] = "razor",
        ["golang"] = "go",
        ["rs"] = "rust",
        ["kt"] = "java",
        ["dockerfile"] = "dockerfile",
        ["gitignore"] = "ignore",
        ["toml"] = "ini",
        ["slnx"] = "xml",
        ["manifest"] = "xml",
        ["resx"] = "xml",
        ["config"] = "xml",
    };

    /// <summary>The grammar for a file, by its name (null when there is none: plain text).</summary>
    public static IGrammar? ForFile(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(name);
        return Find(extension.Length > 1 ? extension[1..] : name);
    }

    /// <summary>The grammar for a code fence's language ("csharp", "ts", "c#"; null when there is none).</summary>
    public static IGrammar? ForLanguage(string? info)
    {
        var name = info?.Trim().Split([' ', '\t', '{', ','], 2)[0];
        return string.IsNullOrEmpty(name) ? null : Find(name);
    }

    private static IGrammar? Find(string name)
    {
        try
        {
            _options ??= new RegistryOptions(ThemeName.DarkPlus);
            _registry ??= new Registry(_options);
            _byName ??= Names(_options);
            if (!_byName.TryGetValue(name, out var id) && !(Extra.TryGetValue(name, out var alias) && _byName.TryGetValue(alias, out id)))
            {
                return null;
            }

            if (!Grammars.TryGetValue(id, out var grammar))
            {
                var scope = _options.GetScopeByLanguageId(id);
                grammar = scope is null ? null : _registry.LoadGrammar(scope);
                Grammars[id] = grammar;
            }

            return grammar;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null; // A grammar that won't load: the text stays plain.
        }
    }

    // Each language by its id, its names and its file endings, all lowercase.
    private static Dictionary<string, string> Names(RegistryOptions options)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in options.GetAvailableLanguages())
        {
            if (options.GetScopeByLanguageId(language.Id) is null)
            {
                continue;
            }

            names.TryAdd(language.Id, language.Id);
            foreach (var alias in language.Aliases ?? [])
            {
                names.TryAdd(alias, language.Id);
            }

            foreach (var extension in language.Extensions ?? [])
            {
                names.TryAdd(extension.TrimStart('.'), language.Id);
            }
        }

        return names;
    }

    /// <summary>A line's runs, and the state the next line starts from.</summary>
    public static (CodeSpan[] Spans, IStateStack? State) Tokenize(IGrammar grammar, string line, IStateStack? state)
    {
        if (line.Length > LineLimit)
        {
            return ([], state);
        }

        ITokenizeLineResult result;
        try
        {
            result = grammar.TokenizeLine(line, state, LineTimeout);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return ([], state);
        }

        var spans = new List<CodeSpan>(result.Tokens.Length);
        foreach (var token in result.Tokens)
        {
            var start = Math.Min(token.StartIndex, line.Length);
            var end = Math.Min(token.EndIndex, line.Length);
            if (end <= start)
            {
                continue;
            }

            var kind = KindOf(token.Scopes, line.AsSpan(start, end - start));
            if (kind == CodeKind.Plain)
            {
                continue;
            }

            // Runs of one kind next to each other are one.
            if (spans.Count > 0 && spans[^1] is var last && last.Kind == kind && last.Start + last.Length == start)
            {
                spans[^1] = last with { Length = end - last.Start };
            }
            else
            {
                spans.Add(new CodeSpan(start, end - start, kind));
            }
        }

        return ([.. spans], result.RuleStack);
    }

    /// <summary>A token's kind: the innermost of its scopes that maps to one.</summary>
    public static CodeKind KindOf(IList<string> scopes, ReadOnlySpan<char> text)
    {
        for (var i = scopes.Count - 1; i >= 0; i--)
        {
            if (Map(scopes[i], text) is { } kind)
            {
                return kind;
            }
        }

        return CodeKind.Plain;
    }

    // VS Code's Dark+ split into our kinds; null leaves it to an outer scope.
    private static CodeKind? Map(string scope, ReadOnlySpan<char> text)
    {
        static bool Is(string scope, string prefix) =>
            scope.StartsWith(prefix, StringComparison.Ordinal) && (scope.Length == prefix.Length || scope[prefix.Length] == '.');

        // Code inside a string ({name} in $"…", ${x} in `…`) is code, not string.
        if (Is(scope, "meta.interpolation") || Is(scope, "meta.template.expression") || Is(scope, "meta.embedded"))
        {
            return CodeKind.Plain;
        }

        if (Is(scope, "comment") || Is(scope, "punctuation.definition.comment"))
        {
            return CodeKind.Comment;
        }

        if (Is(scope, "string") || Is(scope, "punctuation.definition.string") || Is(scope, "markup.inline.raw") || Is(scope, "markup.fenced_code"))
        {
            return CodeKind.String;
        }

        if (Is(scope, "constant.numeric"))
        {
            return CodeKind.Number;
        }

        if (Is(scope, "constant.language") || Is(scope, "variable.language") ||
            Is(scope, "markup.heading") || Is(scope, "entity.name.section"))
        {
            return CodeKind.Keyword;
        }

        // Operators (and storage symbols, as =>) are plain, unless they are words (new, typeof, is, and).
        if (Is(scope, "keyword.operator") || Is(scope, "storage"))
        {
            return text.Length > 0 && char.IsLetter(text[0]) ? CodeKind.Keyword : CodeKind.Plain;
        }

        if (Is(scope, "keyword.control") && !Is(scope, "keyword.control.directive"))
        {
            return CodeKind.Control;
        }

        if (Is(scope, "keyword"))
        {
            return CodeKind.Keyword;
        }

        if (Is(scope, "support.type.property-name") || Is(scope, "entity.other.attribute-name") || Is(scope, "support.type.vendored.property-name"))
        {
            return CodeKind.Attribute;
        }

        if (Is(scope, "entity.name.tag"))
        {
            return CodeKind.Tag;
        }

        if (Is(scope, "entity.name.type") || Is(scope, "entity.name.class") || Is(scope, "entity.other.inherited-class") ||
            Is(scope, "support.class") || Is(scope, "support.type"))
        {
            return CodeKind.Type;
        }

        if (Is(scope, "entity.name.function") || Is(scope, "support.function") || Is(scope, "meta.function-call.generic"))
        {
            return CodeKind.Function;
        }

        return null;
    }
}

/// <summary>
/// A document's highlighting, line by line from the top: each line's runs and the state the next starts from. Made
/// lazily, as far down as is shown, a little at a time; an edit drops what follows the line it changed.
/// </summary>
public sealed class CodeHighlighting(IGrammar grammar)
{
    private readonly List<CodeSpan[]> _spans = [];
    private readonly List<IStateStack?> _states = [];

    /// <summary>Lines tokenized from the top.</summary>
    public int Count => _spans.Count;

    /// <summary>The line's runs, if it has been tokenized.</summary>
    public CodeSpan[]? Spans(int line) => line < _spans.Count ? _spans[line] : null;

    /// <summary>The text changed from this line on.</summary>
    public void Invalidate(int line)
    {
        if (line < _spans.Count)
        {
            _spans.RemoveRange(line, _spans.Count - line);
            _states.RemoveRange(line, _states.Count - line);
        }
    }

    /// <summary>Tokenizes on down to <paramref name="last"/> for at most <paramref name="budget"/>; false if it isn't there yet.</summary>
    public bool Advance(IReadOnlyList<string> lines, int last, TimeSpan budget)
    {
        last = Math.Min(last, lines.Count - 1);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (_spans.Count <= last)
        {
            var state = _states.Count > 0 ? _states[^1] : null;
            var (spans, next) = Highlighter.Tokenize(grammar, lines[_spans.Count], state);
            _spans.Add(spans);
            _states.Add(next);
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) > budget)
            {
                return _spans.Count > last;
            }
        }

        return true;
    }
}

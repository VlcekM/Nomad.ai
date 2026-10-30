namespace Nomad.Core.Code;

/// <summary>What a symbol is, for its icon and its place in the results (types before members).</summary>
public enum SymbolKind
{
    File,
    Class,
    Record,
    Struct,
    Interface,
    Enum,
    Delegate,
    Method,
    Constructor,
    Property,
    Field,
    Constant,
    Event,
    EnumMember,
    Function,
    Variable,
}

/// <summary>
/// A declaration found in a file: its name, what it is, its type (a member's, a method's return type) and what it is
/// in (<c>Namespace.Type</c>), and where: the file, and the 0-based line and column of its name.
/// </summary>
public sealed record CodeSymbol(string Name, SymbolKind Kind, string? Type, string Container, string Path, int Line, int Column)
{
    public bool IsType => Kind is SymbolKind.Class or SymbolKind.Record or SymbolKind.Struct or SymbolKind.Interface or SymbolKind.Enum or SymbolKind.Delegate;
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Nomad.App.ViewModels;
using Nomad.Core.Code;

namespace Nomad.App.Ide;

/// <summary>
/// A code search result's text, as Visual Studio's Go To lists it: the name with the matched characters picked out,
/// "()" after a method, its type after a colon, then "(in Container)". Its runs are styled by class in the view
/// ("match", "type", "muted").
/// </summary>
public sealed class CodeSearchText : TextBlock
{
    public static readonly StyledProperty<CodeSearchResult?> ResultProperty =
        AvaloniaProperty.Register<CodeSearchText, CodeSearchResult?>(nameof(Result));

    public CodeSearchResult? Result
    {
        get => GetValue(ResultProperty);
        set => SetValue(ResultProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ResultProperty)
        {
            Build(change.GetNewValue<CodeSearchResult?>());
        }
    }

    private void Build(CodeSearchResult? result)
    {
        var inlines = Inlines ??= [];
        inlines.Clear();
        if (result is null)
        {
            return;
        }

        var symbol = result.Symbol;
        var name = symbol.Name;
        var matched = new HashSet<int>(result.Match.Positions);
        for (var i = 0; i < name.Length;)
        {
            var on = matched.Contains(i);
            var j = i;
            while (j < name.Length && matched.Contains(j) == on)
            {
                j++;
            }

            var run = new Run(name[i..j]);
            if (on)
            {
                run.Classes.Add("match");
            }

            inlines.Add(run);
            i = j;
        }

        if (symbol.Kind is SymbolKind.Method or SymbolKind.Constructor or SymbolKind.Function or SymbolKind.Delegate)
        {
            inlines.Add(new Run("()"));
        }

        if (symbol.Type is { Length: > 0 } type)
        {
            inlines.Add(new Run(" : "));
            var typeRun = new Run(type);
            typeRun.Classes.Add("type");
            inlines.Add(typeRun);
        }

        if (symbol.Container.Length > 0)
        {
            var container = new Run($"  (in {symbol.Container})");
            container.Classes.Add("muted");
            inlines.Add(container);
        }
    }
}

/// <summary>A code search result's icon, by what the symbol is, in Visual Studio's colors for each.</summary>
public sealed class CodeSymbolIcon : Control
{
    public static readonly StyledProperty<SymbolKind> KindProperty =
        AvaloniaProperty.Register<CodeSymbolIcon, SymbolKind>(nameof(Kind));

    private static readonly IBrush Orange = new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D));
    private static readonly IBrush Purple = new SolidColorBrush(Color.FromRgb(0xB1, 0x80, 0xD7));
    private static readonly IBrush Blue = new SolidColorBrush(Color.FromRgb(0x3C, 0x9C, 0xDC));
    private static readonly IBrush Teal = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
    private static readonly IBrush Gold = new SolidColorBrush(Color.FromRgb(0xDC, 0xB3, 0x3C));
    private static readonly IBrush Grey = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0));

    private static readonly Geometry ClassShape = StreamGeometry.Parse("M2.5,2.5 L6.5,2.5 L6.5,6.5 L2.5,6.5 Z M9.5,9.5 L13.5,9.5 L13.5,13.5 L9.5,13.5 Z M4.5,6.5 L4.5,11.5 L9.5,11.5 M6.5,4.5 L11.5,4.5 L11.5,9.5");
    private static readonly Geometry StructShape = StreamGeometry.Parse("M3,3 L13,3 L13,13 L3,13 Z M3,8 L13,8 M8,3 L8,13");
    private static readonly Geometry InterfaceShape = StreamGeometry.Parse("M8,8 L14.5,8 M8,8 A3,3 0 1 1 2,8 A3,3 0 1 1 8,8 Z");
    private static readonly Geometry EnumShape = StreamGeometry.Parse("M2.5,2.5 L10.5,2.5 L10.5,6.5 L2.5,6.5 Z M5.5,9.5 L13.5,9.5 L13.5,13.5 L5.5,13.5 Z");
    private static readonly Geometry EnumMemberShape = StreamGeometry.Parse("M3,5 L13,5 L13,11 L3,11 Z M6,8 L10,8");
    private static readonly Geometry DelegateShape = StreamGeometry.Parse("M2,8 L10,8 M7,5 L10,8 L7,11 M12.75,4 L12.75,12");
    private static readonly Geometry MethodShape = StreamGeometry.Parse("M8,1.75 L14,5 L14,11 L8,14.25 L2,11 L2,5 Z M2,5 L8,8.25 L14,5 M8,8.25 L8,14.25");
    private static readonly Geometry PropertyShape = StreamGeometry.Parse("M10.2,2.2 C11.6,1.7 13.2,2.1 14,3.3 L11.6,5.6 L10.4,5.6 L10.4,4.4 L12.7,2 M10.4,5.6 L3.3,12.7 C2.8,13.2 2.8,13.9 3.3,14.4 C3.8,14.9 4.5,14.9 5,14.4 L12.1,7.3 C13.3,7.6 14.6,7 15,5.8");
    private static readonly Geometry FieldShape = StreamGeometry.Parse("M2,8 L8,2.5 L14,8 L8,13.5 Z");
    private static readonly Geometry ConstantShape = StreamGeometry.Parse("M2.5,4 L13.5,4 L13.5,12 L2.5,12 Z M5.5,7 L10.5,7 M5.5,9 L10.5,9");
    private static readonly Geometry EventShape = StreamGeometry.Parse("M9.5,1.5 L4,9 L8,9 L6.5,14.5 L12,7 L8,7 Z");
    private static readonly Geometry FileShape = StreamGeometry.Parse("M4,1.75 L9.25,1.75 L12.5,5 L12.5,14.25 L4,14.25 Z M9.25,1.75 L9.25,5 L12.5,5");

    static CodeSymbolIcon()
    {
        AffectsRender<CodeSymbolIcon>(KindProperty);
    }

    public SymbolKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(16, 16);

    public override void Render(DrawingContext context)
    {
        var (shape, brush, filled) = Kind switch
        {
            SymbolKind.Class or SymbolKind.Record => (ClassShape, Orange, false),
            SymbolKind.Struct => (StructShape, Teal, false),
            SymbolKind.Interface => (InterfaceShape, Blue, false),
            SymbolKind.Enum => (EnumShape, Gold, false),
            SymbolKind.EnumMember => (EnumMemberShape, Blue, false),
            SymbolKind.Delegate => (DelegateShape, Purple, false),
            SymbolKind.Method or SymbolKind.Constructor or SymbolKind.Function => (MethodShape, Purple, false),
            SymbolKind.Property => (PropertyShape, Grey, false),
            SymbolKind.Field or SymbolKind.Variable => (FieldShape, Blue, false),
            SymbolKind.Constant => (ConstantShape, Grey, false),
            SymbolKind.Event => (EventShape, Orange, true),
            _ => (FileShape, TextElement.GetForeground(this) ?? Grey, false),
        };
        var offset = new Point((Bounds.Width - 16) / 2, (Bounds.Height - 16) / 2);
        using (context.PushTransform(Matrix.CreateTranslation(offset.X, offset.Y)))
        {
            if (filled)
            {
                context.DrawGeometry(brush, null, shape);
            }
            else
            {
                context.DrawGeometry(null, new Pen(brush, 1.25, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), shape);
            }
        }
    }
}

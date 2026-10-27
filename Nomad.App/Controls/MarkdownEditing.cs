using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Nomad.Core.Text;

namespace Nomad.App.Controls;

/// <summary>
/// List writing in a text box (see <see cref="MarkdownLists"/>): Shift+Enter in a list item starts the next one
/// (Enter itself still sends), and in an empty one ends the list; Tab and Shift+Tab nest and un-nest the items
/// the caret or selection is on; Backspace right after a marker takes it away. Each is one undo step.
/// </summary>
public static class MarkdownEditing
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("IsEnabled", typeof(MarkdownEditing));

    static MarkdownEditing()
    {
        IsEnabledProperty.Changed.AddClassHandler<TextBox>((box, e) =>
        {
            box.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            if (e.GetNewValue<bool>())
            {
                box.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            }
        });
    }

    public static bool GetIsEnabled(TextBox box) => box.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(TextBox box, bool value) => box.SetValue(IsEnabledProperty, value);

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || sender is not TextBox { IsReadOnly: false } box)
        {
            return;
        }

        var text = box.Text ?? "";
        var start = box.SelectionStart;
        var end = box.SelectionEnd;
        var edit = (e.Key, e.KeyModifiers) switch
        {
            (Key.Enter, KeyModifiers.Shift) when start == end => MarkdownLists.NewLine(text, box.CaretIndex, box.NewLine),
            (Key.Tab, KeyModifiers.None) => MarkdownLists.Indent(text, start, end, outdent: false),
            (Key.Tab, KeyModifiers.Shift) => MarkdownLists.Indent(text, start, end, outdent: true),
            (Key.Back, KeyModifiers.None) when start == end => MarkdownLists.Backspace(text, box.CaretIndex),
            _ => null,
        };

        if (edit is { } change)
        {
            Apply(box, change);
            e.Handled = true;
        }
    }

    private static void Apply(TextBox box, TextEdit edit)
    {
        // Through the selection, as typing would: the text box keeps its undo history and bindings.
        box.SelectionStart = edit.Start;
        box.SelectionEnd = edit.Start + edit.Length;
        box.SelectedText = edit.Insert;

        // The caret first (it collapses the selection), then the selection's other end.
        box.CaretIndex = edit.SelectionEnd;
        box.SelectionStart = edit.SelectionStart;
    }
}

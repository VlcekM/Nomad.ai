using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;

namespace Nomad.Mobile.Views;

/// <summary>
/// What the phone's keyboard is told about a text box: its whole text, with the caret and selection counted from the start.
/// Avalonia's own (11.3) tells it only the wrapped line the caret is on. So when a word ends exactly at a line's end, the
/// keyboard sees the next line start empty, takes it for a new sentence (a capital), and its word in progress, now counted
/// from the wrong place, lands twice ("You are TThe best").
/// </summary>
public sealed class WholeTextInputClient : TextInputMethodClient
{
    private static readonly ConditionalWeakTable<TextBox, WholeTextInputClient> Clients = new();

    private readonly TextBox _box;
    private TextPresenter? _presenter;

    private WholeTextInputClient(TextBox box)
    {
        _box = box;
        _box.PropertyChanged += OnBoxPropertyChanged;
        _box.Tapped += (_, _) => RaiseInputPaneActivationRequested();

        // After TextBox's own class handler, which hands out its line-by-line client.
        _box.AddHandler(InputElement.TextInputMethodClientRequestedEvent, (_, e) =>
        {
            if (!_box.IsReadOnly)
            {
                e.Client = this;
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Every text box in the app uses it: called once at startup.</summary>
    public static void UseForAllTextBoxes() =>
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<TextBox>((box, e) =>
            Clients.GetValue(box, b => new WholeTextInputClient(b)).SetPresenter(e.NameScope.Find<TextPresenter>("PART_TextPresenter")));

    public override Visual TextViewVisual => _presenter!;

    public override bool SupportsPreedit => true;

    public override bool SupportsSurroundingText => true;

    public override string SurroundingText => _box.Text ?? "";

    public override Rect CursorRectangle =>
        _presenter is not null && _presenter.TransformToVisual(_box) is { } toBox
            ? _presenter.TextLayout.HitTestTextPosition(_box.CaretIndex).TransformToAABB(toBox)
            : default;

    public override TextSelection Selection
    {
        get => new(_box.SelectionStart, _box.SelectionEnd);
        set
        {
            var length = SurroundingText.Length;
            _box.SelectionStart = Math.Clamp(value.Start, 0, length);
            _box.SelectionEnd = Math.Clamp(value.End, 0, length);
            RaiseSelectionChanged();
        }
    }

    public override void SetPreeditText(string? preeditText) => SetPreeditText(preeditText, null);

    public override void SetPreeditText(string? preeditText, int? cursorPos)
    {
        if (_presenter is not null)
        {
            _presenter.SetCurrentValue(TextPresenter.PreeditTextProperty, preeditText);
            _presenter.SetCurrentValue(TextPresenter.PreeditTextCursorPositionProperty, cursorPos);
        }
    }

    public override void ExecuteContextMenuAction(ContextMenuAction action)
    {
        base.ExecuteContextMenuAction(action);
        switch (action)
        {
            case ContextMenuAction.Copy:
                _box.Copy();
                break;
            case ContextMenuAction.Cut:
                _box.Cut();
                break;
            case ContextMenuAction.Paste:
                _box.Paste();
                break;
            case ContextMenuAction.SelectAll:
                _box.SelectAll();
                break;
        }
    }

    private void SetPresenter(TextPresenter? presenter)
    {
        if (_presenter is not null)
        {
            _presenter.ClearValue(TextPresenter.PreeditTextProperty);
            _presenter.CaretBoundsChanged -= OnCaretBoundsChanged;
        }

        _presenter = presenter;
        if (_presenter is not null)
        {
            _presenter.CaretBoundsChanged += OnCaretBoundsChanged;
        }

        RaiseTextViewVisualChanged();
        RaiseCursorRectangleChanged();
    }

    private void OnCaretBoundsChanged(object? sender, EventArgs e) => RaiseCursorRectangleChanged();

    private void OnBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBox.TextProperty)
        {
            RaiseSurroundingTextChanged();
        }
        else if (e.Property == TextBox.SelectionStartProperty || e.Property == TextBox.SelectionEndProperty)
        {
            RaiseSelectionChanged();
        }
    }
}

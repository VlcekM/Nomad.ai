using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Nomad.App.Controls;
using Nomad.App.Ide;
using Nomad.App.ViewModels;
using Nomad.Core.Git;

namespace Nomad.App.Views;

public partial class FilesView : UserControl
{
    private SessionViewModel? _session;
    private bool _attached;
    private double? _gripOffset;
    private CodeSearchViewModel? _codeSearch;

    public FilesView()
    {
        InitializeComponent();
        Tree.FileOpened += (path, keep) => _session?.Files.Open(path, keep);
        Tree.DiffRequested += path => _session?.Files.OpenDiff(path);
        Tree.ContextRequested += OnTreeContext;
        Tree.ActionRequested += (node, action) =>
        {
            if (action == "rename")
            {
                _session?.Files.Rename(node.FullPath);
            }
            else
            {
                _session?.Files.Delete(node.FullPath);
            }
        };
    }

    // ------------------------------------------------------------------ the tree's menu

    // A right-click on a file or folder (or below them all: the folder itself), as Solution Explorer's menu.
    private void OnTreeContext(FileNode? node)
    {
        if (_session?.Files is not { Tree: { } tree } files)
        {
            return;
        }

        node ??= tree.Root;
        var path = node.FullPath;
        var isRoot = node == tree.Root;
        var folder = node.IsDirectory ? path : Path.GetDirectoryName(path)!;

        MenuItem Item(string header, Action action, string? gesture = null)
        {
            var item = new MenuItem { Header = header };
            if (gesture is not null)
            {
                item.InputGesture = KeyGesture.Parse(gesture);
            }

            item.Click += (_, _) => action();
            return item;
        }

        var menu = new ContextMenu();
        if (!node.IsDirectory)
        {
            menu.Items.Add(Item("Open", () => files.Open(path, keep: true)));
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(Item("New file…", () => files.NewFile(folder)));
        menu.Items.Add(Item("New folder…", () => files.NewFolder(folder)));
        if (!isRoot)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Rename…", () => files.Rename(path), "F2"));
            menu.Items.Add(Item("Delete", () => files.Delete(path), "Delete"));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Copy path", () => _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(path)));
        if (!isRoot)
        {
            menu.Items.Add(Item("Copy relative path", () => _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(Path.GetRelativePath(tree.Root.FullPath, path))));
        }

        menu.Items.Add(Item("Reveal in File Explorer", () => Markdown.LinkSupport.ShowInExplorer(path)));
        menu.Items.Add(Item("Open in terminal", () => OpenTerminal(folder)));

        // Git, for what has changes: a file's own, a folder's below it.
        if (tree.Git is { } git)
        {
            var state = node.IsDirectory ? GitFileState.None : git.StateOf(path);
            var counts = node.IsDirectory ? git.CountsUnder(path) : default;
            var changed = state is GitFileState.Modified or GitFileState.Added or GitFileState.Removed || counts != default;
            if (changed)
            {
                menu.Items.Add(new Separator());
                if (state == GitFileState.Modified)
                {
                    menu.Items.Add(Item("Compare with last commit", () => files.OpenDiff(path)));
                }

                menu.Items.Add(Item("Stage", () => _ = files.StageAsync(path)));
                menu.Items.Add(Item("Unstage", () => _ = files.UnstageAsync(path)));
                if (state != GitFileState.Added && (counts.Modified > 0 || counts.Removed > 0 || state == GitFileState.Modified))
                {
                    menu.Items.Add(Item("Undo changes…", () => files.Revert(path)));
                }

                menu.Items.Add(Item("Commit…", () => files.Commit(path)));
            }
        }

        menu.Closed += (_, _) => Tree.Focus();
        menu.Open(Tree);
    }

    /// <summary>Windows Terminal in the folder, or a console where it is not installed.</summary>
    private static void OpenTerminal(string folder)
    {
        if (Program.Dev.ScreenshotPath is not null)
        {
            Console.Error.WriteLine($"OpenTerminal {folder}");
            return;
        }

        static bool Start(ProcessStartInfo start)
        {
            try
            {
                Process.Start(start)?.Dispose();
                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return false;
            }
        }

        if (!Start(new ProcessStartInfo("wt.exe") { ArgumentList = { "-d", folder }, UseShellExecute = true }))
        {
            Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = folder, UseShellExecute = true });
        }
    }

    // ------------------------------------------------------------------ the question card

    private void OnPromptChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FilePromptViewModel.IsOpen) || _session?.Files.Prompt is not { } prompt)
        {
            return;
        }

        if (prompt.IsOpen)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (prompt.HasInput)
                {
                    PromptBox.Focus();
                    PromptBox.SelectionStart = 0;
                    PromptBox.SelectionEnd = prompt.SelectLength;
                }
                else
                {
                    PromptConfirm.Focus();
                }
            }, DispatcherPriority.Loaded);
        }
        else if (PromptCard.IsKeyboardFocusWithin)
        {
            Tree.Focus();
        }
    }

    private void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (_session?.Files.Prompt is not { } prompt)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            prompt.Cancel();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            prompt.ConfirmCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPromptOutsidePressed(object? sender, PointerPressedEventArgs e)
    {
        _session?.Files.Prompt.Cancel();
        e.Handled = true;
    }

    private MainViewModel? Main => TopLevel.GetTopLevel(this)?.DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_session is not null)
        {
            _session.PropertyChanged -= OnSessionChanged;
            _session.Files.Prompt.PropertyChanged -= OnPromptChanged;
            _session.Files.Prompt.Cancel();
            _session.Files.Show(false);
        }

        _codeSearch?.Close();
        if (_codeSearch is not null)
        {
            _codeSearch.PropertyChanged -= OnCodeSearchChanged;
        }

        _session = DataContext as SessionViewModel;
        _codeSearch = _session?.Files.CodeSearch;
        if (_session is not null)
        {
            _session.PropertyChanged += OnSessionChanged;
            _session.Files.Prompt.PropertyChanged += OnPromptChanged;
        }

        if (_codeSearch is not null)
        {
            _codeSearch.PropertyChanged += OnCodeSearchChanged;
        }

        ApplySide();
        ApplyShown();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        ApplyShown();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        ApplyShown();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.IsFilesView))
        {
            ApplyShown();
            if (_session is { IsFilesView: false })
            {
                _session.Files.CodeSearch.Close();
            }

            // Back to the chat: typing goes to its composer again, not to the tree fading out.
            if (_session is { IsFilesView: false } && (Parent as Panel)?.Children.OfType<SessionView>().FirstOrDefault() is { } chat)
            {
                chat.FocusComposerSoon();
            }
        }
        else if (e.PropertyName == nameof(SessionViewModel.FileTreeOnRight))
        {
            ApplySide();
        }
    }

    // The mouse's back and forward buttons go back and forth between the places jumped to.
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsXButton1Pressed)
        {
            _session?.Files.NavigateBack();
            e.Handled = true;
        }
        else if (props.IsXButton2Pressed)
        {
            _session?.Files.NavigateForward();
            e.Handled = true;
        }
    }

    // Ctrl+W closes the tab shown in the focused group; Ctrl+S saves it.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control && _session?.Files.ActiveGroup is { ActiveTab: { } edited })
        {
            edited.SaveCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.W && e.KeyModifiers == KeyModifiers.Control && _session?.Files.ActiveGroup is { ActiveTab: { } tab } group)
        {
            group.CloseCommand.Execute(tab);
            e.Handled = true;
        }
    }

    // The code search opening takes the focus to its box; closing gives it back to the file (or the tree).
    private void OnCodeSearchChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_codeSearch is not { } search)
        {
            return;
        }

        if (e.PropertyName == nameof(CodeSearchViewModel.IsOpen))
        {
            if (search.IsOpen)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    CodeSearchBox.Focus();
                    if (search.Query.StartsWith(':'))
                    {
                        // Ctrl+G: the line number goes after the ":".
                        CodeSearchBox.ClearSelection();
                        CodeSearchBox.CaretIndex = search.Query.Length;
                    }
                    else
                    {
                        CodeSearchBox.SelectAll();
                    }
                }, DispatcherPriority.Loaded);
            }
            else if (CodeSearchBox.IsFocused)
            {
                Tree.Focus();
            }
        }
        else if (e.PropertyName == nameof(CodeSearchViewModel.SelectedIndex) && search.SelectedIndex is >= 0 and var index)
        {
            // The arrows keep the selected row in sight (the first one scrolls back to the top).
            Dispatcher.UIThread.Post(() =>
            {
                if (index == 0)
                {
                    CodeSearchScroll.Offset = default;
                }
                else
                {
                    CodeSearchList.ScrollIntoView(index);
                }
            }, DispatcherPriority.Loaded);
        }
    }

    private void OnCodeSearchOutsidePressed(object? sender, PointerPressedEventArgs e)
    {
        _codeSearch?.Close();
        e.Handled = true;
    }

    private void OnCodeSearchResultClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CodeSearchResult result })
        {
            _codeSearch?.OpenResult(result);
        }
    }

    /// <summary>The tree watches the folder only while this view is on screen.</summary>
    private void ApplyShown()
    {
        if (_session is { } session)
        {
            var shown = _attached && session.IsFilesView;
            session.Files.Show(shown);
            if (shown)
            {
                Tree.Focus();
            }
        }
    }

    /// <summary>The tree on the right (its grip on its left edge) or on the left (the grip on its right).</summary>
    private void ApplySide()
    {
        var right = _session?.FileTreeOnRight ?? true;
        var column = right ? 2 : 0;
        Grid.SetColumn(TreePane, column);
        Grid.SetColumn(TreeGrip, column);
        TreePane.Margin = right ? new Thickness(0, 8, 8, 12) : new Thickness(8, 8, 0, 12);
        Documents.Margin = right ? new Thickness(8, 6, 4, 12) : new Thickness(4, 6, 8, 12);
        TreeGrip.HorizontalAlignment = right ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        TreeGrip.Margin = right ? new Thickness(-4, 8, 0, 12) : new Thickness(0, 8, -4, 12);
        TreeGripLine.HorizontalAlignment = right ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        TreeGripLine.Margin = right ? new Thickness(3, 0, 0, 0) : new Thickness(0, 0, 3, 0);
    }

    private bool TreeOnRight => _session?.FileTreeOnRight ?? true;

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Main is not { } main || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        if (e.ClickCount == 2)
        {
            main.FileTreeWidth = MainViewModel.DefaultFileTreeWidth;
            main.SaveSettings();
            return;
        }

        var x = e.GetPosition(this).X;
        _gripOffset = TreeOnRight ? main.FileTreeWidth + x : main.FileTreeWidth - x;
        TreeGrip.Classes.Add("dragging");
        e.Pointer.Capture(TreeGrip);
    }

    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        if (_gripOffset is { } offset && Main is { } main)
        {
            var x = e.GetPosition(this).X;
            main.FileTreeWidth = TreeOnRight ? offset - x : offset + x;
        }
    }

    private void OnGripReleased(object? sender, PointerReleasedEventArgs e) => EndGripDrag(e.Pointer);

    private void OnGripCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndGripDrag(null);

    private void EndGripDrag(IPointer? pointer)
    {
        if (_gripOffset is null)
        {
            return;
        }

        _gripOffset = null;
        pointer?.Capture(null);
        TreeGrip.Classes.Remove("dragging");
        Main?.SaveSettings();
    }
}

namespace Nomad.Mobile;

/// <summary>
/// Keeping a file the agent sent on the phone and opening it (Android: the Downloads folder's Nomad folder, opened in another app).
/// The tests use a fake.
/// </summary>
public interface IFileSaver
{
    /// <summary>Where saved files go, as the phone's Files app shows it ("Downloads › Nomad").</summary>
    string Where { get; }

    /// <summary>
    /// Saves the file under the name, as the type (which the phone then gives any app that opens it; another name is picked if it
    /// is taken); returns where it is, for <see cref="Open"/>.
    /// </summary>
    Task<string> SaveAsync(string name, string type, Stream content, CancellationToken ct = default);

    /// <summary>Opens a saved file in an app that shows <paramref name="type"/>; false when the phone has none.</summary>
    bool Open(string saved, string type);
}

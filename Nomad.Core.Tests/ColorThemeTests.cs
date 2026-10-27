using Nomad.Core.Settings;

namespace Nomad.Core.Tests;

public class ColorThemeTests
{
    [Fact]
    public void AThemeIsSavedAndListedByNameWithItsColors()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, $"themes-{Guid.NewGuid():N}");
        try
        {
            var id = ColorThemeStore.NewId(folder, "My Ocean", "nomad");
            ColorThemeStore.Save(folder, id, new CustomTheme { Name = "My Ocean", Dark = { ["Accent"] = "#4F9CF0" } });
            File.WriteAllText(Path.Combine(folder, "broken.json"), "{ not json");

            var themes = ColorThemeStore.LoadAll(folder);

            Assert.Equal("my-ocean", id);
            var (loadedId, theme) = Assert.Single(themes);
            Assert.Equal(id, loadedId);
            Assert.Equal("#4F9CF0", theme.Dark["Accent"]);
            Assert.Empty(theme.Light);
            Assert.Equal("my-ocean-2", ColorThemeStore.NewId(folder, "My Ocean", "nomad"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ANewThemeNeverTakesTheBuiltInThemesId()
    {
        Assert.Equal("nomad-2", ColorThemeStore.NewId(Path.Combine(AppContext.BaseDirectory, "no-such-folder"), "Nomad", "nomad"));
    }
}

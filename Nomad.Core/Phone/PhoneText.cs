namespace Nomad.Core.Phone;

/// <summary>Text shaping for the phone view's JSON.</summary>
public static class PhoneText
{
    /// <summary>At most <paramref name="max"/> characters and an ellipsis, never ending in half a surrogate pair (the JSON writer refuses one).</summary>
    public static string Clip(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var cut = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..cut] + "…";
    }
}

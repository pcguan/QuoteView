namespace StockClient.App.Views;

internal static class PanelBrightness
{
    public const int MaxShade = 10;

    // Keep the saved 0..10 scale; give the faint end more precision than a
    // linear fade (1%, 4%, 9% instead of 10%, 20%, 30%). Zero stays invisible.
    public static double ToOpacity(int shade)
    {
        var level = Math.Clamp(shade, 0, MaxShade) / (double)MaxShade;
        return level * level;
    }
}

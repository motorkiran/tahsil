using MudBlazor;

namespace Tahsil.Admin.Components.Layout;

internal static class AdminTheme
{
    public static readonly MudTheme Theme = new()
    {
        PaletteLight = new PaletteLight { Primary = "#0f766e", AppbarBackground = "#0f766e" },
        PaletteDark = new PaletteDark { Primary = "#2dd4bf" },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "8px" },
    };
}

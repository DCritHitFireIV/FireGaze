namespace FireGaze.PortraitPreview;

internal sealed record PortraitDye(byte ID, string Name, uint Color, byte Shade, byte SubOrder);
internal sealed record PortraitDyeGroup(byte Shade, string Name, IReadOnlyList<PortraitDye> Dyes);

internal static class PortraitDyePalette
{
    // Shade and SubOrder are game data, so future colors keep the game's own order.
    public static IReadOnlyList<PortraitDyeGroup> Group(IEnumerable<PortraitDye> dyes) => dyes
        .Where(dye => dye.ID != 0 && dye.Shade != 0)
        .GroupBy(dye => dye.Shade).OrderBy(group => group.Key)
        .Select(group => new PortraitDyeGroup(group.Key, Name(group.Key),
            group.OrderBy(dye => dye.SubOrder).ThenBy(dye => dye.ID).ToArray())).ToArray();

    private static string Name(byte shade) => shade switch
    {
        1 => "白色与黑色", 2 => "红色", 3 => "棕色", 4 => "黄色",
        5 => "绿色", 6 => "蓝色", 7 => "紫色", 8 => "特殊颜色",
        _ => $"颜色分类 {shade}",
    };
}

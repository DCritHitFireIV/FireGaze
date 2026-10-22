using FireGaze.PortraitPreview;

internal static class PortraitWorkspaceGeometryCases
{
    internal static void Run(Action<bool, string> check)
    {
#if !HAS_WORKSPACE_LAYOUT
        check(false, "workspace has a responsive body and reserved footer geometry");
#else
        foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
        foreach (var logicalWidth in new[] { 260f, 668f, 700f, 979f, 980f, 1120f, 1600f })
        foreach (var logicalHeight in new[] { 300f, 500f, 800f })
        {
            var width = logicalWidth * scale;
            var height = logicalHeight * scale;
            var layout = PortraitWorkspaceLayout.For(width, height, scale);
            var tag = $"{logicalWidth}x{logicalHeight}/{scale}x";
            check(layout.Stacked == (logicalWidth < 980), tag + ": uses the responsive breakpoint in logical pixels");
            check(layout.FooterHeight > 0 && layout.BodyHeight + layout.Gap + layout.FooterHeight <= height + .02f,
                tag + ": reserves visible footer space without growing beyond the content");
            foreach (var region in new[] { layout.Navigation, layout.Image, layout.Options })
                check(float.IsFinite(region.Width) && float.IsFinite(region.Height) && region.Width > 0 && region.Height > 0
                    && region.X >= 0 && region.Y >= 0 && region.X + region.Width <= width + .02f
                    && region.Y + region.Height <= layout.BodyHeight + .02f,
                    tag + ": body regions remain usable and inside the reserved workspace");
            if (layout.Stacked)
                check(layout.Image.Y == 0 && layout.Image.Y + layout.Image.Height < layout.Navigation.Y
                    && layout.Navigation.Y == layout.Options.Y && layout.Navigation.X + layout.Navigation.Width < layout.Options.X,
                    tag + ": narrow image precedes the navigation and editing row");
            else
                check(layout.Navigation.X == 0 && layout.Navigation.Y == layout.Image.Y && layout.Image.Y == layout.Options.Y
                    && layout.Navigation.X + layout.Navigation.Width < layout.Image.X
                    && layout.Image.X + layout.Image.Width < layout.Options.X,
                    tag + ": wide navigation image and options form three separate columns");
        }
#endif
    }
}

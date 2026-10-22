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
        foreach (var (width, height, scale) in new[] { (700f, 700f, 1f), (1120f, 700f, 1f), (1400f, 1400f, 2f), (2240f, 1400f, 2f) })
        {
            var visible = PortraitWorkspaceLayout.For(width, height, scale);
            var hidden = PortraitWorkspaceLayout.For(width, height, scale, false);
            check(hidden.Image.Width == 0 && hidden.Image.Height == 0,
                "hidden portrait does not reserve an invisible image cell");
            check(hidden.Navigation.Y == 0 && hidden.Options.Y == 0
                && hidden.Navigation.Height == hidden.BodyHeight && hidden.Options.Height == hidden.BodyHeight,
                "hiding image restores the whole editing height at wide and narrow sizes");
            check(hidden.Options.X == hidden.Navigation.Width + hidden.Gap
                && hidden.Options.X + hidden.Options.Width <= width + .02f
                && (visible.Stacked ? hidden.Options.Height > visible.Options.Height : hidden.Options.Width > visible.Options.Width),
                "the editing region receives all released image space without crossing bounds");
        }

        // Adjacent widths, immediate reversal and DPI conversion are gesture contracts.
        {
            var start = PortraitWorkspaceLayout.For(1400, 700, 1, true, 300, 500);
            (float Navigation, float Image) Drag(PortraitWorkspaceGeometry geometry, int index, float delta)
                => PortraitWorkspaceLayout.MoveDivider(geometry, 1f, index, delta);
            var first = Drag(start, 0, 10000);
            var limited = PortraitWorkspaceLayout.For(1400, 700, 1, true, first.Navigation, first.Image);
            check(limited.Navigation.Width == 580 && limited.Image.Width == 220 && limited.Options.Width == 576,
                "first divider stops at the adjacent image minimum without moving the options column");
            var reverse = Drag(limited, 0, -10);
            check(reverse.Navigation == 570 && reverse.Image == 230,
                "reversing at a limit moves immediately without repaying an accumulated overshoot");
            var second = Drag(start, 1, 10000);
            var rightLimited = PortraitWorkspaceLayout.For(1400, 700, 1, true, second.Navigation, second.Image);
            check(rightLimited.Navigation.Width == 300 && rightLimited.Image.Width == 756 && rightLimited.Options.Width == 320,
                "second divider preserves navigation while protecting the options minimum");
            var dpi = PortraitWorkspaceLayout.For(2800, 1400, 2, true, 300, 500);
            var high = PortraitWorkspaceLayout.MoveDivider(dpi, 2f, 0, 40f);
            check(high.Navigation == 320 && high.Image == 480,
                "physical drag distance is converted to logical column widths at high DPI");
            foreach (var font in new[] { 13f, 14f, 19f, 20f, 24f, 26f, 28f })
            {
                var scale = font / 17f;
                var minimum = PortraitWorkspaceLayout.For(1120 * scale, 700 * scale, scale, true, 160, 220);
                try
                {
                    var moved = PortraitWorkspaceLayout.MoveDivider(minimum, scale, 0, 100);
                    check(float.IsFinite(moved.Navigation) && moved.Navigation >= 159.999f && moved.Image >= 219.999f,
                        $"font {font}: adjacent minima tolerate float rounding while dragging");
                }
                catch (ArgumentException)
                { check(false, $"font {font}: rounded maximum cannot fall below the minimum drag bound"); }
            }
        }
        PortraitWorkspaceSplitterCases.Run(check);
        var dragged = PortraitWorkspaceLayout.For(1400, 700, 1, true, 300, 500);
        check(MathF.Abs(dragged.Navigation.Width - 300) < .01f && MathF.Abs(dragged.Image.Width - 500) < .01f,
            "wide layout preserves independently dragged navigation and image widths");
        check(dragged.Navigation.Width >= PortraitWorkspaceLayout.MinimumNavigationWidth
            && dragged.Image.Width >= PortraitWorkspaceLayout.MinimumImageWidth
            && dragged.Options.Width >= PortraitWorkspaceLayout.MinimumOptionsWidth,
            "wide layout keeps every column above its minimum width");
        check(dragged.Options.X + dragged.Options.Width <= 1400.01f,
            "wide layout keeps dragged columns inside the available width");

        var clamped = PortraitWorkspaceLayout.For(1120, 700, 1, true, 900, 900);
        check(clamped.Navigation.Width >= PortraitWorkspaceLayout.MinimumNavigationWidth
            && clamped.Image.Width >= PortraitWorkspaceLayout.MinimumImageWidth
            && clamped.Options.Width >= PortraitWorkspaceLayout.MinimumOptionsWidth,
            "oversized splitter drags clamp all columns to usable minimums");
        check(clamped.Options.Width >= 3 * 80f + 2 * 8f,
            "options minimum leaves room for the three equipment filter controls");
        check(clamped.Navigation.X + clamped.Navigation.Width <= clamped.Image.X + .01f
            && clamped.Image.X + clamped.Image.Width <= clamped.Options.X + .01f
            && clamped.Options.X + clamped.Options.Width <= 1120.01f,
            "clamped splitter drags never overlap or overflow");

        var narrowDragged = PortraitWorkspaceLayout.For(700, 700, 1, true, 300, 500);
        check(narrowDragged.Stacked && narrowDragged.Image.Y == 0,
            "narrow windows retain the existing stacked layout despite splitter state");

        var hiddenDragged = PortraitWorkspaceLayout.For(1120, 700, 1, false, 420, 500);
        check(!hiddenDragged.Stacked && MathF.Abs(hiddenDragged.Navigation.Width - 420) < .01f,
            "hiding the portrait retains a draggable navigation-to-options split");
        check(hiddenDragged.Options.Width >= PortraitWorkspaceLayout.MinimumOptionsWidth
            && hiddenDragged.Options.X + hiddenDragged.Options.Width <= 1120.01f,
            "hidden portrait split remains usable and inside the window");
#endif
    }
}

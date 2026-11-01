using Godot;
using MegaCrit.Sts2.Core.Localization.Fonts;

namespace SpireRewind;

// Builds the mod's controls from the game's own resources, loaded from its pck at runtime (fonts, the HSV
// shader, button art, tickbox sprites, paginator arrows). Nothing is copied into the mod. Every resource is
// optional: when one is missing the control falls back to an opaque, readable plain Godot control.
internal static class Style
{
    public static readonly Color Cream = new("FFF6E2");
    public static readonly Color Gold = new("EFC851");
    public static readonly Color Dim = new(1f, 0.964706f, 0.886275f, 0.7f);
    public static readonly Color Shadow = new(0f, 0f, 0f, 0.25f);
    public static readonly Color Highlight = new(0.172549f, 0.345098f, 0.439216f);    // dropdown item highlight
    public static readonly Color RowHover = new(1f, 0.6392157f, 0.44705883f, 0.2509804f); // tickbox row highlight
    public static readonly Color Track = new(0.164706f, 0.290196f, 0.321569f);        // scrollbar track
    public static readonly Color PanelColor = new(0.02f, 0.055f, 0.085f, 0.97f);
    public static readonly Color TealOutline = new(0.144f, 0.3312f, 0.36f);
    public static readonly Color GreenOutline = new(0.121333f, 0.28f, 0f);
    public static readonly Color RedOutline = new(0.36f, 0.1944f, 0.144f);
    public static readonly Color GoldOutline = new(0.33f, 0.2475f, 0f);
    public static readonly Color Gray = new(0.5f, 0.5f, 0.5f);

    public enum Tone { Teal, Green, Red }

    private static readonly Dictionary<string, Resource?> cache = new();

    internal static T? Load<T>(string path) where T : Resource
    {
        if (!cache.TryGetValue(path, out var resource))
        {
            try { resource = ResourceLoader.Load<T>(path, null, ResourceLoader.CacheMode.Reuse); }
            catch (Exception ex) { RewindMod.Error(ex); resource = null; }
            cache[path] = resource;
        }
        return resource as T;
    }

    public static bool Cjk => Text.CurrentLanguage is "zht" or "zhs";

    // The game's own zhs substitution font (FontManager, used by every MegaLabel) is Noto Sans Mono CJK SC: its Latin
    // letters, digits and spaces are half-width, so mixed text such as "HP 55/80" looks widely spaced. The game ships
    // no proportional Simplified Chinese face, but its Japanese face is the proportional Noto Sans CJK with the full
    // Pan-CJK glyph set (same design, same 0.95 scale and baseline). The label's language tag makes the text server
    // pick the Simplified or Traditional glyph forms. The game's zhs font stays as the fallback. MegaLabel uses the
    // Regular face for everything, so the mod does too.
    private const string ProportionalCjk = "res://themes/fonts/jpn/noto_sans_cjkjp_regular_shared.tres";
    private static Font? CjkFont()
    {
        var font = Load<Font>(ProportionalCjk);
        if (font != null) return font;
        try { font = FontManager.GetSubstituteFont("zhs", FontType.Regular); } catch (Exception ex) { RewindMod.Error(ex); }
        return font ?? Load<Font>("res://themes/fonts/zhs/noto_sans_mono_cjksc_regular_shared.tres");
    }

    // BCP 47 tag for text shaping (glyph forms and line breaking); empty restores the game's locale.
    private static string LanguageTag => Text.CurrentLanguage switch { "zht" => "zh-Hant", "zhs" => "zh-Hans", _ => "" };

    private static Font? Kreon(bool bold) =>
        Load<Font>(bold ? "res://themes/kreon_bold_glyph_space_one.tres" : "res://themes/kreon_regular_glyph_space_one.tres");

    public static Font? UiFont(bool bold) => Cjk ? CjkFont() ?? Kreon(bold) : Kreon(bold);

    // For labels that belong to the game's own nodes: only replace the font for Chinese, restore Kreon afterwards.
    public static void ApplyFont(Label label, bool bold)
    {
        const string marker = "spire_rewind_font";
        if (Cjk)
        {
            var font = CjkFont();
            if (font != null) { label.AddThemeFontOverride("font", font); label.Language = LanguageTag; label.SetMeta(marker, true); }
        }
        else if (label.HasMeta(marker))
        {
            var font = Kreon(bold);
            if (font != null) label.AddThemeFontOverride("font", font);
            label.Language = "";
            label.RemoveMeta(marker);
        }
    }

    public static void Apply(Label label, int size, bool bold, Color color, Color? outline = null)
    {
        var font = UiFont(bold);
        if (font != null) label.AddThemeFontOverride("font", font);
        label.Language = LanguageTag;
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_shadow_color", Shadow);
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        label.AddThemeConstantOverride("shadow_outline_size", 0);
        if (outline is { } o)
        {
            label.AddThemeColorOverride("font_outline_color", o);
            label.AddThemeConstantOverride("outline_size", size >= 30 ? 12 : 8);
        }
        else label.AddThemeConstantOverride("outline_size", 0);
    }

    public static Label Label(Node parent, string text, int size = 22, bool bold = false, Color? color = null,
        Color? outline = null, HorizontalAlignment align = HorizontalAlignment.Left, bool wrap = true)
    {
        var label = new Label { Text = text, HorizontalAlignment = align, MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center };
        if (wrap) label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        else label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        Apply(label, size, bold, color ?? Cream, outline);
        parent.AddChild(label);
        return label;
    }

    public static Label Title(Node parent, string text, int size = 34) => Label(parent, text, size, true, Gold, GoldOutline);

    public static void Divider(Node parent)
    {
        var line = new ColorRect { Color = new Color(Cream, 0.18f), CustomMinimumSize = new Vector2(0, 2), MouseFilter = Control.MouseFilterEnum.Ignore };
        parent.AddChild(line);
    }

    public static Control Spacer(Node parent, bool vertical = false)
    {
        var spacer = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        if (vertical) spacer.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        else spacer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        parent.AddChild(spacer);
        return spacer;
    }

    public static HBoxContainer Row(Node parent, int separation = 12)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", separation);
        parent.AddChild(row);
        return row;
    }

    public static ShaderMaterial? Hsv(float h, float s, float v)
    {
        var shader = Load<Shader>("res://shaders/hsv.gdshader");
        if (shader == null) return null;
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("h", h);
        material.SetShaderParameter("s", s);
        material.SetShaderParameter("v", v);
        return material;
    }

    private static void Flat(Button button)
    {
        var empty = new StyleBoxEmpty();
        foreach (string name in new[] { "normal", "hover", "pressed", "focus", "disabled", "hover_pressed" })
            button.AddThemeStyleboxOverride(name, empty);
    }

    public const float PanelHeight = 960, ConfirmHeight = 600;

    public static void Resize(Control panel, float height)
    {
        panel.OffsetTop = -height / 2; panel.OffsetBottom = height / 2;
    }

    // Opaque dialog panel centred on the 1920x1080 canvas; returns the panel and the container for its content.
    public static (Control panel, MarginContainer content) Panel(Control screen, float width = 1120)
    {
        var panel = new Control { Name = "Panel" };
        screen.AddChild(panel);
        panel.AnchorLeft = panel.AnchorRight = panel.AnchorTop = panel.AnchorBottom = 0.5f;
        panel.OffsetLeft = -width / 2; panel.OffsetRight = width / 2;
        Resize(panel, PanelHeight);
        var nine = Load<Texture2D>("res://images/ui/tiny_nine_patch.png");
        Control background = nine != null
            ? new NinePatchRect { Texture = nine, PatchMarginLeft = 14, PatchMarginTop = 14, PatchMarginRight = 13, PatchMarginBottom = 13, SelfModulate = PanelColor }
            : new ColorRect { Color = PanelColor };
        background.MouseFilter = Control.MouseFilterEnum.Ignore;
        panel.AddChild(background);
        background.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var margin = new MarginContainer();
        panel.AddChild(margin);
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 36); margin.AddThemeConstantOverride("margin_right", 36);
        margin.AddThemeConstantOverride("margin_top", 30); margin.AddThemeConstantOverride("margin_bottom", 30);
        return (panel, margin);
    }

    public static ScrollContainer Scroll(Node parent, float minHeight)
    {
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, minHeight), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        scroll.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        var bar = scroll.GetVScrollBar();
        bar.CustomMinimumSize = new Vector2(14, 0);
        var track = new StyleBoxFlat { BgColor = Track }; track.SetCornerRadiusAll(7);
        var grabber = new StyleBoxFlat { BgColor = new Color(Cream, 0.55f) }; grabber.SetCornerRadiusAll(5);
        var grabberLit = new StyleBoxFlat { BgColor = Gold }; grabberLit.SetCornerRadiusAll(5);
        foreach (var style in new[] { track, grabber, grabberLit }) { style.ContentMarginLeft = 2; style.ContentMarginRight = 2; }
        bar.AddThemeStyleboxOverride("scroll", track);
        bar.AddThemeStyleboxOverride("scroll_focus", track);
        bar.AddThemeStyleboxOverride("grabber", grabber);
        bar.AddThemeStyleboxOverride("grabber_highlight", grabberLit);
        bar.AddThemeStyleboxOverride("grabber_pressed", grabberLit);
        parent.AddChild(scroll);
        return scroll;
    }

    // Pause-menu style button: the game's button art as a scaled nine-patch, recoloured with its HSV shader.
    public static Button Button(Node parent, string text, Action action, Tone tone = Tone.Teal, float minWidth = 180,
        float height = 56, int fontSize = 24, bool expand = true)
    {
        var button = new Button { CustomMinimumSize = new Vector2(minWidth, height), FocusMode = Control.FocusModeEnum.All,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand, ClipContents = false };
        if (expand) button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        Flat(button);
        float hue = tone switch { Tone.Green => 0.85f, Tone.Red => 0.5f, _ => 1f };
        var outline = tone switch { Tone.Green => GreenOutline, Tone.Red => RedOutline, _ => TealOutline };
        ShaderMaterial? material = null;
        var art = Load<Texture2D>("res://images/ui/reward_screen/reward_item_button.png");
        if (art != null)
        {
            const float scale = 0.5f;
            var image = new NinePatchRect { Name = "Image", Texture = art, MouseFilter = Control.MouseFilterEnum.Ignore,
                PatchMarginLeft = 60, PatchMarginRight = 60, PatchMarginTop = 48, PatchMarginBottom = 48, Scale = new Vector2(scale, scale) };
            material = Hsv(hue, 0.8f, 0.9f);
            image.Material = material;
            button.AddChild(image);
            button.Resized += () => image.Size = button.Size / scale;
        }
        else
        {
            var fallback = new StyleBoxFlat { BgColor = tone switch { Tone.Green => new Color(0.2f, 0.45f, 0.25f), Tone.Red => new Color(0.55f, 0.25f, 0.25f), _ => new Color(0.2f, 0.42f, 0.45f) } };
            fallback.SetCornerRadiusAll(10);
            button.AddThemeStyleboxOverride("normal", fallback);
            button.AddThemeStyleboxOverride("disabled", fallback);
            var lit = (StyleBoxFlat)fallback.Duplicate(); lit.BgColor = fallback.BgColor.Lightened(0.15f);
            foreach (string name in new[] { "hover", "pressed", "focus", "hover_pressed" }) button.AddThemeStyleboxOverride(name, lit);
        }
        var label = new Label { Name = "Label", Text = text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        Apply(label, fontSize, true, Cream, outline);
        label.AddThemeConstantOverride("outline_size", 10);
        button.AddChild(label);
        label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        label.OffsetLeft = 14; label.OffsetRight = -14;
        bool hovered = false, focused = false;
        void Refresh()
        {
            bool lit = (hovered || focused) && !button.Disabled;
            material?.SetShaderParameter("s", lit ? 1.1f : 0.8f);
            material?.SetShaderParameter("v", lit ? 1.1f : 0.9f);
            label.AddThemeColorOverride("font_color", lit ? Gold : Cream);
        }
        button.MouseEntered += () => { hovered = true; Refresh(); };
        button.MouseExited += () => { hovered = false; Refresh(); };
        button.FocusEntered += () => { focused = true; Refresh(); };
        button.FocusExited += () => { focused = false; Refresh(); };
        button.Pressed += action;
        parent.AddChild(button);
        return button;
    }

    public static void SetText(Button button, string text)
    {
        var label = button.GetNodeOrNull<Label>("Label");
        if (label != null) { label.Text = text; Apply(label, label.GetThemeFontSize("font_size"), true, label.GetThemeColor("font_color"), label.GetThemeColor("font_outline_color")); label.AddThemeConstantOverride("outline_size", 10); }
        else button.Text = text;
    }

    public static void Enable(Button button, bool enabled)
    {
        button.Disabled = !enabled;
        button.Modulate = enabled ? Colors.White : Gray;
    }

    // List row (checkpoint, saved run, replay): two text lines, highlighted like the game's dropdown items.
    public static Button ListRow(Node parent, string title, string detail, string? right, Action? action)
    {
        var button = new Button { CustomMinimumSize = new Vector2(0, 62), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            FocusMode = action == null ? Control.FocusModeEnum.None : Control.FocusModeEnum.All,
            MouseDefaultCursorShape = action == null ? Control.CursorShape.Arrow : Control.CursorShape.PointingHand };
        var normal = new StyleBoxFlat { BgColor = new Color(1f, 1f, 1f, 0.05f) }; normal.SetCornerRadiusAll(6);
        var lit = new StyleBoxFlat { BgColor = Highlight }; lit.SetCornerRadiusAll(6);
        var pressed = new StyleBoxFlat { BgColor = Highlight.Darkened(0.2f) }; pressed.SetCornerRadiusAll(6);
        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("disabled", normal);
        button.AddThemeStyleboxOverride("hover", action == null ? normal : lit);
        button.AddThemeStyleboxOverride("focus", lit);
        button.AddThemeStyleboxOverride("pressed", pressed);
        button.AddThemeStyleboxOverride("hover_pressed", pressed);
        if (action == null) button.Disabled = true;
        var box = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 16);
        button.AddChild(box);
        box.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        box.OffsetLeft = 18; box.OffsetRight = -18;
        var lines = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore };
        lines.AddThemeConstantOverride("separation", -2);
        box.AddChild(lines);
        Label(lines, title, 24, true, wrap: false);
        Label(lines, detail, 19, false, Dim, wrap: false);
        if (right != null)
        {
            var time = Label(box, right, 19, false, Dim, align: HorizontalAlignment.Right, wrap: false);
            time.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
            time.TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming; // keeps its own width next to the expanding lines
        }
        if (action != null) button.Pressed += action;
        parent.AddChild(button);
        return button;
    }

    // Tickbox row with the game's checkbox sprites; ButtonPressed holds the value like a CheckBox.
    public static Button Tickbox(Node parent, string text, bool pressed)
    {
        var ticked = Load<Texture2D>("res://images/atlases/ui_atlas.sprites/checkbox_ticked.tres");
        var unticked = Load<Texture2D>("res://images/atlases/ui_atlas.sprites/checkbox_unticked.tres");
        if (ticked == null || unticked == null)
        {
            var check = new CheckBox { Text = text, ButtonPressed = pressed, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 48) };
            var font = UiFont(false);
            if (font != null) check.AddThemeFontOverride("font", font);
            check.Language = LanguageTag;
            check.AddThemeFontSizeOverride("font_size", 24);
            check.AddThemeColorOverride("font_color", Cream);
            parent.AddChild(check);
            return check;
        }
        var button = new Button { ToggleMode = true, ButtonPressed = pressed, CustomMinimumSize = new Vector2(0, 50),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.All, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        Flat(button);
        var lit = new StyleBoxFlat { BgColor = RowHover }; lit.SetCornerRadiusAll(6);
        foreach (string name in new[] { "hover", "focus", "hover_pressed" }) button.AddThemeStyleboxOverride(name, lit);
        var box = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 10);
        button.AddChild(box);
        box.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        box.OffsetLeft = 6; box.OffsetRight = -6;
        var icon = new TextureRect { Texture = pressed ? ticked : unticked, CustomMinimumSize = new Vector2(44, 44), MouseFilter = Control.MouseFilterEnum.Ignore,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        box.AddChild(icon);
        Label(box, text, 24);
        button.Toggled += on => icon.Texture = on ? ticked : unticked;
        parent.AddChild(button);
        return button;
    }

    private static Button Arrow(Node parent, bool left, Action action)
    {
        var button = new Button { CustomMinimumSize = new Vector2(48, 48), FocusMode = Control.FocusModeEnum.All, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        Flat(button);
        var texture = Load<Texture2D>(left ? "res://images/atlases/ui_atlas.sprites/settings_tiny_left_arrow.tres" : "res://images/atlases/ui_atlas.sprites/settings_tiny_right_arrow.tres");
        Control visual;
        if (texture != null)
        {
            var image = new TextureRect { Texture = texture, MouseFilter = Control.MouseFilterEnum.Ignore, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, Modulate = Cream };
            button.AddChild(image);
            image.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            image.OffsetLeft = image.OffsetTop = 6; image.OffsetRight = image.OffsetBottom = -6;
            visual = image;
        }
        else
        {
            var label = new Label { Text = left ? "<" : ">", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
            Apply(label, 30, true, Cream);
            button.AddChild(label);
            label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            visual = label;
        }
        bool hovered = false, focused = false;
        void Refresh() => visual.Modulate = hovered || focused ? Gold : Cream;
        button.MouseEntered += () => { hovered = true; Refresh(); };
        button.MouseExited += () => { hovered = false; Refresh(); };
        button.FocusEntered += () => { focused = true; Refresh(); };
        button.FocusExited += () => { focused = false; Refresh(); };
        button.Pressed += action;
        parent.AddChild(button);
        return button;
    }

    // Settings-screen style value picker: < value >. Each step callback returns the new text.
    public static Label Paginator(Node parent, string initial, Func<string> stepLeft, Func<string> stepRight, float width = 220)
    {
        var box = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        box.AddThemeConstantOverride("separation", 2);
        parent.AddChild(box);
        Label? value = null;
        Arrow(box, true, () => { if (value != null) value.Text = stepLeft(); });
        value = Label(box, initial, 24, true, align: HorizontalAlignment.Center, wrap: false);
        value.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        value.CustomMinimumSize = new Vector2(width, 48);
        Arrow(box, false, () => { if (value != null) value.Text = stepRight(); });
        return value;
    }
}

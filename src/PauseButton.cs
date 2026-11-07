using System;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace FIHSync;

/// A green "MOD SHARING" button (the mods' own look: accent green, Geist) in the game's menus: under Exit in the main
/// menu, and under Save and exit in the pause menu. It's our own object, not a copy of the game's button: a copy
/// brings the game's menu animation along, which kept snapping it back on top of the original. Where the menu places
/// its buttons by hand (the main menu), ours follows the original every frame, one step below it (the same step as
/// between the original and the button above it), so it slides in with the menu. In a layout group it's laid out as
/// the next item instead. It opens the Mod sharing panel.
internal static class PauseButton
{
    const string Text = "MOD SHARING";

    sealed class Slot
    {
        public string Where = "";
        public GameObject Button;
        public RectTransform Rect, Original, Above;
        public IntPtr Owner;
        public bool Failed, LaidOut;
    }

    static readonly Slot main = new() { Where = "main menu" }, pause = new() { Where = "pause menu" };
    static float nextLook;

    /// Whether a menu with our button is on screen.
    internal static bool MenuOpen => Shown(main) || Shown(pause);

    static bool Shown(Slot s) => s.Button != null && s.Button.activeInHierarchy;

    internal static void Tick()
    {
        if (Time.unscaledTime >= nextLook && (main.Button == null || pause.Button == null))
        {
            nextLook = Time.unscaledTime + 2f;
            if (main.Button == null && !main.Failed)
            {
                var m = Find<EHS.MainMenu.MainMenuManager>();
                if (m != null && m.Pointer != main.Owner && m.exitGameButton != null) Add(main, m.Pointer, m.exitGameButton);
            }
            if (pause.Button == null && !pause.Failed)
            {
                var p = Find<EHS.GamePauseMenuController>();
                if (p != null && p.Pointer != pause.Owner && p.saveAndExitButton != null) Add(pause, p.Pointer, p.saveAndExitButton);
            }
        }
        Follow(main);
        Follow(pause);
    }

    static T Find<T>() where T : Il2CppSystem.Object
    {
        var found = Object.FindObjectsOfType(Il2CppType.Of<T>());
        return found != null && found.Length > 0 ? found[0].TryCast<T>() : null;
    }

    /// Shows ours with the original and keeps it one step below it (in world space, so the menu's own animation and
    /// its wrappers don't matter).
    static void Follow(Slot s)
    {
        if (s.Button == null || s.Original == null) return;
        bool on = s.Original.gameObject.activeInHierarchy;
        if (s.Button.activeSelf != on) s.Button.SetActive(on);
        if (!on || s.LaidOut) return;
        var o = s.Original.position;
        Vector3 step;
        if (s.Above != null && s.Above.gameObject.activeInHierarchy)
        {
            var a = s.Above.position;
            step = new Vector3(o.x - a.x, o.y - a.y, 0f);
        }
        else step = s.Original.TransformVector(new Vector3(0f, -s.Original.rect.height * 1.5f, 0f));
        s.Rect.position = new Vector3(o.x + step.x, o.y + step.y, o.z);
    }

    /// The menu button just above the original: the nearest one higher up, lined up with it.
    static RectTransform FindAbove(RectTransform original)
    {
        var root = original.parent != null && original.parent.parent != null ? original.parent.parent : original.parent;
        if (root == null) return null;
        RectTransform best = null;
        float bestDy = float.MaxValue, h = Math.Abs(original.TransformVector(new Vector3(0f, original.rect.height, 0f)).y);
        var o = original.position;
        foreach (var b in root.GetComponentsInChildren<Button>(true))
        {
            var rt = b.transform.TryCast<RectTransform>();
            if (rt == null || rt.Pointer == original.Pointer || b.gameObject.name.StartsWith("FIHSync")) continue;
            var p = rt.position;
            float dy = p.y - o.y;
            if (dy > h * 0.3f && dy < bestDy && Math.Abs(p.x - o.x) < h * 6f) { best = rt; bestDy = dy; }
        }
        return best;
    }

    static void Add(Slot s, IntPtr owner, Button original)
    {
        try
        {
            s.Owner = owner;
            Ui.Build();
            var source = original.transform.TryCast<RectTransform>();
            var parent = source.parent;
            s.Original = source;
            s.LaidOut = false; // the main menu's column has a layout group, but the game moves its buttons itself: follow instead

            // the game's own button font (and its outline material), on our green pill
            var gameLabel = source.GetComponentInChildren<TMP_Text>(true);
            float h = Mathf.Clamp(source.rect.height * 0.8f, 40f, 90f);
            s.Rect = Ui.Node(parent, "FIHSync Mod sharing");
            s.Button = s.Rect.gameObject;
            s.Button.AddComponent<LayoutElement>().ignoreLayout = true;
            s.Rect.anchorMin = s.Rect.anchorMax = s.Rect.pivot = new Vector2(0.5f, 0.5f);
            var back = Ui.Fill(s.Rect, Ui.rounded, Ui.Accent, true);
            var label = Ui.Label(s.Rect, Text, Ui.semiBold, h * 0.5f, Ui.AccentText, 0f, 0f, h * 6f, h, TextAlignmentOptions.Center);
            if (gameLabel != null && gameLabel.font != null)
            {
                label.font = gameLabel.font;
                label.fontSharedMaterial = gameLabel.fontSharedMaterial;
                label.fontSize = Mathf.Min(gameLabel.fontSize * 0.8f, h * 0.7f);
            }
            float w = label.GetPreferredValues(Text).x + h * 1.2f;
            s.Rect.sizeDelta = new Vector2(w, h);
            label.rectTransform.sizeDelta = new Vector2(w, h);
            Ui.Click(s.Button, back, () => SharePanel.Open(true));
            s.Rect.SetAsLastSibling(); // drawn over the menu's backgrounds
            s.Above = FindAbove(source);
            Follow(s);
            Plugin.Logger.LogInfo($"added the Mod sharing button to the {s.Where}, {w:0}x{h:0}, font {(gameLabel != null && gameLabel.font != null ? gameLabel.font.name : "Geist")}, below {source.name}{(s.Above != null ? $", step from {s.Above.name}" : "")}");
        }
        catch (Exception e)
        {
            s.Failed = true;
            if (s.Button != null) Object.Destroy(s.Button);
            s.Button = null;
            Plugin.Logger.LogWarning($"couldn't add the Mod sharing button to the {s.Where} ({e.Message}): {Plugin.PanelKey.Value} opens it");
        }
    }
}

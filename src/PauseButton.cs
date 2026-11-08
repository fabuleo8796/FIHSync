using System;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace FIHSync;

/// A green "MOD SHARING" button (the mods' own look on the game's button font) under Exit in the main menu. It's our
/// own object, not a copy of the game's button: a copy brings the menu's animation along, which kept snapping it back
/// on top of Exit. It stays out of the menu's layout group and follows Exit every frame, one step below it (the same
/// step as between Exit and the button above it), so it slides in with the menu. In game, the panel key opens Mod
/// sharing instead (no pause menu button: nothing to search for while playing).
internal static class PauseButton
{
    const string Text = "MOD SHARING";
    static GameObject button;
    static RectTransform rect, original, above;
    static bool failed;
    static float nextLook;

    /// Whether the main menu (with our button) is on screen.
    internal static bool MenuOpen => button != null && button.activeInHierarchy;

    internal static void Tick()
    {
        // look for the menu only while the cursor is free (menus), never while playing: a scene-wide search is a hitch
        if (button == null && !failed && Cursor.lockState != CursorLockMode.Locked && Time.unscaledTime >= nextLook)
        {
            nextLook = Time.unscaledTime + 1f;
            var found = Object.FindObjectsOfType(Il2CppType.Of<EHS.MainMenu.MainMenuManager>());
            var m = found != null && found.Length > 0 ? found[0].TryCast<EHS.MainMenu.MainMenuManager>() : null;
            if (m != null && m.exitGameButton != null) Add(m.exitGameButton);
        }
        Follow();
    }

    /// Shows ours with Exit and keeps it one step below it (in world space, so the menu's animation doesn't matter).
    static void Follow()
    {
        if (button == null || original == null) return;
        bool on = original.gameObject.activeInHierarchy;
        if (button.activeSelf != on) button.SetActive(on);
        if (!on) return;
        var o = original.position;
        Vector3 step;
        if (above != null && above.gameObject.activeInHierarchy)
        {
            var a = above.position;
            step = new Vector3(o.x - a.x, o.y - a.y, 0f);
        }
        else step = original.TransformVector(new Vector3(0f, -original.rect.height * 1.5f, 0f));
        rect.position = new Vector3(o.x + step.x, o.y + step.y, o.z);
    }

    /// The menu button just above Exit: the nearest one higher up, lined up with it.
    static RectTransform FindAbove(RectTransform exit)
    {
        var root = exit.parent != null && exit.parent.parent != null ? exit.parent.parent : exit.parent;
        if (root == null) return null;
        RectTransform best = null;
        float bestDy = float.MaxValue, h = Math.Abs(exit.TransformVector(new Vector3(0f, exit.rect.height, 0f)).y);
        var o = exit.position;
        foreach (var b in root.GetComponentsInChildren<Button>(true))
        {
            var rt = b.transform.TryCast<RectTransform>();
            if (rt == null || rt.Pointer == exit.Pointer || b.gameObject.name.StartsWith("FIHSync")) continue;
            var p = rt.position;
            float dy = p.y - o.y;
            if (dy > h * 0.3f && dy < bestDy && Math.Abs(p.x - o.x) < h * 6f) { best = rt; bestDy = dy; }
        }
        return best;
    }

    static void Add(Button exit)
    {
        try
        {
            Ui.Build();
            original = exit.transform.TryCast<RectTransform>();
            var gameLabel = original.GetComponentInChildren<TMP_Text>(true);
            float h = Mathf.Clamp(original.rect.height * 0.8f, 40f, 90f);
            rect = Ui.Node(original.parent, "FIHSync Mod sharing");
            button = rect.gameObject;
            button.AddComponent<LayoutElement>().ignoreLayout = true; // the column has a layout group, but the game moves its buttons itself
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            var back = Ui.Fill(rect, Ui.rounded, Ui.Accent, true);
            var label = Ui.Label(rect, Text, Ui.semiBold, h * 0.5f, Ui.AccentText, 0f, 0f, h * 6f, h, TextAlignmentOptions.Center);
            if (gameLabel != null && gameLabel.font != null)
            {
                label.font = gameLabel.font;
                label.fontSharedMaterial = gameLabel.fontSharedMaterial;
                label.fontSize = Mathf.Min(gameLabel.fontSize * 0.8f, h * 0.7f);
            }
            float w = label.GetPreferredValues(Text).x + h * 1.2f;
            rect.sizeDelta = new Vector2(w, h);
            label.rectTransform.sizeDelta = new Vector2(w, h);
            Ui.Click(button, back, () => SharePanel.Open(true));
            rect.SetAsLastSibling(); // drawn over the menu's backgrounds
            above = FindAbove(original);
            Follow();
            Plugin.Logger.LogInfo($"added the Mod sharing button to the main menu, {w:0}x{h:0}, font {(gameLabel != null && gameLabel.font != null ? gameLabel.font.name : "Geist")}, below {original.name}{(above != null ? $", step from {above.name}" : "")}");
        }
        catch (Exception e)
        {
            failed = true;
            if (button != null) Object.Destroy(button);
            button = null;
            Plugin.Logger.LogWarning($"couldn't add the Mod sharing button to the main menu ({e.Message}): {Plugin.PanelKey.Value} opens it");
        }
    }
}

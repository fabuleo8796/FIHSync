using System;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace FIHSync;

/// "Mod sharing" buttons in the game's own menus: the main menu (a copy of its Exit game button) and the pause menu
/// (a copy of Save and exit), so they look native. Each is put just above the button it copies, with the game's text
/// and click handlers taken off, and opens the Mod sharing panel.
internal static class PauseButton
{
    const string Text = "Mod sharing";

    sealed class Slot
    {
        public string Where = "";
        public GameObject Button;
        public TMP_Text Label;
        public IntPtr Owner;
        public bool Failed;
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
        Relabel(main);
        Relabel(pause);
    }

    static void Relabel(Slot s)
    {
        if (s.Label != null && Shown(s) && s.Label.text != Text) s.Label.text = Text; // the game's localization may write it back
    }

    static T Find<T>() where T : Il2CppSystem.Object
    {
        var found = Object.FindObjectsOfType(Il2CppType.Of<T>());
        return found != null && found.Length > 0 ? found[0].TryCast<T>() : null;
    }

    /// In a layout group the copy just goes above the original. The main menu places its buttons by hand, so there the
    /// copy goes one step below the original, the same step as between the original and the button above it.
    static void Place(GameObject source, GameObject copy)
    {
        var parent = source.transform.parent;
        int index = source.transform.GetSiblingIndex();
        if (parent != null && parent.GetComponent<LayoutGroup>() != null)
        {
            copy.transform.SetSiblingIndex(index);
            return;
        }
        var from = source.transform.TryCast<RectTransform>();
        var to = copy.transform.TryCast<RectTransform>();
        if (from == null || to == null) return;
        float dx = 0f, dy = -120f;
        var above = index > 0 ? parent.GetChild(index - 1).TryCast<RectTransform>() : null;
        if (above != null)
        {
            float sx = from.anchoredPosition.x - above.anchoredPosition.x, sy = from.anchoredPosition.y - above.anchoredPosition.y;
            if (sy < -20f && sy > -400f) { dx = sx; dy = sy; }
        }
        var p = from.anchoredPosition;
        to.anchoredPosition = new Vector2(p.x + dx, p.y + dy);
        copy.transform.SetSiblingIndex(index + 1);
    }

    static void Add(Slot s, IntPtr owner, Button original)
    {
        try
        {
            s.Owner = owner;
            var source = original.gameObject;
            s.Button = Object.Instantiate(source.Cast<Object>(), source.transform.parent).Cast<GameObject>(); // the real (not rebuilt) overload
            s.Button.name = "FIHSync Mod sharing";
            Place(source, s.Button);
            foreach (var loc in s.Button.GetComponentsInChildren<EHS.LocalizedUIText>(true)) Object.Destroy(loc);
            var b = s.Button.GetComponent<Button>();
            b.onClick.RemoveAllListeners();
            for (int i = 0; i < b.onClick.GetPersistentEventCount(); i++) b.onClick.SetPersistentListenerState(i, UnityEventCallState.Off);
            b.onClick.AddListener((UnityAction)new Action(() => Ui.Safe(() => SharePanel.Open(true))));
            s.Label = s.Button.GetComponentInChildren<TMP_Text>(true);
            if (s.Label != null) s.Label.text = Text;
            Plugin.Logger.LogInfo($"added the Mod sharing button to the {s.Where}");
        }
        catch (Exception e)
        {
            s.Failed = true;
            Plugin.Logger.LogWarning($"couldn't add the Mod sharing button to the {s.Where} ({e.Message}): {Plugin.PanelKey.Value} opens it");
        }
    }
}

using System;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace FIHSync;

/// A "Mod sharing" button in the game's own pause menu: a copy of its Save and exit button (so it looks native), put
/// just above it, with the game's text and click handlers taken off. It opens the Mod sharing panel.
internal static class PauseButton
{
    const string Text = "Mod sharing";
    static GameObject button;
    static TMP_Text label;
    static IntPtr controller;
    static float nextLook;
    static bool failed;

    /// Whether the pause menu (with our button) is on screen.
    internal static bool MenuOpen => button != null && button.activeInHierarchy;

    internal static void Tick()
    {
        if (button == null && !failed && Time.unscaledTime >= nextLook)
        {
            nextLook = Time.unscaledTime + 2f;
            Add();
        }
        if (label != null && MenuOpen && label.text != Text) label.text = Text; // the game's localization may write it back
    }

    static void Add()
    {
        var found = Object.FindObjectsOfType(Il2CppType.Of<EHS.GamePauseMenuController>());
        if (found == null || found.Length == 0) return;
        var menu = found[0].TryCast<EHS.GamePauseMenuController>();
        if (menu == null || menu.Pointer == controller) return;
        var original = menu.saveAndExitButton;
        if (original == null) return;
        try
        {
            controller = menu.Pointer;
            var source = original.gameObject;
            button = Object.Instantiate(source.Cast<Object>(), source.transform.parent).Cast<GameObject>(); // the real (not rebuilt) overload
            button.name = "FIHSync Mod sharing";
            button.transform.SetSiblingIndex(source.transform.GetSiblingIndex());
            foreach (var loc in button.GetComponentsInChildren<EHS.LocalizedUIText>(true)) Object.Destroy(loc);
            var b = button.GetComponent<Button>();
            b.onClick.RemoveAllListeners();
            for (int i = 0; i < b.onClick.GetPersistentEventCount(); i++) b.onClick.SetPersistentListenerState(i, UnityEventCallState.Off);
            b.onClick.AddListener((UnityAction)new Action(() => Ui.Safe(() => SharePanel.Open(true))));
            label = button.GetComponentInChildren<TMP_Text>(true);
            if (label != null) label.text = Text;
            Plugin.Logger.LogInfo("added the Mod sharing button to the pause menu");
        }
        catch (Exception e)
        {
            failed = true;
            Plugin.Logger.LogWarning($"couldn't add the pause menu button ({e.Message}): {Plugin.PanelKey.Value} opens Mod sharing");
        }
    }
}

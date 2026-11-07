using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace FIHSync;

/// The Mod sharing panel: whose stuff you see (Everyone / Friends only / Nobody), whose videos play, sharing yours,
/// the lag shield, and every modded player in the lobby with Hide (this session), Block (for good) and Show anyway
/// (after the lag shield hid them). Opened from the pause menu button or the panel key.
internal static class SharePanel
{
    const float W = 520f, Pad = 28f, Inner = W - Pad * 2, RowsTop = 360f, RowH = 52f;
    const int MaxRows = 6;
    static GameObject root;
    static Ui.Segmented showSeg, videoSeg;
    static Ui.Switch shareSwitch, shieldSwitch;
    static TextMeshProUGUI empty, blockedText;
    static readonly List<Row> rows = new();
    static List<Share.PeerInfo> shown = new();
    static bool fromMenu;
    static float nextRefresh;
    static CursorLockMode lockWas;
    static bool cursorWas;

    sealed class Row
    {
        public GameObject Go;
        public TextMeshProUGUI Name, What, A, B;
    }

    internal static bool IsOpen => root != null && root.activeSelf;

    internal static void Open(bool menu)
    {
        if (root == null) Build();
        Ui.EnsureEventSystem();
        fromMenu = menu;
        if (!menu) { lockWas = Cursor.lockState; cursorWas = Cursor.visible; }
        root.SetActive(true);
        Refresh();
    }

    internal static void Close()
    {
        if (root == null || !root.activeSelf) return;
        root.SetActive(false);
        if (!fromMenu) { Cursor.lockState = lockWas; Cursor.visible = cursorWas; }
    }

    internal static void Tick()
    {
        var kb = Keyboard.current;
        if (kb != null && kb[Plugin.PanelKey.Value].wasPressedThisFrame)
        {
            if (IsOpen) Close(); else Open(PauseButton.MenuOpen);
        }
        if (!IsOpen) return;
        if (fromMenu && !PauseButton.MenuOpen) { Close(); return; } // the pause menu closed (Esc, Resume)
        if (!fromMenu) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } // kit trap 6
        if (Time.unscaledTime >= nextRefresh) Refresh();
    }

    static void Build()
    {
        Ui.Build();
        root = new GameObject("FIHSync Panel");
        Object.DontDestroyOnLoad(root);
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 30025;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        root.AddComponent<GraphicRaycaster>();

        var panel = Ui.Node(root.transform, "Panel");
        Ui.Anchor(panel, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-60f, 0f), new Vector2(W, 720f));
        Ui.Fill(panel, Ui.rounded, Ui.Back, true);
        Ui.Label(panel, "Mod sharing", Ui.semiBold, 28f, Color.white, Pad, 24f, 300f, 40f);
        var close = Ui.At(panel, "Close", W - Pad - 40f, 24f, 40f, 40f);
        var closeBack = Ui.Fill(close, Ui.small, Ui.Glass, true);
        Ui.Label(close, "×", Ui.semiBold, 24f, Color.white, 0f, 0f, 40f, 40f, TextAlignmentOptions.Center);
        Ui.Click(close.gameObject, closeBack, Close);

        string[] who = { "Everyone", "Friends only", "Nobody" };
        Ui.Label(panel, "See other players' stuff from", Ui.medium, 16f, Ui.Dim, Pad, 80f, Inner, 24f);
        showSeg = new Ui.Segmented(panel, Pad, 106f, Inner, 40f, who, i => { Plugin.ShowFrom.Value = (Plugin.Who)i; Refresh(); }, 15f);
        Ui.Label(panel, "Videos from", Ui.medium, 16f, Ui.Dim, Pad, 158f, Inner, 24f);
        videoSeg = new Ui.Segmented(panel, Pad, 184f, Inner, 40f, who, i => { Plugin.VideosFrom.Value = (Plugin.Who)i; Refresh(); }, 15f);

        Ui.Label(panel, "Share my stuff with others", Ui.medium, 17f, Ui.Ink, Pad, 244f, 360f, 30f);
        shareSwitch = new Ui.Switch(panel, W - Pad - 54f, 244f, () => { Plugin.ShareMine.Value = !Plugin.ShareMine.Value; Refresh(); });
        Ui.Label(panel, "Lag shield", Ui.medium, 17f, Ui.Ink, Pad, 286f, 120f, 30f);
        Ui.Label(panel, "hides anything too heavy", Ui.medium, 14f, Ui.Dim, Pad + 98f, 287f, 260f, 30f);
        shieldSwitch = new Ui.Switch(panel, W - Pad - 54f, 286f, () => { Plugin.LagShield.Value = !Plugin.LagShield.Value; Refresh(); });

        Ui.Section(panel, "PLAYERS IN THIS LOBBY WITH MODS", Pad, RowsTop - 30f, Inner);
        empty = Ui.Label(panel, "Nobody else here has FIH Sync yet", Ui.medium, 16f, Ui.Dim, Pad, RowsTop + 8f, Inner, 30f);
        for (int i = 0; i < MaxRows; i++)
        {
            int index = i;
            var row = new Row();
            var rt = Ui.At(panel, "Player", Pad, RowsTop + i * RowH, Inner, RowH - 8f);
            Ui.Fill(rt, Ui.small, Ui.Soft);
            row.Go = rt.gameObject;
            row.Name = Ui.Label(rt, "", Ui.semiBold, 16f, Color.white, 14f, 2f, 230f, 22f);
            row.What = Ui.Label(rt, "", Ui.medium, 13f, Ui.Dim, 14f, 22f, 230f, 18f);
            row.A = RowButton(rt, Inner - 216f, 108f, () => ClickA(index));
            row.B = RowButton(rt, Inner - 100f, 90f, () => ClickB(index));
            rows.Add(row);
        }
        blockedText = Ui.Label(panel, "", Ui.medium, 14f, Ui.Dim, Pad, 676f, Inner - 120f, 24f);
        var unblock = Ui.At(panel, "Unblock all", W - Pad - 110f, 672f, 110f, 32f);
        var unblockBack = Ui.Fill(unblock, Ui.small, Ui.Glass, true);
        Ui.Label(unblock, "Unblock all", Ui.medium, 14f, Ui.Ink, 0f, 0f, 110f, 32f, TextAlignmentOptions.Center);
        Ui.Click(unblock.gameObject, unblockBack, () => { Plugin.Blocked.Value = ""; Refresh(); });
        root.SetActive(false);
    }

    static TextMeshProUGUI RowButton(RectTransform row, float x, float w, System.Action click)
    {
        var b = Ui.At(row, "Button", x, 6f, w, RowH - 20f);
        var back = Ui.Fill(b, Ui.small, Ui.Glass, true);
        var label = Ui.Label(b, "", Ui.medium, 14f, Ui.Ink, 0f, 0f, w, RowH - 20f, TextAlignmentOptions.Center);
        Ui.Click(b.gameObject, back, click);
        return label;
    }

    // A: Hide / Show / Show anyway.  B: Block / Unblock.
    static void ClickA(int i)
    {
        if (i >= shown.Count) return;
        var p = shown[i];
        if (p.TooHeavy) Share.ShowAnyway(p.Id);
        else Share.SetHidden(p.Id, !p.Hidden);
        Refresh();
    }

    static void ClickB(int i)
    {
        if (i >= shown.Count) return;
        Plugin.Block(shown[i].Id, !shown[i].Blocked);
        Refresh();
    }

    static void Refresh()
    {
        if (root == null) return;
        nextRefresh = Time.unscaledTime + 0.5f;
        showSeg.Select((int)Plugin.ShowFrom.Value);
        videoSeg.Select((int)Plugin.VideosFrom.Value);
        shareSwitch.Set(Plugin.ShareMine.Value);
        shieldSwitch.Set(Plugin.LagShield.Value);
        shown = Share.Peers();
        empty.gameObject.SetActive(shown.Count == 0);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            bool on = i < shown.Count;
            if (row.Go.activeSelf != on) row.Go.SetActive(on);
            if (!on) continue;
            var p = shown[i];
            row.Name.text = p.Name;
            row.What.text = p.Blocked ? "blocked" : p.TooHeavy ? "hidden (too heavy)" : p.Hidden ? "hidden" : p.Loading ? "loading…" : p.What;
            row.A.text = p.TooHeavy ? "Show anyway" : p.Hidden ? "Show" : "Hide";
            row.B.text = p.Blocked ? "Unblock" : "Block";
        }
        int blocked = Plugin.BlockedSet().Count;
        blockedText.text = blocked == 0 ? "No blocked players" : $"{blocked} blocked player{(blocked == 1 ? "" : "s")}";
    }
}

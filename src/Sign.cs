using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace FIHSync;

/// The "Loading mod textures… 2 of 5" sign, top right, while other players' looks are still coming in. It never
/// blocks anything. Skip (or the skip key) just hides it: those players show the game's default look until their
/// files land, then their customizations pop in.
internal static class Sign
{
    static GameObject root;
    static TextMeshProUGUI title, detail;
    static RectTransform bar;
    static CanvasGroup group;
    static bool skipped;
    static int total;
    static float doneAt = -1f;

    internal static void Reset()
    {
        skipped = false;
        total = 0;
        doneAt = -1f;
        if (root != null) root.SetActive(false);
    }

    internal static void Tick()
    {
        int pending = Share.Pending;
        if (pending > 0) total = Mathf.Max(total, pending + Share.DownloadedSinceJoin);
        bool want = !skipped && Plugin.ShowSign.Value && (pending > 0 || (doneAt > 0f && Time.unscaledTime - doneAt < 1.5f));
        if (pending == 0 && total > 0 && doneAt < 0f) doneAt = Time.unscaledTime;
        if (pending > 0) doneAt = -1f;
        if (!want)
        {
            if (root != null && root.activeSelf) root.SetActive(false);
            if (pending == 0 && doneAt > 0f && Time.unscaledTime - doneAt >= 1.5f) { total = 0; doneAt = -1f; }
            return;
        }
        if (root == null) Build();
        if (!root.activeSelf) root.SetActive(true);
        int done = Mathf.Clamp(total - pending, 0, total);
        title.text = pending > 0 ? "Loading mod textures…" : "Mod textures loaded";
        detail.text = $"{done} of {total} from other players";
        bar.sizeDelta = new Vector2(300f * (total > 0 ? (float)done / total : 0f), 6f);
        var kb = Keyboard.current;
        if (kb != null && kb[Plugin.SkipKey.Value].wasPressedThisFrame) Skip();
    }

    static void Skip()
    {
        skipped = true;
        if (root != null) root.SetActive(false);
        Plugin.Logger.LogInfo("loading sign skipped: default looks until the files arrive");
    }

    static void Build()
    {
        Ui.Build();
        Ui.EnsureEventSystem();
        root = new GameObject("FIHSync Sign");
        Object.DontDestroyOnLoad(root);
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 30020;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        root.AddComponent<GraphicRaycaster>();
        group = root.AddComponent<CanvasGroup>();

        var card = Ui.Node(root.transform, "Card");
        Ui.Anchor(card, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-28f, -28f), new Vector2(348f, 118f));
        Ui.Fill(card, Ui.rounded, Ui.Back, true);
        title = Ui.Label(card, "Loading mod textures…", Ui.semiBold, 21f, Ui.Ink, 24f, 18f, 300f, 28f);
        detail = Ui.Label(card, "", Ui.medium, 15f, Ui.Dim, 24f, 46f, 200f, 22f);

        var track = Ui.At(card, "Track", 24f, 80f, 300f, 6f);
        Ui.Fill(track, Ui.small, Ui.Soft);
        bar = Ui.At(track, "Bar", 0f, 0f, 0f, 6f);
        Ui.Fill(bar, Ui.small, Ui.Accent);

        var skip = Ui.At(card, "Skip", 236f, 40f, 88f, 30f);
        var skipImage = Ui.Fill(skip, Ui.small, Ui.Glass, true);
        Ui.Label(skip, $"Skip ({Ui.KeyName(Plugin.SkipKey.Value)})", Ui.medium, 14f, Ui.Ink, 0f, 0f, 88f, 30f, TextAlignmentOptions.Center);
        Ui.Click(skip.gameObject, skipImage, Skip);
    }
}

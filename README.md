# FIH Sync (0.1.0 beta)

Modded players see each other's customizations in Flipping is Hard, automatically. A Flipping is Hard mod (BepInEx 6
IL2CPP) by Fabuleo8796.

Works with:

| Mod | What other players see |
|---|---|
| Flippy Auras 0.2.0+ | your auras around your Flippy |
| Body Accessories 0.2.1+ | everything you wear, animations and videos too |

Unmodded players don't notice anything, and you see them as normal.

## How it works

1. In a lobby, FIH Sync says hello to the other members over **Steam peer-to-peer** (relayed by Steam, no IPs, no
   server). The ones that also run it answer.
2. Each of you sends what you wear. Each look is downloaded **once** and cached, so meeting again is instant.
3. Change something mid-game (a new aura, a new export from Blender) and it re-sends by itself.
4. While other players' files are coming in, a small **"Loading mod textures… 2 of 5"** sign shows top right.
   **Skip** (or F7) hides it: those players look default until their files land.
5. Files from other players **delete themselves after 3 days** without being used.

It never touches the game's own netcode (FishNet): it's a separate Steam channel next to it.

## Mod sharing (main menu and pause menu)

There's a new **Mod sharing** button in the **main menu** and in the pause menu (or press **F8**).

- **See other players' stuff from:** Everyone, Friends only or Nobody.
- **Videos from:** Everyone, Friends only (the default) or Nobody.
- **Share my stuff with others:** on or off.
- **Lag shield:** hides a player's stuff if it keeps costing too much frame time on your PC ("hidden (too heavy)",
  with **Show anyway**).
- **Players in this lobby with mods:** **Hide** (this session) or **Block** (for good) for each one.

## Safety

Other players' files are treated as untrusted:

- Nothing received is ever run. It's data: aura settings, 3D models, raw pixels, and videos only from the players
  you allow.
- Only members of your current Steam lobby can send you anything.
- Every download is checked against its SHA-256 hash and its announced size, piece by piece, up to `MaxMB`.
- Pictures are never decoded on your PC (Body Accessories sends raw pixels). Videos are the one thing Windows
  still decodes, which is why they're friends only by default.
- Each mod caps what it builds (triangles, textures, animation length, aura sliders), and the lag shield catches
  the rest.

## Settings

`BepInEx\config\com.fabuleo8796.fihsync.cfg`:

| Setting | Default | |
|---|---|---|
| `ShowFrom` | Everyone | `Everyone`, `FriendsOnly` (your Steam friends) or `Nobody` |
| `ShareMine` | true | let others see yours |
| `VideosFrom` | FriendsOnly | whose videos play |
| `LagShield` / `LagShieldMs` | true / 1.5 | hide a player whose stuff costs more than this per frame |
| `MaxMB` | 30 | biggest look you download from someone (videos can be big) |
| `Blocked` | | Steam IDs you never see (they're in `LogOutput.log`) |
| `Days` | 3 | how long other players' files stay |
| `Show` / `SkipKey` | true / F7 | the loading sign |
| `Panel` | F8 | opens Mod sharing |

The cache is `BepInEx\cache\FIHSync\`.

## Install

Extract the zip into the game folder: `BepInEx\plugins\FIHSync\FIHSync.dll`. Both players need it, plus the mods
whose looks they want to share.

## For modders

Register a key with five callbacks (all on the main thread):

```csharp
FIHSync.Share.Register("mymod",
    version: () => "changes when my look changes",   // asked once a second; null = nothing to share
    data: () => bytes,                               // my look
    received: (ownerId, bytes, folder) => { ... },   // another player's look; folder = scratch space for files
    gone: ownerId => { ... });                       // they left, switched it off, or got blocked
var player = FIHSync.Share.FindPlayer(ownerId);      // their EHS.PlayerRef (null while not spawned)
bool videos = FIHSync.Share.VideosAllowed(ownerId); // the "Videos from" setting for that player
FIHSync.Share.ReportCost(ownerId, ms);              // each frame: what their stuff cost (for the lag shield)
```

Treat what you receive as untrusted (cap sizes, never run or decode anything risky). Use a soft
`BepInDependency("com.fabuleo8796.fihsync")` and keep the calls in a `NoInlining` method inside a
try/catch, so your mod still runs without FIH Sync.

## Layout

| Path | What |
|---|---|
| `src/Steam.cs` | Steam P2P through the game's `steam_api64.dll` (flat API: ISteamNetworkingMessages, lobby members, friends) |
| `src/Share.cs` | The API, hello / want / chunk messages, the cache and expiry |
| `src/Sign.cs` | The loading sign |
| `src/SharePanel.cs` | The Mod sharing panel |
| `src/PauseButton.cs` | Its buttons in the main menu and the pause menu |
| `src/Plugin.cs` | Load, config |
| `src/Ui.cs` | UI pieces (Geist font) |

Build like Body Accessories: `python tools\make_rsp.py fihsync bin\FIHSync.dll src`, then Unity's bundled `csc`.

MIT license. Geist font © Vercel, SIL Open Font License (`src/Resources/OFL-Geist.txt`).

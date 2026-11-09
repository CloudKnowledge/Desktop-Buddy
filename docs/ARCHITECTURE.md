# Project Mochi – Research & Architecture

## 1. Research summary

| Project | What it does well | Pattern adopted by Mochi |
|---|---|---|
| **Shimeji-ee** (Java) | Each mascot is its own small transparent window; XML behaviours with weighted frequencies and conditions; gravity, falling, edge/window climbing | One small per‑pixel‑alpha window per actor; weighted behaviour selection with conditions; gravity onto the "floor" (top of the taskbar) |
| **Desktop Goose** (C#) | Character drawn procedurally from simple shapes with procedural foot placement – tiny assets, fluid motion | Procedural skeletal rig instead of sprite sheets |
| **VPet‑Simulator** (C#/WPF) | Rich state set; each animation split into *start → loop → end* | Activities have enter / loop / exit phases so transitions never pop |
| **Screen Pet Engine** | One small transparent window per pet, walks along the bottom of the display, drag + fall always available, weighted‑random behaviour graph | Drag & fall are global capabilities available in every state |
| **YijiaDuan/desktop-pet** (Tauri + SVG) | idle → curious → drowsy → sleeping thresholds; tray; autostart; but click‑through needs **cursor polling** because a webview has no per‑pixel hit testing | Avoid webviews: a Win32 layered window gives per‑pixel hit testing for free |
| **OpenPets** | Local‑first, privacy‑friendly, gentle reminders, outfits on a schedule | Offline‑only, no telemetry; outfits/accessories via a config system |

Key lessons: (1) small per‑actor windows are far cheaper than a full‑screen overlay; (2) behaviour must be weighted‑random with cool‑downs or the pet feels robotic; (3) drag/fall/sleep must work in every state; (4) the pet must never steal focus or clicks from real work.

## 2. Technology evaluation (environment: Windows 11, .NET 10 SDK, Python 3.14, Node 26, no Rust)

| Option | Transparency / hit‑testing | Footprint | Verdict |
|---|---|---|---|
| Tauri 2 + Rust | Webview: transparent, but click‑through needs polling | Small, but Rust + MSVC toolchain not installed | Rejected (toolchain, webview hit‑testing) |
| Electron + TS | Same webview limits | 100 MB+ RAM | Rejected (heavy) |
| Python + PySide6 | Good (Qt layered windows) | ~100 MB RAM, packaging friction | Viable runner‑up |
| **.NET 10 + SkiaSharp + Win32 layered windows** | **Per‑pixel alpha + hit testing via `UpdateLayeredWindow`; atomic move+paint** | Small, single‑file exe, SDK already installed | **Chosen** |

Rendering uses **SkiaSharp** (Skia CPU raster: anti‑aliased Béziers, gradients, blur) drawing straight into a DIB section that is pushed to a layered window. The UI shell uses WinForms (tray, menus) and WPF with the Windows 11 Fluent theme (settings).

## 3. Character approach – procedural 2D skeletal rig

Instead of a static PNG or sprite sheets, Mochi is a **parametric rig** (`PetPose`) with ~60 continuous parameters (posture blends *sit / front-facing sit / paws up / loaf / curl / stretch / crouch / arch / leap / held*, purr, puff, head turn & tilt, eye openness, happy‑eyes, dilating cat pupils, brows, mouth, tongue, whiskers, ears, tail sway / curl / wrap, kneading, gait phase, fairy-light twinkle…). A renderer turns a pose into layered vector parts with gradients and fur tufts (head and chest fluff, body, haunches, ears and tabby markings are baked into supersampled sprites; the face, eyes, legs, tail and lights are drawn live). The side-on and square-on bodies cross-fade when Mochi turns to face you; the head is shared.

* Consistent proportions across **every** animation (one model, not hundreds of frames).
* Resolution independent → crisp ("HD") at any size and DPI.
* Transitions are continuous blends, never frame pops.
* Colours/accessories are data (`PetLook`), so tabby coats (golden, cream, ginger, silver), stripe strength, fairy lights, bell collar and hair bow are configurable.

## 4. Module map

```
src/Mochi.Core        (platform‑neutral: net10.0 + SkiaSharp)
  Rig/        PetPose, PetLook, Skeleton, Shapes, PetRenderer, IconArt -> PetRenderer
  Animation/  PoseAnimator (smoothing, blink, breath, purr vibration, tail sway wave, ear springs) -> AnimationController
  Behaviour/  PetBrain (state machine + body physics: gravity, bounce, walls, pendulum when held),
              Activities, BallPlayActivity, LifeActivities, Personality -> BehaviourStateMachine, PhysicsEngine, PersonalityEngine, MovementController
  Play/       YarnBall (ball physics, landing prediction), YarnBallRenderer, Prop + PropRenderer (milk, treats, TV, laptop)
  Interaction/ PointerTracker (click / multi‑click / long‑press / drag / petting strokes) -> InteractionManager
  Effects/    Particles (hearts, Zzz, sparkles, marks, dream bubble, speech bubbles)
  Stage/      PetStage: Mochi on one touch screen – the room, touches mapped to the desktop gestures, view layout, drawing
  Persistence/ Settings + pet memory (JSON, atomic writes)           -> SettingsManager, PersistenceManager
src/Mochi.App         (Windows host: net10.0-windows)
  Platform/   LayeredWindow, Native (Win32 calls), Displays (monitors, idle time, full-screen check, startup registration), SelfInstaller -> DesktopWindowManager, IdleDetector
  PetController (frame loop glue), MochiAppContext (tray icon, menus), Ui/ (WPF settings and welcome) -> SystemTrayManager
tools/Mochi.Preview   renders pose sheets, ball / daily-life films and iPhone screens for visual QA, and builds the app icons
tests/Mochi.Core.Tests unit tests for behaviour, idle policy, physics, ball play, input detection, touch, persistence, rendering
src/Mochi.iOS         (iPhone/iPad host: net10.0-ios, ~280 lines)
  PetViewController (room, kitten and ball as Skia views, toolbar, touches, display-link loop), SceneDelegate, AppDelegate
src/Mochi.Android     (Android host: net10.0-android, API 30+)
  MainActivity (room, kitten and ball as SKCanvasViews, action bar with popup menus, touches, Choreographer loop, window insets)
src/Mochi.Mac         (Mac desktop host: net10.0-macos, 13+)
  Platform/   PetWindow (borderless non-activating NSPanel, Skia pixels shared with a CGBitmapContext, per-pixel click-through),
              Screens (NSScreen floors above the Dock, top-left coordinates, idle time, SMAppService login item)
  PetController (frame loop glue), AppDelegate (menu bar icon and menu, welcome / rename prompt)
```

Only `Mochi.App` touches Win32 and only `Mochi.Mac` touches AppKit. The Mac host works like the Windows one. Each actor
is a small transparent panel above other apps. Because AppKit sends mouse moves only to the active app, the host polls
the pointer each frame. It turns a panel's mouse handling on only while the pointer is over a visible pixel, so hovering
is petting and clicks elsewhere fall through.

The iPhone/iPad and Android apps work this way. Phones don't allow windows over other apps, so Mochi lives in its own room. `PetStage` (in `Mochi.Core`, unit-tested on Windows) does everything that isn't UIKit or Android views:
* It turns touches into the desktop's gestures (tap = click, press and hold = pick up, finger strokes = petting; elsewhere the finger is the cursor).
* It hit-tests against a small rendered silhouette.
* It lays out the kitten's and the ball's views on whole device pixels.
* It paints the day/night room.

`Mochi.iOS` is only the UIKit glue: three `SKCanvasView`s, a `UIToolbar`, a `CADisplayLink` loop and scene lifecycle events (coming back after two minutes or more gets a "Hi!"). `Mochi.Android` is the same glue for Android views: three `SKCanvasView`s moved by translation, a bar of buttons with `PopupMenu`s, a `Choreographer` loop and `OnResume`/`OnPause`.

## 5. Frame loop

`Timer (~60 Hz tick) → read cursor (GetCursorPos) + idle time (GetLastInputInfo) → PetBrain.Update → PoseAnimator.Update → render only if the adaptive frame budget allows → UpdateLayeredWindow(position + pixels atomically)`.

Adaptive frame rate: ~60 fps while moving, ~30 fps idle, ~12 fps asleep, 0 fps when hidden or the session is locked.

## 6. Privacy model

* Presence detection uses only `GetLastInputInfo` (a timestamp – no keys, no content).
* Cursor position is read with `GetCursorPos` each frame and never stored.
* No hooks, no camera, no microphone, no screenshots, no network calls, no telemetry.
* Optional `WDA_EXCLUDEFROMCAPTURE` hides Mochi from screen sharing/recording while it stays visible to you.
* Startup registration is an explicit opt‑in (`HKCU\...\Run`), no admin rights.

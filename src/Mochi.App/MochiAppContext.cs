using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Mochi.App.Platform;
using Mochi.App.Ui;
using Mochi.Core.Behaviour;
using Mochi.Core.Persistence;
using Mochi.Core.Play;
using Mochi.Core.Rig;
using SkiaSharp;

namespace Mochi.App;

/// <summary>Composition root: settings, the pet, the tray icon and menus, and the settings windows.</summary>
internal sealed class MochiAppContext : ApplicationContext
{
    readonly string _dataDir, _settingsPath;
    readonly MochiSettings _settings;
    readonly PetMemory _memory;
    readonly PetController _pet;
    readonly NotifyIcon _tray;
    readonly ContextMenuStrip _menu = new();
    readonly Form _owner;
    readonly RegisteredWaitHandle _showWait, _exitWait, _ballWait;
    Icon? _icon;
    Bitmap? _face;
    SettingsWindow? _settingsWindow;
    bool _exiting;

    public MochiAppContext(EventWaitHandle showSignal, EventWaitHandle exitSignal, EventWaitHandle ballSignal, bool atSignIn)
    {
        _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mochi");
        _settingsPath = Path.Combine(_dataDir, "settings.json");
        var memoryPath = Path.Combine(_dataDir, "memory.json");
        _settings = JsonStore.Load<MochiSettings>(_settingsPath, Log.Info);
        _settings.Normalize();
        _memory = JsonStore.Load<PetMemory>(memoryPath, Log.Info);
        _memory.Normalize();

        // Invisible helper used to marshal to the UI thread and to own popup menus.
        _owner = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(1, 1),
        };
        _ = _owner.Handle;

        _pet = new PetController(_settings, _memory, memoryPath, new Displays(), atSignIn);
        _pet.ContextMenuRequested += ShowPetMenu;

        ModernMenu.Style(_menu);
        _menu.Opening += (_, e) =>
        {
            BuildMenu();
            e.Cancel = false;
        };

        RefreshArtwork();
        _tray = new NotifyIcon { Icon = _icon, Text = _settings.PetName, Visible = true, ContextMenuStrip = _menu };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (_pet.IsHidden) SetHidden(false);
            else _pet.Brain.CommandComeHere();
        };
        _tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };

        // A second launch of Mochi.exe just asks this instance to show the kitten.
        _showWait = ThreadPool.RegisterWaitForSingleObject(showSignal,
            (_, _) => _owner.BeginInvoke(() => { SetHidden(false); _pet.Brain.CommandComeHere(); }), null, -1, false);
        // Setup/uninstall ask a running Mochi to save and exit.
        _exitWait = ThreadPool.RegisterWaitForSingleObject(exitSignal, (_, _) => _owner.BeginInvoke(Exit), null, -1, true);
        // `Mochi.exe --ball` (e.g. from a desktop shortcut) brings out the yarn ball.
        _ballWait = ThreadPool.RegisterWaitForSingleObject(ballSignal,
            (_, _) => _owner.BeginInvoke(() => { SetHidden(false); _pet.Brain.ThrowBall(); }), null, -1, false);

        _pet.Start();
        if (!_settings.FirstRunDone) ShowWelcome();

        // Once start-up work is done, hand unused start-up memory back to Windows.
        var settle = new System.Windows.Forms.Timer { Interval = 20_000 };
        settle.Tick += (_, _) =>
        {
            settle.Dispose();
            if (_settingsWindow == null && _settings.FirstRunDone) WpfHost.Trim();
        };
        settle.Start();
    }

    // ---- menus ----------------------------------------------------------------------------------

    void ShowPetMenu(SKPoint at)
    {
        Native.SetForegroundWindow(_owner.Handle);
        _menu.Show(new Point((int)at.X, (int)at.Y));
    }

    void BuildMenu()
    {
        var brain = _pet.Brain;
        string name = _settings.PetName;
        _menu.Items.Clear();

        // A label (not a disabled item) so the little face keeps its colours.
        var header = new ToolStripLabel($"{name} {brain.MoodText}")
        {
            Image = _face,
            ImageAlign = ContentAlignment.MiddleLeft,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            Padding = new Padding(6, 6, 6, 4),
            Font = new Font(_menu.Font, FontStyle.Bold),
        };
        _menu.Items.Add(header);
        _menu.Items.Add(new ToolStripSeparator());

        if (_pet.IsHidden) _menu.Items.Add(Item($"Show {name}", () => SetHidden(false)));
        _menu.Items.Add(Item($"Pet {name}", brain.CommandPet));
        _menu.Items.Add(brain.IsAsleep ? Item("Wake up", brain.CommandWake) : Item($"Make {name} sleep", brain.CommandSleep));
        _menu.Items.Add(Item("Follow my cursor", () => brain.SetFollowCursor(!brain.FollowCursor), brain.FollowCursor));
        _menu.Items.Add(Item("Come here", brain.CommandComeHere));
        if (brain.Ball == null) _menu.Items.Add(Item("Play ball", brain.ThrowBall));
        else
        {
            _menu.Items.Add(Item("Throw the ball", brain.ThrowBall));
            _menu.Items.Add(Item("Put the ball away", brain.RemoveBall));
        }
        _menu.Items.Add(Sub($"Feed {name}", new ToolStripItem[]
        {
            Item("A saucer of milk", () => brain.CommandFeed(PropKind.Milk)),
            Item("Some treats", () => brain.CommandFeed(PropKind.Treats)),
        }));
        _menu.Items.Add(Item("Watch cat TV", brain.CommandWatchTv));
        _menu.Items.Add(Item("Work with me", brain.CommandWork));
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(Sub("Personality", Enum.GetValues<PersonalityProfile>().Select(p =>
            Item(p.ToString(), () => { _settings.Personality = p; Apply(); }, _settings.Personality == p))));

        var look = new ToolStripMenuItem("Look");
        look.DropDownItems.Add(Sub("Coat", new[] { ("Golden", CoatPreset.Golden), ("Cream", CoatPreset.Cream), ("Ginger", CoatPreset.Ginger), ("Silver", CoatPreset.Silver) }
            .Select(c => Item(c.Item1, () => { _settings.Coat = c.Item2; Apply(); }, _settings.Coat == c.Item2))));
        look.DropDownItems.Add(Sub("Accessory", new[] { ("Fairy lights", AccessoryKind.FairyLights), ("Bell collar", AccessoryKind.Bell), ("Hair bow", AccessoryKind.Bow), ("None", AccessoryKind.None) }
            .Select(a => Item(a.Item1, () => { _settings.Accessory = a.Item2; Apply(); }, _settings.Accessory == a.Item2))));
        look.DropDownItems.Add(Sub("Colour", PetLook.AccessoryColors.Select((c, i) =>
            Item(c.Name, () => { _settings.AccessoryColorIndex = i; Apply(); }, _settings.AccessoryColorIndex == i))));
        _menu.Items.Add(look);

        _menu.Items.Add(Sub("Size", new[] { ("Small", 0.75f), ("Medium", 1f), ("Large", 1.35f), ("Huge", 1.75f) }
            .Select(s => Item(s.Item1, () => { _settings.Size = s.Item2; Apply(); }, MathF.Abs(_settings.Size - s.Item2) < 0.01f))));
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(Item("Ghost mode (click-through)", () => { _settings.ClickThrough = !_settings.ClickThrough; Apply(); }, _settings.ClickThrough));
        _menu.Items.Add(Item("Hide from screen sharing", () => { _settings.HideFromScreenCapture = !_settings.HideFromScreenCapture; Apply(); }, _settings.HideFromScreenCapture));
        _menu.Items.Add(Item("Start with Windows", () => { _settings.StartWithWindows = !_settings.StartWithWindows; Apply(); }, _settings.StartWithWindows));
        _menu.Items.Add(Item("Settings…", ShowSettings));
        if (!_pet.IsHidden) _menu.Items.Add(Item($"Hide {name}", () => SetHidden(true)));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("Exit", Exit));
        ModernMenu.StyleItems(_menu.Items, _menu);
    }

    static ToolStripMenuItem Item(string text, Action onClick, bool isChecked = false)
    {
        var item = new ToolStripMenuItem(text) { Checked = isChecked };
        item.Click += (_, _) =>
        {
            try { onClick(); }
            catch (Exception ex) { Log.Error($"Menu action '{text}' failed", ex); }
        };
        return item;
    }

    static ToolStripMenuItem Sub(string text, System.Collections.Generic.IEnumerable<ToolStripItem> children)
    {
        var item = new ToolStripMenuItem(text);
        item.DropDownItems.AddRange(children.ToArray());
        return item;
    }

    // ---- actions --------------------------------------------------------------------------------

    void Apply()
    {
        _pet.ApplySettings();
        SaveSettings();
        RefreshArtwork();
        _tray.Icon = _icon;
        _tray.Text = _settings.PetName;
    }

    void SaveSettings()
    {
        try { JsonStore.Save(_settingsPath, _settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Could not save settings", ex); }
    }

    void RefreshArtwork()
    {
        var look = _settings.BuildLook();
        var oldIcon = _icon;
        var oldFace = _face;
        using (var ms = new MemoryStream(IconArt.BuildIco(look, 16, 20, 24, 32, 40, 48, 64)))
            _icon = new Icon(ms, SystemInformation.SmallIconSize);
        using (var bmp = IconArt.RenderFace(look, 40, happy: true))
        using (var data = bmp.Encode(SKEncodedImageFormat.Png, 100))
        using (var ms = new MemoryStream(data.ToArray()))
            _face = new Bitmap(Image.FromStream(ms), new Size(20, 20));
        oldIcon?.Dispose();
        oldFace?.Dispose();
    }

    void SetHidden(bool hidden) => _pet.SetHidden(hidden);

    void ShowSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        WpfHost.Ensure();
        _settingsWindow = new SettingsWindow(_settings, _pet.Brain, _dataDir, Apply, ForgetMemory, () => (_pet.LastFrameMs, _pet.CurrentFps));
        ElementHost.EnableModelessKeyboardInterop(_settingsWindow);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            _owner.BeginInvoke(WpfHost.Trim);
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    void ShowWelcome()
    {
        WpfHost.Ensure();
        var welcome = new WelcomeWindow(_settings);
        ElementHost.EnableModelessKeyboardInterop(welcome);
        welcome.Closed += (_, _) =>
        {
            _settings.FirstRunDone = true;
            Apply();
            _tray.ShowBalloonTip(6000, $"{_settings.PetName} lives here now",
                "Click to boop, stroke its head gently to hear it purr, drag to carry. Right-click for more.", ToolTipIcon.None);
            _owner.BeginInvoke(WpfHost.Trim);
        };
        welcome.Show();
        welcome.Activate();
    }

    void ForgetMemory()
    {
        _memory.TimesPetted = _memory.TimesClicked = _memory.TimesCarried = _memory.TimesGreeted = 0;
        _memory.FirstMetUtc = DateTime.UtcNow;
        _memory.LastMorningGreeting = null;
        Array.Clear(_pet.Brain.Personality.Drift);
        _pet.SaveMemory();
    }

    void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        _settingsWindow?.Close();
        _showWait.Unregister(null);
        _exitWait.Unregister(null);
        _ballWait.Unregister(null);
        _pet.Dispose();
        SaveSettings();
        _tray.Visible = false;
        _tray.Dispose();
        Log.Info("Mochi exited.");
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (!_exiting) Exit();
            _menu.Dispose();
            _owner.Dispose();
            _icon?.Dispose();
            _face?.Dispose();
        }
        base.Dispose(disposing);
    }
}

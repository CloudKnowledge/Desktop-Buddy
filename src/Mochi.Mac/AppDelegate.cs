using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AppKit;
using CoreGraphics;
using Foundation;
using Mochi.Core.Behaviour;
using Mochi.Core.Persistence;
using Mochi.Core.Play;
using Mochi.Core.Rig;
using Mochi.Mac.Platform;

namespace Mochi.Mac;

/// <summary>Composition root: settings, the pet, the menu bar icon and its menu.</summary>
[Register("AppDelegate")]
internal sealed class AppDelegate : NSApplicationDelegate
{
    string _dataDir = "", _settingsPath = "";
    MochiSettings _settings = new();
    PetMemory _memory = new();
    PetController? _pet;
    NSStatusItem? _status;
    readonly NSMenu _menu = new() { AutoEnablesItems = false };
    NSImage? _face, _bigFace;
    bool _exiting;

    PetController Pet => _pet!;

    public override void DidFinishLaunching(NSNotification notification)
    {
        // A menu bar app: no Dock icon, never takes focus from your work.
        NSApplication.SharedApplication.ActivationPolicy = NSApplicationActivationPolicy.Accessory;
        _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mochi");
        _settingsPath = Path.Combine(_dataDir, "settings.json");
        var memoryPath = Path.Combine(_dataDir, "memory.json");
        _settings = JsonStore.Load<MochiSettings>(_settingsPath, Log.Info);
        _settings.Normalize();
        _memory = JsonStore.Load<PetMemory>(memoryPath, Log.Info);
        _memory.Normalize();

        _pet = new PetController(_settings, _memory, memoryPath, new Screens());
        _pet.ContextMenuRequested += _ => _menu.PopUpMenu(null, NSEvent.CurrentMouseLocation, null);
        _menu.Delegate = new MenuOpening(BuildMenu);

        RefreshArtwork();
        _status = NSStatusBar.SystemStatusBar.CreateStatusItem(NSStatusItemLength.Square);
        _status.Menu = _menu;
        UpdateStatusItem();

        _pet.Start();
        if (!_settings.FirstRunDone) BeginInvokeOnMainThread(Welcome);
    }

    // Opening Mochi again (Finder, Spotlight, Launchpad) calls the kitten over.
    public override bool ApplicationShouldHandleReopen(NSApplication sender, bool hasVisibleWindows)
    {
        Pet.SetHidden(false);
        Pet.Brain.CommandComeHere();
        return false;
    }

    public override void WillTerminate(NSNotification notification) => Exit();

    // ---- menu ----------------------------------------------------------------------------------

    void BuildMenu()
    {
        var brain = Pet.Brain;
        string name = _settings.PetName;
        _menu.RemoveAllItems();

        _menu.AddItem(new NSMenuItem($"{name} {brain.MoodText}") { Image = _face, Enabled = false });
        _menu.AddItem(NSMenuItem.SeparatorItem);

        if (Pet.IsHidden) _menu.AddItem(Item($"Show {name}", () => Pet.SetHidden(false)));
        _menu.AddItem(Item($"Pet {name}", brain.CommandPet));
        _menu.AddItem(brain.IsAsleep ? Item("Wake up", brain.CommandWake) : Item($"Make {name} sleep", brain.CommandSleep));
        _menu.AddItem(Item("Follow my cursor", () => brain.SetFollowCursor(!brain.FollowCursor), brain.FollowCursor));
        _menu.AddItem(Item("Come here", brain.CommandComeHere));
        if (brain.Ball == null) _menu.AddItem(Item("Play ball", brain.ThrowBall));
        else
        {
            _menu.AddItem(Item("Throw the ball", brain.ThrowBall));
            _menu.AddItem(Item("Put the ball away", brain.RemoveBall));
        }
        _menu.AddItem(Sub($"Feed {name}", [
            Item("A saucer of milk", () => brain.CommandFeed(PropKind.Milk)),
            Item("Some treats", () => brain.CommandFeed(PropKind.Treats)),
        ]));
        _menu.AddItem(Item("Watch cat TV", brain.CommandWatchTv));
        _menu.AddItem(Item("Work with me", brain.CommandWork));
        _menu.AddItem(NSMenuItem.SeparatorItem);

        _menu.AddItem(Sub("Personality", Enum.GetValues<PersonalityProfile>().Select(p =>
            Item(p.ToString(), () => { _settings.Personality = p; Apply(); }, _settings.Personality == p))));
        _menu.AddItem(Sub("Look", [
            Sub("Coat", new[] { ("Golden", CoatPreset.Golden), ("Cream", CoatPreset.Cream), ("Ginger", CoatPreset.Ginger), ("Silver", CoatPreset.Silver) }
                .Select(c => Item(c.Item1, () => { _settings.Coat = c.Item2; Apply(); }, _settings.Coat == c.Item2))),
            Sub("Accessory", new[] { ("Fairy lights", AccessoryKind.FairyLights), ("Bell collar", AccessoryKind.Bell), ("Hair bow", AccessoryKind.Bow), ("None", AccessoryKind.None) }
                .Select(a => Item(a.Item1, () => { _settings.Accessory = a.Item2; Apply(); }, _settings.Accessory == a.Item2))),
            Sub("Colour", PetLook.AccessoryColors.Select((c, i) =>
                Item(c.Name, () => { _settings.AccessoryColorIndex = i; Apply(); }, _settings.AccessoryColorIndex == i))),
        ]));
        _menu.AddItem(Sub("Size", new[] { ("Small", 0.75f), ("Medium", 1f), ("Large", 1.35f), ("Huge", 1.75f) }
            .Select(s => Item(s.Item1, () => { _settings.Size = s.Item2; Apply(); }, MathF.Abs(_settings.Size - s.Item2) < 0.01f))));
        _menu.AddItem(Item("Rename…", () => AskName($"Rename {name}", "What should your kitten be called?")));
        _menu.AddItem(NSMenuItem.SeparatorItem);

        _menu.AddItem(Item("Ghost mode (click-through)", () => { _settings.ClickThrough = !_settings.ClickThrough; Apply(); }, _settings.ClickThrough));
        _menu.AddItem(Item("Hide from screen sharing", () => { _settings.HideFromScreenCapture = !_settings.HideFromScreenCapture; Apply(); }, _settings.HideFromScreenCapture));
        _menu.AddItem(Item("Hide over full-screen apps", () => { _settings.HideInFullscreen = !_settings.HideInFullscreen; Apply(); }, _settings.HideInFullscreen));
        _menu.AddItem(Item("Nap when I'm away", () => { _settings.AutoSleep = !_settings.AutoSleep; Apply(); }, _settings.AutoSleep));
        _menu.AddItem(Item("Open at login", () => { _settings.StartWithWindows = !_settings.StartWithWindows; Apply(); }, _settings.StartWithWindows));
        if (!Pet.IsHidden) _menu.AddItem(Item($"Hide {name}", () => Pet.SetHidden(true)));
        _menu.AddItem(NSMenuItem.SeparatorItem);
        _menu.AddItem(Item($"Quit {name}", () => NSApplication.SharedApplication.Terminate(null)));
    }

    static NSMenuItem Item(string title, Action onClick, bool isChecked = false)
    {
        var item = new NSMenuItem(title, (_, _) =>
        {
            try { onClick(); }
            catch (Exception ex) { Log.Error($"Menu action '{title}' failed", ex); }
        });
        if (isChecked) item.State = NSCellStateValue.On;
        return item;
    }

    static NSMenuItem Sub(string title, IEnumerable<NSMenuItem> children)
    {
        var menu = new NSMenu(title) { AutoEnablesItems = false };
        foreach (var child in children) menu.AddItem(child);
        return new NSMenuItem(title) { Submenu = menu };
    }

    // ---- actions --------------------------------------------------------------------------------

    void Apply()
    {
        Pet.ApplySettings();
        SaveSettings();
        RefreshArtwork();
        UpdateStatusItem();
    }

    void SaveSettings()
    {
        try { JsonStore.Save(_settingsPath, _settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Could not save settings", ex); }
    }

    void RefreshArtwork()
    {
        var look = _settings.BuildLook();
        _face?.Dispose();
        _bigFace?.Dispose();
        _face = Image(IconArt.RenderFacePng(look, 36), 18);
        _bigFace = Image(IconArt.RenderFacePng(look, 128), 64);
    }

    static NSImage Image(byte[] png, double points)
    {
        using var data = NSData.FromArray(png);
        return new NSImage(data) { Size = new CGSize(points, points) };
    }

    void UpdateStatusItem()
    {
        if (_status?.Button is not { } button) return;
        button.Image = _face;
        button.ToolTip = _settings.PetName;
    }

    void Welcome()
    {
        AskName("Say hello to your new kitten",
            "What would you like to call it? It lives just above the Dock: click to boop, hover gently over its head to pet it, drag to carry it. Control-click it or use the menu bar icon for more.");
        _settings.FirstRunDone = true;
        Apply();
    }

    void AskName(string message, string info)
    {
        var app = NSApplication.SharedApplication;
        if (OperatingSystem.IsMacOSVersionAtLeast(14)) app.Activate();
        else app.ActivateIgnoringOtherApps(true);
        var field = new NSTextField(new CGRect(0, 0, 240, 24)) { StringValue = _settings.PetName, PlaceholderString = "Mochi" };
        using var alert = new NSAlert { MessageText = message, InformativeText = info, AccessoryView = field, Icon = _bigFace! };
        alert.AddButton("OK");
        alert.AddButton("Cancel");
        alert.Window.InitialFirstResponder = field;
        if (alert.RunModal() != (nint)(long)NSAlertButtonReturn.First) return;
        _settings.PetName = field.StringValue;
        Apply();
    }

    void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        _pet?.Dispose();
        SaveSettings();
        if (_status != null) NSStatusBar.SystemStatusBar.RemoveStatusItem(_status);
        Log.Info("Mochi exited.");
    }

    /// <summary>Rebuilds the menu each time it opens, so it shows the kitten's current mood and choices.</summary>
    sealed class MenuOpening(Action build) : NSMenuDelegate
    {
        public override void MenuWillHighlightItem(NSMenu menu, NSMenuItem? item) { }

        public override void NeedsUpdate(NSMenu menu) => build();
    }
}

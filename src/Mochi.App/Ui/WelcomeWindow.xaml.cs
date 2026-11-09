using System;
using System.Windows;
using System.Windows.Controls;
using Mochi.Core.Behaviour;
using Mochi.Core.Persistence;
using Mochi.Core.Rig;

namespace Mochi.App.Ui;

public partial class WelcomeWindow : Window
{
    readonly MochiSettings _s;
    readonly PetPreview _preview;

    internal WelcomeWindow(MochiSettings settings)
    {
        InitializeComponent();
        _s = settings;
        _preview = new PetPreview(PreviewImage, settings.BuildLook());
        NameBox.Text = settings.PetName;
        foreach (var p in Enum.GetValues<PersonalityProfile>()) PersonalityBox.Items.Add(new ComboBoxItem { Content = p.ToString(), Tag = p });
        PersonalityBox.SelectedIndex = (int)settings.Personality;
        CoatBox.SelectedIndex = (int)settings.Coat;
        StartupBox.IsChecked = settings.StartWithWindows;
        Closed += (_, _) => _preview.Dispose();
        Loaded += (_, _) => NameBox.Focus();
    }

    void OnPersonalityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PersonalityBox.SelectedItem is ComboBoxItem { Tag: PersonalityProfile p })
            PersonalityHint.Text = Personality.Describe(p);
    }

    void OnCoatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_preview == null || CoatBox.SelectedIndex < 0) return;
        _preview.SetLook(PetLook.FromPreset((CoatPreset)CoatBox.SelectedIndex) with
        {
            Accessory = _s.Accessory,
            AccessoryColor = PetLook.AccessoryColors[_s.AccessoryColorIndex].Color,
        });
    }

    /// <summary>True when the user confirmed with "Let's go!" (the window is shown modelessly).</summary>
    public bool Accepted { get; private set; }

    void OnGo(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(NameBox.Text)) _s.PetName = NameBox.Text.Trim();
        if (PersonalityBox.SelectedItem is ComboBoxItem { Tag: PersonalityProfile p }) _s.Personality = p;
        if (CoatBox.SelectedIndex >= 0) _s.Coat = (CoatPreset)CoatBox.SelectedIndex;
        _s.StartWithWindows = StartupBox.IsChecked == true;
        Accepted = true;
        Close();
    }
}

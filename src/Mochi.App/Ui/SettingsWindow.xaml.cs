using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mochi.Core.Behaviour;
using Mochi.Core.Persistence;
using Mochi.Core.Rig;

namespace Mochi.App.Ui;

public partial class SettingsWindow : Window
{
    readonly MochiSettings _s;
    readonly PetBrain _brain;
    readonly string _dataDir;
    readonly Action _changed, _forget;
    readonly Func<(double Ms, double Fps)> _perf;
    readonly PetPreview _preview;
    readonly DispatcherTimer _statusTimer;
    bool _loading = true;

    internal SettingsWindow(MochiSettings settings, PetBrain brain, string dataDir, Action changed, Action forget, Func<(double, double)> perf)
    {
        InitializeComponent();
        _s = settings;
        _brain = brain;
        _dataDir = dataDir;
        _changed = changed;
        _forget = forget;
        _perf = perf;
        _preview = new PetPreview(PreviewImage, settings.BuildLook());

        foreach (var (name, _) in PetLook.AccessoryColors) ColourBox.Items.Add(new ComboBoxItem { Content = name });
        BuildPersonalityList();
        LoadValues();
        Nav.SelectedIndex = 0;
        DataPathText.Text = dataDir;
        VersionText.Text = $"Mochi {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        UpdateStatus();
        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            _preview.Dispose();
        };
        _loading = false;
    }

    void BuildPersonalityList()
    {
        foreach (var profile in Enum.GetValues<PersonalityProfile>())
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = profile.ToString(), FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = Personality.Describe(profile), Opacity = 0.72, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            var radio = new RadioButton { GroupName = "Personality", Tag = profile, Content = text, IsChecked = profile == _s.Personality };
            radio.Checked += OnPersonalityChecked;
            PersonalityList.Children.Add(new Border { Style = (Style)FindResource("Card"), Child = radio });
        }
    }

    void LoadValues()
    {
        NameBox.Text = _s.PetName;
        CoatBox.SelectedIndex = (int)_s.Coat;
        AccessoryBox.SelectedIndex = (int)_s.Accessory;
        ColourBox.SelectedIndex = _s.AccessoryColorIndex;
        SizeSlider.Value = _s.Size;
        LivelySlider.Value = _s.Liveliness;
        EffectsBox.IsChecked = _s.ShowEffects;
        FpsBox.SelectedIndex = _s.MaxFps >= 60 ? 0 : 1;
        AutoSleepBox.IsChecked = _s.AutoSleep;
        DrowsySlider.Value = _s.DrowsyAfterSeconds;
        SleepSlider.Value = _s.SleepAfterSeconds;
        StartupBox.IsChecked = _s.StartWithWindows;
        CaptureBox.IsChecked = _s.HideFromScreenCapture;
        FullscreenBox.IsChecked = _s.HideInFullscreen;
        GhostBox.IsChecked = _s.ClickThrough;
        UpdateLabels();
    }

    void UpdateLabels()
    {
        SizeText.Text = $"{Math.Round(_s.Size * 100)} % of normal size";
        DrowsyText.Text = $"{Duration(_s.DrowsyAfterSeconds)} without keyboard or mouse input";
        SleepText.Text = $"{Duration(_s.SleepAfterSeconds)} without keyboard or mouse input";
        PetTitle.Text = _s.PetName;
    }

    static string Duration(int seconds) => seconds < 60 ? $"{seconds} seconds"
        : seconds % 60 == 0 ? $"{seconds / 60} minute{(seconds == 60 ? "" : "s")}"
        : $"{seconds / 60.0:0.#} minutes";

    void UpdateStatus()
    {
        PetMood.Text = $"{_s.PetName} {_brain.MoodText}";
        var p = _brain.Personality;
        var traits = new (string Name, float Value)[]
        {
            ("Playful", p.Playful), ("Lazy", p.Lazy), ("Curious", p.Curious),
            ("Shy", p.Shy), ("Affectionate", p.Affectionate), ("Mischievous", p.Mischievous),
        };
        var rows = new StackPanel();
        foreach (var (name, value) in traits)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = value, Height = 6, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(bar, 1);
            row.Children.Add(bar);
            rows.Children.Add(row);
        }
        TraitList.ItemsSource = null;
        TraitList.Items.Clear();
        TraitList.Items.Add(rows);

        var (ms, fps) = _perf();
        PerfText.Text = $"Right now: {fps:0} fps, {ms:0.00} ms per frame.";
        var m = _brain.Memory;
        StatsText.Text = $"Together since {m.FirstMetUtc.ToLocalTime():d} · petted {m.TimesPetted} times · {m.TimesClicked} boops · carried {m.TimesCarried} times · {m.TimesGreeted} greetings.";
    }

    void Changed()
    {
        if (_loading) return;
        UpdateLabels();
        _changed();
    }

    void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not ListBoxItem { Tag: string page }) return;
        foreach (var name in new[] { "CharacterPage", "PersonalityPage", "BehaviourPage", "SleepPage", "DesktopPage", "PrivacyPage", "AboutPage" })
            if (FindName(name) is UIElement el) el.Visibility = name == page ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || string.IsNullOrWhiteSpace(NameBox.Text)) return;
        _s.PetName = NameBox.Text.Trim();
        Changed();
    }

    void OnLookChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (CoatBox.SelectedIndex >= 0) _s.Coat = (CoatPreset)CoatBox.SelectedIndex;
        if (AccessoryBox.SelectedIndex >= 0) _s.Accessory = (AccessoryKind)AccessoryBox.SelectedIndex;
        if (ColourBox.SelectedIndex >= 0) _s.AccessoryColorIndex = ColourBox.SelectedIndex;
        _preview.SetLook(_s.BuildLook());
        Changed();
    }

    void OnSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _s.Size = (float)SizeSlider.Value;
        Changed();
    }

    void OnLivelyChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _s.Liveliness = (float)LivelySlider.Value;
        Changed();
    }

    void OnFpsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _s.MaxFps = FpsBox.SelectedIndex == 1 ? 30 : 60;
        Changed();
    }

    void OnSleepTimesChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _s.DrowsyAfterSeconds = (int)DrowsySlider.Value;
        if (SleepSlider.Value < DrowsySlider.Value + 30) SleepSlider.Value = DrowsySlider.Value + 30;
        _s.SleepAfterSeconds = (int)SleepSlider.Value;
        Changed();
    }

    void OnToggle(object sender, RoutedEventArgs e)
    {
        _s.ShowEffects = EffectsBox.IsChecked == true;
        _s.AutoSleep = AutoSleepBox.IsChecked == true;
        _s.StartWithWindows = StartupBox.IsChecked == true;
        _s.HideFromScreenCapture = CaptureBox.IsChecked == true;
        _s.HideInFullscreen = FullscreenBox.IsChecked == true;
        _s.ClickThrough = GhostBox.IsChecked == true;
        Changed();
    }

    void OnPersonalityChecked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: PersonalityProfile profile }) return;
        _s.Personality = profile;
        Changed();
    }

    void OnResetTraits(object sender, RoutedEventArgs e)
    {
        Array.Clear(_brain.Personality.Drift);
        _changed();
        UpdateStatus();
    }

    void OnOpenData(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_dataDir}\"") { UseShellExecute = true });

    void OnForget(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, $"{_s.PetName} will forget its position, learned personality and counters. Settings are kept.",
            "Forget everything?", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        _forget();
        UpdateStatus();
    }
}

using Godot;
using MasterTrack.Networking;

namespace MasterTrack.UI;

/// <summary>
/// The Escape menu: volume sliders, a keybinds page (see <c>PauseMenu.Keybinds.cs</c>),
/// back to the game, or back to the main menu.
///
/// Instanced last in each playable scene so its <c>_UnhandledInput</c> sees Escape before
/// anything else does. Solo it pauses the whole tree; in a session it only overlays — a
/// networked match cannot stop for one player's menu, and the peer has to keep pumping
/// packets from behind it.
///
/// Volumes drive the audio buses directly (Master, SFX, Music — see
/// <c>default_bus_layout.tres</c>) and are kept in <c>user://settings.cfg</c>, re-applied
/// whenever a scene carrying this menu loads.
/// </summary>
public partial class PauseMenu : CanvasLayer
{
    private const string MainMenuScenePath = "res://scenes/Main.tscn";
    private const string SettingsPath = "user://settings.cfg";
    private const string VolumeSection = "audio";

    /// <summary>Below this a slider is treated as "off" — LinearToDb(0) is -infinity.</summary>
    private const float SilenceThreshold = 0.001f;

    private static readonly string[] BusNames = { "Master", "SFX", "Music" };

    private HSlider[] _sliders = null!;

    public override void _Ready()
    {
        _sliders = new[]
        {
            GetNode<HSlider>("%MasterSlider"),
            GetNode<HSlider>("%SfxSlider"),
            GetNode<HSlider>("%MusicSlider"),
        };

        // First run there is no file; each slider then starts from the bus's current volume.
        var config = new ConfigFile();
        config.Load(SettingsPath);

        for (int i = 0; i < BusNames.Length; i++)
        {
            int busIndex = AudioServer.GetBusIndex(BusNames[i]);
            HSlider slider = _sliders[i];

            float saved = (float)config.GetValue(VolumeSection, BusNames[i],
                Mathf.DbToLinear(AudioServer.GetBusVolumeDb(busIndex)));
            slider.SetValueNoSignal(Mathf.Clamp(saved, 0.0f, 1.0f));
            ApplyVolume(busIndex, saved);

            slider.ValueChanged += value =>
            {
                ApplyVolume(busIndex, (float)value);
                SaveVolumes();
            };
        }

        GetNode<Button>("%ResumeButton").Pressed += Close;
        GetNode<Button>("%MenuButton").Pressed += OnMenuPressed;
        GetNode<Button>("%QuitButton").Pressed += OnQuitPressed;

        InitKeybinds(config);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!@event.IsActionPressed("ui_cancel"))
            return;

        GetViewport().SetInputAsHandled();

        if (Visible && _bindsPage.Visible)
            ShowBindsPage(false);
        else if (Visible)
            Close();
        else
            Open();
    }

    private void Open()
    {
        Visible = true;

        // Solo the world can wait; a session cannot, so there the menu only floats over it.
        if (!NetworkManager.Instance.IsNetworked)
            GetTree().Paused = true;

        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    private void Close()
    {
        ShowBindsPage(false);
        GetTree().Paused = false;
        Visible = false;
    }

    /// <summary>
    /// Out to the main menu. In a session that means dropping out of it first — otherwise the
    /// peer stays connected from behind the menu and the host keeps waiting on a ghost.
    /// </summary>
    private void OnMenuPressed()
    {
        if (NetworkManager.Instance.IsNetworked)
            NetworkManager.Instance.Disconnect();

        GetTree().Paused = false;
        SceneFader.Instance.TransitionTo(MainMenuScenePath);
    }

    /// <summary>
    /// Close the application. Same courtesy as the main-menu exit: leave the session first
    /// so the peer sees a disconnect instead of a timeout.
    /// </summary>
    private void OnQuitPressed()
    {
        if (NetworkManager.Instance.IsNetworked)
            NetworkManager.Instance.Disconnect();

        GetTree().Quit();
    }

    private static void ApplyVolume(int busIndex, float linear)
    {
        // Mute rather than chase -inf dB: the slider's bottom really means silent.
        AudioServer.SetBusMute(busIndex, linear < SilenceThreshold);
        AudioServer.SetBusVolumeDb(busIndex, Mathf.LinearToDb(Mathf.Max(linear, SilenceThreshold)));
    }

    private void SaveVolumes()
    {
        // Read-modify-write so any future non-audio section in the file survives.
        var config = new ConfigFile();
        config.Load(SettingsPath);
        for (int i = 0; i < BusNames.Length; i++)
            config.SetValue(VolumeSection, BusNames[i], (float)_sliders[i].Value);
        config.Save(SettingsPath);
    }
}

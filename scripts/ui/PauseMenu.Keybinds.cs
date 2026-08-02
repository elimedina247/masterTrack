using Godot;
using System.Collections.Generic;

namespace MasterTrack.UI;

/// <summary>
/// Keybinds page of the Escape menu. Rows are generated from <see cref="BindGroups"/> so the
/// list can't drift from the input map; clicking a key button arms a capture and the next
/// keypress becomes the binding.
///
/// Only keyboard events are rebindable — gamepad and mouse bindings on the same actions are
/// left alone, so a rebound action keeps working on a controller. Overrides live in
/// <c>user://settings.cfg</c> under <c>[keybinds]</c> as physical keycodes and are re-applied
/// to the runtime <see cref="InputMap"/> whenever a scene carrying this menu loads. Actions
/// without an override keep every default key (e.g. both W and Up accelerate); assigning one
/// collapses that action to the single chosen key.
/// </summary>
public partial class PauseMenu
{
    private const string BindsSection = "keybinds";

    /// <summary>
    /// What the player may rebind, in display order. Driving and Building are separate
    /// contexts, so the same key may appear once in each group (W drives and flies the build
    /// camera by default) — conflicts are only resolved within a group, by swapping.
    /// </summary>
    private static readonly (string Header, (string Action, string Label)[] Actions)[] BindGroups =
    {
        ("Driving", new[]
        {
            ("racer_accelerate", "Accelerate"),
            ("racer_brake", "Brake / Reverse"),
            ("racer_steer_left", "Steer Left"),
            ("racer_steer_right", "Steer Right"),
            ("racer_handbrake", "Handbrake"),
            ("racer_nitro", "Nitro"),
            ("racer_hop", "Hop"),
            ("racer_reset", "Reset Car"),
        }),
        ("Building", new[]
        {
            ("builder_rotate", "Rotate Piece"),
            ("builder_undo", "Undo"),
            ("builder_toggle", "Toggle Build Mode"),
            ("builder_cam_forward", "Camera Forward"),
            ("builder_cam_back", "Camera Back"),
            ("builder_cam_left", "Camera Left"),
            ("builder_cam_right", "Camera Right"),
            ("builder_cam_up", "Camera Up"),
            ("builder_cam_down", "Camera Down"),
        }),
    };

    private readonly Dictionary<string, Button> _bindButtons = new();

    private Control _mainPage = null!;
    private Control _bindsPage = null!;

    /// <summary>Action currently waiting for a keypress, or null when not capturing.</summary>
    private string? _listeningAction;

    private void InitKeybinds(ConfigFile config)
    {
        _mainPage = GetNode<Control>("Center");
        _bindsPage = GetNode<Control>("Binds");

        ApplySavedBinds(config);
        BuildBindRows();

        GetNode<Button>("%KeybindsButton").Pressed += () => ShowBindsPage(true);
        GetNode<Button>("%BindsBackButton").Pressed += () => ShowBindsPage(false);
        GetNode<Button>("%ResetBindsButton").Pressed += ResetBinds;
    }

    /// <summary>
    /// Runs before GUI input, so while capturing this sees the keypress ahead of any focused
    /// button — otherwise binding Space or Enter would just re-click the row's button.
    /// </summary>
    public override void _Input(InputEvent @event)
    {
        if (_listeningAction == null || @event is not InputEventKey key || !key.Pressed || key.Echo)
            return;

        GetViewport().SetInputAsHandled();

        Key physical = key.PhysicalKeycode != Key.None ? key.PhysicalKeycode : key.Keycode;

        // Escape backs out of the capture; it stays reserved for this menu.
        if (physical != Key.Escape && physical != Key.None)
            AssignBind(_listeningAction, physical);

        StopListening();
    }

    private void ShowBindsPage(bool show)
    {
        StopListening();
        _mainPage.Visible = !show;
        _bindsPage.Visible = show;
    }

    private void BuildBindRows()
    {
        var list = GetNode<VBoxContainer>("%BindsList");

        foreach ((string header, var actions) in BindGroups)
        {
            var headerLabel = new Label { Text = header };
            headerLabel.AddThemeFontSizeOverride("font_size", 22);
            list.AddChild(headerLabel);

            var grid = new GridContainer { Columns = 2 };
            grid.AddThemeConstantOverride("h_separation", 24);
            grid.AddThemeConstantOverride("v_separation", 4);
            list.AddChild(grid);

            foreach ((string action, string label) in actions)
            {
                grid.AddChild(new Label
                {
                    Text = label,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                });

                var button = new Button
                {
                    Text = KeyDisplayName(FirstKeyOf(action)),
                    CustomMinimumSize = new Vector2(130, 0),
                };
                button.Pressed += () => StartListening(action);
                grid.AddChild(button);
                _bindButtons[action] = button;
            }
        }
    }

    private void StartListening(string action)
    {
        StopListening();
        _listeningAction = action;
        _bindButtons[action].Text = "press a key…";
    }

    /// <summary>Ends any capture and puts every button back to showing its real binding.</summary>
    private void StopListening()
    {
        if (_listeningAction == null)
            return;

        _bindButtons[_listeningAction].Text = KeyDisplayName(FirstKeyOf(_listeningAction));
        _listeningAction = null;
    }

    private void AssignBind(string action, Key physical)
    {
        // Same-group conflicts swap rather than silently leaving two actions on one key or
        // stranding the other action unbound.
        foreach ((_, var actions) in BindGroups)
        {
            bool groupHasAction = false;
            foreach ((string other, _) in actions)
                groupHasAction |= other == action;
            if (!groupHasAction)
                continue;

            foreach ((string other, _) in actions)
            {
                if (other == action || FirstKeyOf(other) != physical)
                    continue;

                Key oldKey = FirstKeyOf(action);
                if (oldKey != Key.None)
                {
                    ReplaceKeyBinding(other, oldKey);
                    _bindButtons[other].Text = KeyDisplayName(oldKey);
                    SaveBind(other, oldKey);
                }
            }
        }

        ReplaceKeyBinding(action, physical);
        SaveBind(action, physical);
    }

    /// <summary>Overlays saved overrides onto the default input map at scene load.</summary>
    private static void ApplySavedBinds(ConfigFile config)
    {
        foreach ((_, var actions) in BindGroups)
            foreach ((string action, _) in actions)
            {
                long saved = (long)config.GetValue(BindsSection, action, -1L);
                if (saved >= 0)
                    ReplaceKeyBinding(action, (Key)saved);
            }
    }

    /// <summary>
    /// Swaps an action's keyboard events for a single physical key, leaving gamepad and
    /// mouse events on the action untouched.
    /// </summary>
    private static void ReplaceKeyBinding(string action, Key physical)
    {
        var stale = new List<InputEvent>();
        foreach (InputEvent existing in InputMap.ActionGetEvents(action))
            if (existing is InputEventKey)
                stale.Add(existing);
        foreach (InputEvent existing in stale)
            InputMap.ActionEraseEvent(action, existing);

        InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = physical });
    }

    /// <summary>First keyboard key on the action — what the row's button displays.</summary>
    private static Key FirstKeyOf(string action)
    {
        foreach (InputEvent existing in InputMap.ActionGetEvents(action))
            if (existing is InputEventKey key)
                return key.PhysicalKeycode != Key.None ? key.PhysicalKeycode : key.Keycode;
        return Key.None;
    }

    /// <summary>
    /// Physical keycodes name the QWERTY position; translate through the active layout so
    /// the button shows the key actually printed on the player's keyboard.
    /// </summary>
    private static string KeyDisplayName(Key physical)
    {
        if (physical == Key.None)
            return "—";

        Key label = DisplayServer.KeyboardGetKeycodeFromPhysical(physical);
        return OS.GetKeycodeString(label != Key.None ? label : physical);
    }

    private static void SaveBind(string action, Key physical)
    {
        // Read-modify-write so the audio section (and anything future) survives.
        var config = new ConfigFile();
        config.Load(SettingsPath);
        config.SetValue(BindsSection, action, (long)physical);
        config.Save(SettingsPath);
    }

    /// <summary>
    /// Back to the project's defaults: reload the pristine input map, drop the overrides
    /// from disk, and refresh every row.
    /// </summary>
    private void ResetBinds()
    {
        StopListening();
        InputMap.LoadFromProjectSettings();

        var config = new ConfigFile();
        config.Load(SettingsPath);
        if (config.HasSection(BindsSection))
            config.EraseSection(BindsSection);
        config.Save(SettingsPath);

        foreach ((string action, Button button) in _bindButtons)
            button.Text = KeyDisplayName(FirstKeyOf(action));
    }
}

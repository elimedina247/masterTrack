using Godot;
using System.Collections.Generic;
using MasterTrack.Networking;
using MasterTrack.Racer;

namespace MasterTrack.UI;

/// <summary>
/// The garage half of the menu: a spotlight stage cycling through the bodies in
/// <see cref="CarVariants.All"/>, with a paint row and an antenna row under the nameplate.
///
/// The cars are flat silhouettes (<c>assets/ui/garage/</c>), not the FBX models — a hand-drawn
/// side view reads better at menu scale than a lit 3D viewport and costs nothing to load.
/// Browsing here <i>is</i> choosing: the shown body, the picked paint and the antenna spot are
/// saved to <c>user://settings.cfg</c> (the same file the pause menu keeps volumes in) and pushed
/// into <see cref="GameManager.LocalPreference"/>, which solo play reads directly and a session
/// sends to the server on join. Paint is a preference rather than a claim — the server only
/// grants a colour nobody else is wearing, because the colour is how players tell each other
/// apart.
///
/// Every pick lands on the car in front of you: <see cref="GarageCar"/> paints the silhouette in
/// the chosen colour and stands the antenna at the chosen mount, so the stage is a picture of the
/// car you are about to drive rather than stock art with a caption. The pill under the name still
/// spells the pick out in words, for the paint two people would argue over the name of.
/// </summary>
public partial class MainMenu
{
	/// <summary>Index-aligned with <see cref="CarVariants.All"/>. Each of these is the body layer
	/// on its own; the trim that goes over it is the same name with <c>_trim</c>, and
	/// <see cref="GarageCar"/> draws the pair. The colour each body used to be filled with is its
	/// own palette entry — the palette in order, wrapping past G — which is what AUTO paints, so a
	/// garage nobody has touched looks exactly as it always did.</summary>
	private static readonly string[] GarageArt =
	{
		"res://assets/ui/garage/car_a_wedge.svg",
		"res://assets/ui/garage/car_b_bubble.svg",
		"res://assets/ui/garage/car_c_cartoon.svg",
		"res://assets/ui/garage/car_d_cartoon.svg",
		"res://assets/ui/garage/car_e_hachi.svg",
		"res://assets/ui/garage/car_f_hachi.svg",
		"res://assets/ui/garage/car_g_panda.svg",
		"res://assets/ui/garage/car_h_hatch.svg",
	};

	/// <summary>
	/// Where the whip's foot lands on each body, in the art's 200x90 space, in the order of
	/// <see cref="CarVariants.AntennaSpots"/>: back deck, roof centre, front.
	///
	/// Read off the silhouettes by hand rather than projected from the 3D spots — the drawings are
	/// not the models. Every body faces left, so "front" is the low end. A side view cannot show
	/// the front mount's offset across the car either; on the stage it only says nose rather than
	/// nose-right, which is the part a player is choosing between.
	/// </summary>
	private static readonly Vector2[][] GarageAntennaMounts =
	{
		new[] { new Vector2(160, 41), new Vector2(126, 21), new Vector2(40, 52) }, // A_Wedge
		new[] { new Vector2(145, 23), new Vector2(87, 16), new Vector2(35, 30) },  // B_Bubble
		new[] { new Vector2(156, 44), new Vector2(116, 28), new Vector2(50, 51) }, // C_Cartoon
		new[] { new Vector2(156, 44), new Vector2(90, 29), new Vector2(50, 51) },  // D_Cartoon
		new[] { new Vector2(164, 48), new Vector2(106, 25), new Vector2(38, 45) }, // E_Hachi
		new[] { new Vector2(164, 48), new Vector2(106, 25), new Vector2(38, 45) }, // F_Hachi
		new[] { new Vector2(164, 48), new Vector2(106, 25), new Vector2(38, 45) }, // G_Panda
		new[] { new Vector2(152, 34), new Vector2(132, 32), new Vector2(48, 52) }, // H_Hatch
	};

	private const string GarageSettingsPath = "user://settings.cfg";
	private const string GarageSection = "garage";

	/// <summary>The muted lavender the stage's captions use — see the Hint label in Main.tscn.</summary>
	private static readonly Color CaptionColour = new(0.4275f, 0.3961f, 0.5176f);

	private GarageCar _stageCar = null!;
	private Label _carName = null!;
	private Label _carSpec = null!;
	private Label _colorPill = null!;
	private HBoxContainer _thumbs = null!;

	private Texture2D[] _bodyTextures = null!;
	private Texture2D[] _trimTextures = null!;
	private StyleBoxFlat _pillStyle = null!;
	private StyleBoxFlat _thumbNormal = null!;
	private StyleBoxFlat _thumbSelected = null!;
	private readonly List<Button> _thumbButtons = new();
	private readonly List<GarageCar> _thumbCars = new();

	private readonly List<Button> _swatchButtons = new();
	private readonly List<StyleBoxFlat> _swatchNormalStyles = new();
	private readonly List<StyleBoxFlat> _swatchSelectedStyles = new();
	private readonly List<Button> _antennaButtons = new();

	private int _shownVariant = CarVariants.DefaultVariantIndex;
	private int _chosenColour = RacerAppearance.NoColour;
	private int _chosenAntenna = CarVariants.DefaultAntennaSpot;

	private void SetupShowcase()
	{
		_carName = GetNode<Label>("%CarName");
		_carSpec = GetNode<Label>("%CarSpec");
		_colorPill = GetNode<Label>("%ColorPill");
		_thumbs = GetNode<HBoxContainer>("%Thumbs");

		// CarImage is the stage's spotlight rectangle, authored in Main.tscn. The car inside it is
		// drawn rather than displayed now that it is layers plus an antenna, so the node is left as
		// the frame and the GarageCar fills it.
		_stageCar = new GarageCar();
		Control frame = GetNode<Control>("%CarImage");
		frame.AddChild(_stageCar);
		_stageCar.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

		LoadGarage();

		_bodyTextures = new Texture2D[GarageArt.Length];
		_trimTextures = new Texture2D[GarageArt.Length];
		for (int i = 0; i < GarageArt.Length; i++)
		{
			_bodyTextures[i] = GD.Load<Texture2D>(GarageArt[i]);
			_trimTextures[i] = GD.Load<Texture2D>(GarageArt[i].Replace(".svg", "_trim.svg"));
		}

		// The pill's border takes the picked paint's colour, so its box has to be per-instance
		// rather than authored in the scene where every state would share it.
		_pillStyle = new StyleBoxFlat
		{
			BgColor = Colors.Transparent,
			BorderColor = Colors.White,
			ContentMarginLeft = 18,
			ContentMarginRight = 18,
			ContentMarginTop = 5,
			ContentMarginBottom = 5,
		};
		_pillStyle.SetBorderWidthAll(1);
		_pillStyle.SetCornerRadiusAll(16);
		_colorPill.AddThemeStyleboxOverride("normal", _pillStyle);

		_thumbNormal = new StyleBoxFlat
		{
			BgColor = new Color(1, 1, 1, 0.05f),
			BorderColor = new Color(1, 1, 1, 0.12f),
		};
		_thumbNormal.SetBorderWidthAll(1);
		_thumbNormal.SetCornerRadiusAll(10);

		_thumbSelected = new StyleBoxFlat
		{
			BgColor = new Color(0.98f, 0.851f, 0.2f, 0.12f),
			BorderColor = new Color(0.98f, 0.851f, 0.2f),
		};
		_thumbSelected.SetBorderWidthAll(1);
		_thumbSelected.SetCornerRadiusAll(10);

		GetNode<Button>("%PrevButton").Pressed += () => ShowVariant(_shownVariant - 1);
		GetNode<Button>("%NextButton").Pressed += () => ShowVariant(_shownVariant + 1);

		for (int i = 0; i < CarVariants.All.Count; i++)
		{
			int index = i;
			var thumb = new Button
			{
				CustomMinimumSize = new Vector2(108, 58),
				// No focus: a focused thumb would eat the arrow keys as ui_left/ui_right focus
				// moves, and those keys are the point of the carousel.
				FocusMode = FocusModeEnum.None,
			};
			thumb.AddThemeStyleboxOverride("normal", _thumbNormal);
			thumb.AddThemeStyleboxOverride("hover", _thumbNormal);
			thumb.AddThemeStyleboxOverride("pressed", _thumbSelected);
			thumb.Pressed += () => ShowVariant(index);

			// The thumbs take the picked paint as well, which leaves the row saying what it is
			// actually choosing between: shapes. No antenna down here — at this size the whip is a
			// smudge, and the stage car above is already showing where it sits.
			var art = new GarageCar { ShowAntenna = false };
			thumb.AddChild(art);
			_thumbCars.Add(art);
			art.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
			art.OffsetLeft = 8;
			art.OffsetTop = 5;
			art.OffsetRight = -8;
			art.OffsetBottom = -5;

			_thumbs.AddChild(thumb);
			_thumbButtons.Add(thumb);
		}

		BuildPickerRows();
		ShowVariant(_shownVariant);
		UpdatePickerRows();
	}

	// ---- Persistence and the preference itself ----

	/// <summary>First run there is no file (or no garage section) and the defaults stand.</summary>
	private void LoadGarage()
	{
		var config = new ConfigFile();
		config.Load(GarageSettingsPath);

		_shownVariant = Mathf.PosMod(
			(int)config.GetValue(GarageSection, "variant", CarVariants.DefaultVariantIndex),
			CarVariants.All.Count);

		int colour = (int)config.GetValue(GarageSection, "colour", RacerAppearance.NoColour);
		_chosenColour = colour == RacerAppearance.NoColour
			? RacerAppearance.NoColour
			: Mathf.PosMod(colour, CarVariants.Palette.Length);

		_chosenAntenna = Mathf.PosMod(
			(int)config.GetValue(GarageSection, "antenna", CarVariants.DefaultAntennaSpot),
			CarVariants.AntennaSpots.Length);

		SyncPreference();
	}

	private void SaveGarage()
	{
		// Read-modify-write so the audio and keybind sections in the same file survive.
		var config = new ConfigFile();
		config.Load(GarageSettingsPath);
		config.SetValue(GarageSection, "variant", _shownVariant);
		config.SetValue(GarageSection, "colour", _chosenColour);
		config.SetValue(GarageSection, "antenna", _chosenAntenna);
		config.Save(GarageSettingsPath);
	}

	/// <summary>The one place the picks leave this pane. Host and solo read the property; a
	/// client sends it on connect — see <see cref="GameManager.PublishLocalPreference"/>.</summary>
	private void SyncPreference() =>
		GameManager.Instance.LocalPreference =
			new RacerAppearance(_shownVariant, _chosenColour, _chosenAntenna);

	// ---- The paint and antenna rows ----

	private void BuildPickerRows()
	{
		var nameplate = GetNode<VBoxContainer>("Layout/Stage/Nameplate");

		HBoxContainer paint = NewPickerRow(nameplate, "PAINT");
		NewSwatch(paint, RacerAppearance.NoColour);
		for (int i = 0; i < CarVariants.Palette.Length; i++)
			NewSwatch(paint, i);

		HBoxContainer antenna = NewPickerRow(nameplate, "ANTENNA");
		for (int i = 0; i < CarVariants.AntennaSpots.Length; i++)
		{
			int index = i;
			var button = new Button
			{
				Text = CarVariants.AntennaSpotNames[i].ToUpperInvariant(),
				CustomMinimumSize = new Vector2(110, 30),
				FocusMode = FocusModeEnum.None,
			};
			button.AddThemeFontSizeOverride("font_size", 13);
			button.Pressed += () => PickAntenna(index);
			antenna.AddChild(button);
			_antennaButtons.Add(button);
		}
	}

	private HBoxContainer NewPickerRow(VBoxContainer nameplate, string caption)
	{
		var center = new CenterContainer();
		nameplate.AddChild(center);

		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 8);
		center.AddChild(row);

		var label = new Label
		{
			Text = caption,
			CustomMinimumSize = new Vector2(84, 0),
			VerticalAlignment = VerticalAlignment.Center,
		};
		label.AddThemeColorOverride("font_color", CaptionColour);
		label.AddThemeFontSizeOverride("font_size", 13);
		row.AddChild(label);

		return row;
	}

	private void NewSwatch(HBoxContainer row, int colourIndex)
	{
		bool auto = colourIndex == RacerAppearance.NoColour;
		Color bg = auto ? new Color(1, 1, 1, 0.06f) : CarVariants.ColourAt(colourIndex);

		var normal = new StyleBoxFlat { BgColor = bg, BorderColor = new Color(1, 1, 1, 0.15f) };
		normal.SetBorderWidthAll(1);
		normal.SetCornerRadiusAll(8);
		_swatchNormalStyles.Add(normal);

		var selected = new StyleBoxFlat { BgColor = bg, BorderColor = Colors.White };
		selected.SetBorderWidthAll(2);
		selected.SetCornerRadiusAll(8);
		_swatchSelectedStyles.Add(selected);

		var button = new Button
		{
			// "Auto" spelt out; a coloured swatch says everything it needs to by being coloured.
			Text = auto ? "AUTO" : "",
			CustomMinimumSize = new Vector2(auto ? 64 : 44, 30),
			FocusMode = FocusModeEnum.None,
			TooltipText = auto ? "Deal me whatever is free" : CarVariants.ColourNameAt(colourIndex),
		};
		button.AddThemeFontSizeOverride("font_size", 13);
		button.Pressed += () => PickColour(colourIndex);
		row.AddChild(button);
		_swatchButtons.Add(button);
	}

	private void PickColour(int colourIndex)
	{
		_chosenColour = colourIndex;
		UpdatePickerRows();
		SyncPreference();
		SaveGarage();
	}

	private void PickAntenna(int spot)
	{
		_chosenAntenna = spot;
		UpdatePickerRows();
		SyncPreference();
		SaveGarage();
	}

	private void UpdatePickerRows()
	{
		// Swatch 0 is AUTO; the palette follows in order, so swatch i wears colour i - 1.
		for (int i = 0; i < _swatchButtons.Count; i++)
		{
			bool on = i == 0 ? _chosenColour == RacerAppearance.NoColour : i - 1 == _chosenColour;
			StyleBox style = on ? _swatchSelectedStyles[i] : _swatchNormalStyles[i];
			_swatchButtons[i].AddThemeStyleboxOverride("normal", style);
			_swatchButtons[i].AddThemeStyleboxOverride("hover", style);
			_swatchButtons[i].AddThemeStyleboxOverride("pressed", style);
		}

		for (int i = 0; i < _antennaButtons.Count; i++)
		{
			StyleBox style = i == _chosenAntenna ? _thumbSelected : _thumbNormal;
			_antennaButtons[i].AddThemeStyleboxOverride("normal", style);
			_antennaButtons[i].AddThemeStyleboxOverride("hover", style);
			_antennaButtons[i].AddThemeStyleboxOverride("pressed", style);
		}

		UpdatePill();
		RefreshCars();
	}

	/// <summary>One line stating the whole pick, coloured by the paint it names.</summary>
	private void UpdatePill()
	{
		bool auto = _chosenColour == RacerAppearance.NoColour;
		Color colour = auto
			? new Color(0.9255f, 0.9098f, 0.9569f)
			: CarVariants.ColourAt(_chosenColour);

		string paint = auto ? "AUTO PAINT" : $"{CarVariants.ColourNameAt(_chosenColour).ToUpperInvariant()} PAINT";
		_colorPill.Text =
			$"{paint} · {CarVariants.AntennaSpotNames[_chosenAntenna].ToUpperInvariant()} ANTENNA";
		_pillStyle.BorderColor = colour;
		_colorPill.AddThemeColorOverride("font_color", colour);
	}

	private void ShowVariant(int index)
	{
		_shownVariant = Mathf.PosMod(index, CarVariants.All.Count);
		CarVariants.Variant variant = CarVariants.At(_shownVariant);

		_carName.Text = variant.Name.Replace('_', ' ').ToUpperInvariant();
		_carSpec.Text = $"front rim {variant.ModelledFrontRadius:0.00} m · rear rim {variant.ModelledRearRadius:0.00} m";

		for (int i = 0; i < _thumbButtons.Count; i++)
		{
			StyleBox style = i == _shownVariant ? _thumbSelected : _thumbNormal;
			_thumbButtons[i].AddThemeStyleboxOverride("normal", style);
			_thumbButtons[i].AddThemeStyleboxOverride("hover", style);
		}

		RefreshCars();
		SyncPreference();
		SaveGarage();
	}

	/// <summary>
	/// Push the current pick onto the cars themselves. Called by every picker, so the stage answers
	/// the click that was just made rather than the next time the scene loads — which is the whole
	/// point of choosing here: what you are looking at is what you will be driving.
	/// </summary>
	private void RefreshCars()
	{
		_stageCar.SetCar(
			_bodyTextures[_shownVariant],
			_trimTextures[_shownVariant],
			PaintColour,
			GarageAntennaMounts[_shownVariant][_chosenAntenna]);

		for (int i = 0; i < _thumbCars.Count; i++)
			_thumbCars[i].SetCar(_bodyTextures[i], _trimTextures[i], PaintColour,
				GarageAntennaMounts[i][_chosenAntenna]);
	}

	/// <summary>What the shown car is painted. AUTO has no colour of its own to show — the server
	/// deals it, and only once there is a lobby — so it wears the shade this body was drawn in,
	/// which is its own place in the palette.</summary>
	private Color PaintColour => _chosenColour == RacerAppearance.NoColour
		? CarVariants.ColourAt(_shownVariant)
		: CarVariants.ColourAt(_chosenColour);

	/// <summary>Arrow keys browse the garage. Unhandled input, so a focused control (a LineEdit
	/// moving its caret, a button moving focus) always wins over the carousel.</summary>
	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is not InputEventKey { Pressed: true, Echo: false } key)
			return;

		int step = key.Keycode switch { Key.Left => -1, Key.Right => 1, _ => 0 };
		if (step == 0)
			return;

		ShowVariant(_shownVariant + step);
		GetViewport().SetInputAsHandled();
	}

	/// <summary>Called from <c>_ExitTree</c>. The styleboxes and textures here are C# wrappers a
	/// scene change orphans; left to the GC they can survive to .NET shutdown, where disposal
	/// after engine teardown crashes the process — see the disposal notes in git history.</summary>
	private void TearDownShowcase()
	{
		_pillStyle?.Dispose();
		_thumbNormal?.Dispose();
		_thumbSelected?.Dispose();
		foreach (StyleBoxFlat style in _swatchNormalStyles)
			style.Dispose();
		foreach (StyleBoxFlat style in _swatchSelectedStyles)
			style.Dispose();
		_swatchNormalStyles.Clear();
		_swatchSelectedStyles.Clear();
		DisposeTextures(ref _bodyTextures);
		DisposeTextures(ref _trimTextures);
	}

	private static void DisposeTextures(ref Texture2D[] textures)
	{
		if (textures != null)
			foreach (Texture2D texture in textures)
				texture?.Dispose();
		textures = null!;
	}
}

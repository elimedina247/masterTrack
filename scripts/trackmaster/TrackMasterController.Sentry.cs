using Godot;
using MasterTrack.Racer;
using MasterTrack.Sentry;

namespace MasterTrack.TrackMaster;

/// <summary>
/// The board's sentry gestures: arm an action from the bar, then click what it hits.
///
/// This is the view-and-input half only, the same split the tile side keeps — clicking a
/// marker produces a <i>request</i> to <see cref="SentryManager"/>, and nothing happens to
/// anybody's car until the server says so. Car targets are picked in screen space against the
/// same racer markers the board already draws (the marker is the thing the sentry is actually
/// looking at; asking them to hit the few grey pixels of car underneath it would be aiming at
/// the wrong thing). The missile is picked with a physics ray, because "that spot on the road"
/// is a place in the world, not a car.
/// </summary>
public partial class TrackMasterController
{
	/// <summary>What the sentry bar's status line should say. Also carries rejections.</summary>
	[Signal] public delegate void SentryStatusChangedEventHandler(string text);

	/// <summary>How far a click can miss a marker and still take it, in screen pixels.</summary>
	private const float MarkerPickRadiusPx = 70.0f;

	private SentryManager? _sentry;
	private bool _sentryArmed;
	private SentryActionKind _armedAction;

	/// <summary>First car of a chain pick, while waiting for the second.</summary>
	private RacerController? _chainFirstPick;

	// ---- The aiming ghost ----

	/// <summary>The ring under the cursor while a placed tool is armed. Null while disarmed.</summary>
	private MeshInstance3D? _aimGhost;

	private StandardMaterial3D? _aimPaint;

	/// <summary>Which tool the current ghost was built for, so re-arming rebuilds it.</summary>
	private SentryActionKind _aimKind;

	/// <summary>Hand the board its sentry once the race phase opens. Until then clicks are clicks.</summary>
	public void EnableSentry(SentryManager sentry)
	{
		// Re-enabling swaps managers cleanly rather than stacking subscriptions.
		UnhookSentry();
		_sentry = sentry;

		// The consequences come back through the manager: a confirmed placement flies the
		// follow-through camera, a resolved blast flashes whoever it caught. Hooked here rather
		// than in _Ready because only the sentry's machine ever gets this call — every other
		// peer's manager fires the same signals to a room with nobody in it.
		sentry.ActionPlaced += OnSentryActionPlaced;
		sentry.BlastLanded += OnSentryBlastLanded;

		// The job just changed from roads to cars, so the camera changes with it: start over
		// the middle of the pack. The toggle on the sentry bar goes back to the other modes.
		SetCameraMode(BoardCameraMode.Pack);
	}

	/// <summary>Take the manager's signal handlers back. Called from the controller's
	/// <c>_ExitTree</c> — a C# <c>+=</c> outlives the node that made it.</summary>
	private void UnhookSentry()
	{
		if (_sentry == null)
			return;

		_sentry.ActionPlaced -= OnSentryActionPlaced;
		_sentry.BlastLanded -= OnSentryBlastLanded;
	}

	/// <summary>
	/// A placed tool was confirmed somewhere on the board: take the sentry to it. This is the
	/// follow-through — the button was pressed seconds and hundreds of metres away from where
	/// the consequence happens, and without this the sentry plays half the match off-screen.
	/// </summary>
	private void OnSentryActionPlaced(int kind, Vector3 target)
	{
		if (!IsActive)
			return;

		// Never while the sentry is already lining up the next play: a stolen camera under an
		// armed tool means the click that comes back cancels the watch <i>and</i> fires — at
		// wherever the half-flown camera happened to be pointing. Aiming beats spectating.
		if (_sentryArmed)
			return;

		BeginSentryWatch(target, WatchSecondsOf((SentryActionKind)kind));
	}

	/// <summary>
	/// How long each tool's consequence is worth watching: long enough to see the payoff land,
	/// short enough that the camera is home before the next play. The ambush tools get the
	/// shortest stays — their payoff comes later, when somebody drives into them.
	/// </summary>
	private static float WatchSecondsOf(SentryActionKind kind) => kind switch
	{
		SentryActionKind.Missile => 3.6f,     // the 2 s descent, the blast, the scatter
		SentryActionKind.BarrelBomb => 3.2f,  // the 2 s fuse and the blast
		SentryActionKind.CargoSpill => 3.8f,  // the rainfall and the first bounces
		SentryActionKind.Magnet => 3.0f,      // the spin-up and the first grab
		SentryActionKind.OilSlick => 2.6f,    // the spread
		_ => 3.0f,
	};

	/// <summary>
	/// A blast resolved: flash the chevron of everyone it caught, so the board answers the shot
	/// where the sentry is actually looking. The count in words is the bar's job — see
	/// <see cref="UI.SentryBar"/> — this is the same fact drawn on the world.
	/// </summary>
	private void OnSentryBlastLanded(int[] caughtPeerIds)
	{
		foreach (RacerMarker marker in _markers)
		{
			if (!IsInstanceValid(marker.Racer) || !marker.Racer.IsInsideTree())
				continue;

			if (System.Array.IndexOf(caughtPeerIds, marker.Racer.OwnerPeerId) < 0)
				continue;

			if (marker.Root.GetChildOrNull<Label3D>(0) is not { } label)
				continue;

			// A white pop that settles back to the player's paint. Tweened on the label rather
			// than the root, because AimMarker rewrites the root's scale every frame.
			label.Modulate = Colors.White;
			Tween tween = label.CreateTween();
			tween.TweenProperty(label, "modulate", marker.Racer.PaintColor, 0.7)
				 .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
		}
	}

	/// <summary>Called by the sentry bar's buttons. Re-arming swaps the action cleanly.
	/// Moon gravity has no target — the whole point is that it lands on everyone — so its
	/// button fires the request outright instead of arming anything.</summary>
	public void ArmSentryAction(int kind)
	{
		if (_sentry == null)
			return;

		if ((SentryActionKind)kind == SentryActionKind.MoonGravity)
		{
			_sentryArmed = false;
			_sentry.RequestMoonGravity();
			EmitSignal(SignalName.SentryStatusChanged, "Moon gravity inbound!");
			return;
		}

		_sentryArmed = true;
		_armedAction = (SentryActionKind)kind;
		_chainFirstPick = null;

		EmitSignal(SignalName.SentryStatusChanged, _armedAction switch
		{
			SentryActionKind.Bouncy => "Bouncy!: click a racer's marker. Right-click to cancel.",
			SentryActionKind.ChainedUp => "Chained up!: click the first racer. Right-click to cancel.",
			SentryActionKind.Missile => "Missile: click a spot on the track. Right-click to cancel.",
			SentryActionKind.BarrelBomb => "Barrel bomb: click a spot on the track. Right-click to cancel.",
			SentryActionKind.RunawayBooster => "Runaway booster: click a racer's marker. Right-click to cancel.",
			SentryActionKind.CrossedWires => "Crossed wires!: click a racer's marker. Right-click to cancel.",
			SentryActionKind.OilSlick => "Oil slick: click a spot on the track. Right-click to cancel.",
			SentryActionKind.Magnet => "Magnet: click a spot on the track. Right-click to cancel.",
			SentryActionKind.CargoSpill => "Cargo spill: click a spot on the track. Right-click to cancel.",
			SentryActionKind.PopUpRamp => "Pop-up ramp: click near a hazard spot on the road. Right-click to cancel.",
			_ => "",
		});
	}

	public void CancelSentryAction()
	{
		ClearSentryAim();

		if (!_sentryArmed)
			return;

		_sentryArmed = false;
		_chainFirstPick = null;
		EmitSignal(SignalName.SentryStatusChanged, "");
	}

	/// <summary>
	/// Keep the armed tool's ghost under the cursor: a ring on the road, sized to the tool's
	/// <i>true</i> reach — the missile's ghost is exactly the circle its blast will own, the
	/// magnet's exactly its field. The honest-VFX rule, applied before the effect exists: what
	/// the sentry aims with is the same promise the racers will be dodging.
	///
	/// Called from the board's <c>_Process</c>. The pop-up ramp is the odd one out — it goes
	/// into a slot, not onto a point, so its ghost sits on the nearest free slot instead of
	/// under the cursor, which is also a live preview of exactly where the click will commit.
	/// </summary>
	private void UpdateSentryAim()
	{
		if (!_sentryArmed
			|| SentryActions.CategoryOf(_armedAction) != SentryActionCategory.PlaceOnTrack)
		{
			ClearSentryAim();
			return;
		}

		if (_aimGhost == null || _aimKind != _armedAction)
			BuildSentryAim();

		Vector2 mouse = GetViewport().GetMousePosition();
		bool found = TryPickTrackPoint(mouse, out Vector3 point);

		// The ramp snaps its ghost to the slot the click would actually take.
		if (found && _armedAction == SentryActionKind.PopUpRamp)
		{
			if (TryNearestFreeSurfaceSlot(point, out int tileIndex, out int slotIndex)
				&& Track!.SlotWorldTransform(tileIndex, slotIndex) is { } at)
				point = at.Origin;
			else
				found = false;
		}

		_aimGhost!.Visible = found;
		if (!found)
			return;

		_aimGhost.GlobalPosition = point + Vector3.Up * 0.6f;

		// The pulse that says "armed". Gentle — the ghost is information first.
		float wave = Mathf.Sin(Time.GetTicksMsec() / 1000.0f * 5.0f);
		Color colour = _aimPaint!.AlbedoColor;
		colour.A = 0.4f + 0.15f * wave;
		_aimPaint.AlbedoColor = colour;
	}

	/// <summary>The ring itself: the armed tool's honest radius in the tool's own colour, drawn
	/// through the road the way the slot highlights are — an aim on the far side of a crest
	/// still reads.</summary>
	private void BuildSentryAim()
	{
		ClearSentryAim();

		(float radius, Color colour) = _armedAction switch
		{
			// The blasts wear the missile's warning red: the ghost is the target ring, early.
			SentryActionKind.Missile => (SentryMissile.ExplosionRadius, new Color(1.0f, 0.2f, 0.15f)),
			SentryActionKind.BarrelBomb => (SentryBarrelBomb.ExplosionRadius, new Color(1.0f, 0.2f, 0.15f)),
			// The magnet's own blue, the oil's own murk, the spill's cargo-crate amber.
			SentryActionKind.Magnet => (SentryMagnet.Radius, new Color(0.35f, 0.55f, 1.0f)),
			SentryActionKind.OilSlick => (SentryActions.OilSlickRadius, new Color(0.35f, 0.28f, 0.18f)),
			SentryActionKind.CargoSpill => (SentryCargoSpill.SpillRadius, new Color(1.0f, 0.62f, 0.2f)),
			// The ramp marks a slot, so it wears the slot highlights' green at their size.
			_ => (6.0f, new Color(0.35f, 1.0f, 0.45f)),
		};

		_aimPaint = new StandardMaterial3D
		{
			AlbedoColor = colour with { A = 0.5f },
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			NoDepthTest = true,
		};

		_aimGhost = new MeshInstance3D
		{
			Name = "SentryAimGhost",
			Mesh = new TorusMesh
			{
				InnerRadius = radius * 0.92f,
				OuterRadius = radius,
				Material = _aimPaint,
			},
			Visible = false,
		};
		AddChild(_aimGhost);

		_aimKind = _armedAction;
	}

	private void ClearSentryAim()
	{
		if (_aimGhost == null)
			return;

		_aimGhost.QueueFree();
		_aimGhost = null;

		// Freed with its ghost rather than kept for reuse: an armed tool is a moment, not a
		// mode, and the exit-crash rule wants every wrapper's owner obvious.
		_aimPaint?.Dispose();
		_aimPaint = null;
	}

	/// <summary>
	/// First look at input while the sentry is live. Returns true when the event was a sentry
	/// gesture, so the rest of the board's input handling stays out of it. Wheel and camera-look
	/// events return false on purpose — aiming a missile should not cost the sentry their zoom.
	///
	/// The number row arms tools without a trip to the bar: <c>1</c>–<c>9</c> for the first nine
	/// actions, <c>Shift</c> for the overflow — the same order the buttons show, because the bar
	/// prints each button's key from the same arithmetic. Verified free of the board's other
	/// bindings, which are only <c>builder_undo</c> and <c>camera_look</c>.
	/// </summary>
	private bool HandleSentryInput(InputEvent @event)
	{
		if (_sentry == null)
			return false;

		if (@event is InputEventKey { Pressed: true, Echo: false } key
			&& key.Keycode is >= Key.Key1 and <= Key.Key9)
		{
			int kind = (int)key.Keycode - (int)Key.Key1 + (key.ShiftPressed ? 9 : 0);
			if (kind < System.Enum.GetValues<SentryActionKind>().Length)
			{
				ArmSentryAction(kind);
				return true;
			}

			return false;
		}

		if (!_sentryArmed)
			return false;

		if (@event is not InputEventMouseButton { Pressed: true } mouse)
			return false;

		switch (mouse.ButtonIndex)
		{
			case MouseButton.Right:
				CancelSentryAction();
				return true;

			case MouseButton.Left:
				PickSentryTarget(mouse.Position);
				return true;

			default:
				return false;
		}
	}

	private void PickSentryTarget(Vector2 screenPosition)
	{
		switch (_armedAction)
		{
			case SentryActionKind.Bouncy:
			{
				RacerController? racer = RacerNearScreenPoint(screenPosition);
				if (racer == null)
				{
					EmitSignal(SignalName.SentryStatusChanged, "No racer there — click a marker.");
					return;
				}

				// Disarmed before the request: in solo the confirmation comes back
				// synchronously, and the follow-through camera only flies for a shot that
				// is no longer being aimed.
				_sentryArmed = false;
				_sentry!.RequestBouncy(racer.OwnerPeerId);
				EmitSignal(SignalName.SentryStatusChanged, "Bouncy! away.");
				return;
			}

			case SentryActionKind.ChainedUp:
			{
				RacerController? racer = RacerNearScreenPoint(screenPosition);
				if (racer == null)
				{
					EmitSignal(SignalName.SentryStatusChanged, "No racer there — click a marker.");
					return;
				}

				if (_chainFirstPick == null || !IsInstanceValid(_chainFirstPick))
				{
					_chainFirstPick = racer;
					EmitSignal(SignalName.SentryStatusChanged, "Now click the second racer.");
					return;
				}

				if (racer == _chainFirstPick)
				{
					EmitSignal(SignalName.SentryStatusChanged, "That's the same car — pick another.");
					return;
				}

				_sentryArmed = false;
				_sentry!.RequestChain(_chainFirstPick.OwnerPeerId, racer.OwnerPeerId);
				_chainFirstPick = null;
				EmitSignal(SignalName.SentryStatusChanged, "Chained up!");
				return;
			}

			case SentryActionKind.Missile:
			{
				if (!TryPickTrackPoint(screenPosition, out Vector3 point))
				{
					EmitSignal(SignalName.SentryStatusChanged, "Nothing there — click the road.");
					return;
				}

				// Disarmed before the request: in solo the confirmation comes back
				// synchronously, and the follow-through camera only flies for a shot that
				// is no longer being aimed.
				_sentryArmed = false;
				_sentry!.RequestMissile(point);
				EmitSignal(SignalName.SentryStatusChanged, "Missile away!");
				return;
			}

			case SentryActionKind.BarrelBomb:
			{
				if (!TryPickTrackPoint(screenPosition, out Vector3 point))
				{
					EmitSignal(SignalName.SentryStatusChanged, "Nothing there — click the road.");
					return;
				}

				// Disarmed before the request: in solo the confirmation comes back
				// synchronously, and the follow-through camera only flies for a shot that
				// is no longer being aimed.
				_sentryArmed = false;
				_sentry!.RequestBarrel(point);
				EmitSignal(SignalName.SentryStatusChanged, "Barrel planted.");
				return;
			}

			case SentryActionKind.RunawayBooster:
			{
				RacerController? racer = RacerNearScreenPoint(screenPosition);
				if (racer == null)
				{
					EmitSignal(SignalName.SentryStatusChanged, "No racer there — click a marker.");
					return;
				}

				// Disarmed before the request: in solo the confirmation comes back
				// synchronously, and the follow-through camera only flies for a shot that
				// is no longer being aimed.
				_sentryArmed = false;
				_sentry!.RequestBooster(racer.OwnerPeerId);
				EmitSignal(SignalName.SentryStatusChanged, "Booster strapped on.");
				return;
			}

			case SentryActionKind.CrossedWires:
			{
				RacerController? racer = RacerNearScreenPoint(screenPosition);
				if (racer == null)
				{
					EmitSignal(SignalName.SentryStatusChanged, "No racer there — click a marker.");
					return;
				}

				// Disarmed before the request: in solo the confirmation comes back
				// synchronously, and the follow-through camera only flies for a shot that
				// is no longer being aimed.
				_sentryArmed = false;
				_sentry!.RequestWires(racer.OwnerPeerId);
				EmitSignal(SignalName.SentryStatusChanged, "Wires crossed.");
				return;
			}

			case SentryActionKind.OilSlick:
			{
				if (!TryPickTrackPoint(screenPosition, out Vector3 point))
				{
					EmitSignal(SignalName.SentryStatusChanged, "Nothing there — click the road.");
					return;
				}

				// Disarmed before the request: in solo the confirmation comes back
				// synchronously, and the follow-through camera only flies for a shot that
				// is no longer being aimed.
				_sentryArmed = false;
				_sentry!.RequestOilSlick(point);
				EmitSignal(SignalName.SentryStatusChanged, "Oil poured.");
				return;
			}

			case SentryActionKind.Magnet:
			{
				if (!TryPickTrackPoint(screenPosition, out Vector3 point))
				{
					EmitSignal(SignalName.SentryStatusChanged, "Nothing there — click the road.");
					return;
				}

				// Disarmed before the request: in solo the confirmation comes back
				// synchronously, and the follow-through camera only flies for a shot that
				// is no longer being aimed.
				_sentryArmed = false;
				_sentry!.RequestMagnet(point);
				EmitSignal(SignalName.SentryStatusChanged, "Magnet planted.");
				return;
			}

			case SentryActionKind.CargoSpill:
			{
				if (!TryPickTrackPoint(screenPosition, out Vector3 point))
				{
					EmitSignal(SignalName.SentryStatusChanged, "Nothing there — click the road.");
					return;
				}

				// Disarmed before the request: in solo the confirmation comes back
				// synchronously, and the follow-through camera only flies for a shot that
				// is no longer being aimed.
				_sentryArmed = false;
				_sentry!.RequestCargoSpill(point);
				EmitSignal(SignalName.SentryStatusChanged, "Cargo away!");
				return;
			}

			case SentryActionKind.PopUpRamp:
			{
				if (!TryPickTrackPoint(screenPosition, out Vector3 point)
					|| !TryNearestFreeSurfaceSlot(point, out int tileIndex, out int slotIndex))
				{
					EmitSignal(SignalName.SentryStatusChanged,
							   "No free hazard spot near there — slotted pieces only.");
					return;
				}

				// Disarmed before the request: in solo the confirmation comes back
				// synchronously, and the follow-through camera only flies for a shot that
				// is no longer being aimed.
				_sentryArmed = false;
				_sentry!.RequestPopUpRamp(tileIndex, slotIndex);
				EmitSignal(SignalName.SentryStatusChanged, "Ramp rising!");
				return;
			}
		}
	}

	/// <summary>
	/// The free surface slot nearest a clicked point on the road, within most of a tile. The
	/// sentry aims at road the way the missile does; the slot system decides what "there" means,
	/// which is the same curation every other hazard placement gets.
	/// </summary>
	private bool TryNearestFreeSurfaceSlot(Vector3 point, out int tileIndex, out int slotIndex)
	{
		tileIndex = -1;
		slotIndex = -1;

		if (Track == null)
			return false;

		float bestDistance = Tiles.TrackTile.Size * 0.8f;

		for (int tile = Track.Grid.OldestLiveIndex; tile < Track.Grid.Count; tile++)
		{
			var slots = Track.SlotsOf(tile);
			for (int slot = 0; slot < slots.Count; slot++)
			{
				if (slots[slot].Kind != Tiles.HazardSlotKind.Surface
					|| !Track.IsSlotFree(tile, slot))
					continue;

				if (Track.SlotWorldTransform(tile, slot) is not { } at)
					continue;

				float distance = at.Origin.DistanceTo(point);
				if (distance >= bestDistance)
					continue;

				bestDistance = distance;
				tileIndex = tile;
				slotIndex = slot;
			}
		}

		return tileIndex >= 0;
	}

	/// <summary>
	/// The racer whose marker is nearest a screen point, within <see cref="MarkerPickRadiusPx"/>.
	/// Picked in screen space rather than by ray, because the markers are drawn depth-free at
	/// constant screen size — screen distance is exactly what "I clicked that one" means here.
	/// </summary>
	private RacerController? RacerNearScreenPoint(Vector2 screenPosition)
	{
		RacerController? best = null;
		float bestDistance = MarkerPickRadiusPx;

		foreach (RacerMarker marker in _markers)
		{
			if (!IsInstanceValid(marker.Racer) || !marker.Racer.IsInsideTree())
				continue;

			Vector3 world = marker.Racer.GlobalPosition;
			if (_camera.IsPositionBehind(world))
				continue;

			float distance = _camera.UnprojectPosition(world).DistanceTo(screenPosition);
			if (distance >= bestDistance)
				continue;

			bestDistance = distance;
			best = marker.Racer;
		}

		return best;
	}

	/// <summary>Where in the world a click lands: a ray from the board camera to the track.</summary>
	private bool TryPickTrackPoint(Vector2 screenPosition, out Vector3 point)
	{
		Vector3 from = _camera.ProjectRayOrigin(screenPosition);
		Vector3 direction = _camera.ProjectRayNormal(screenPosition);

		var query = PhysicsRayQueryParameters3D.Create(from, from + direction * 4000.0f);
		Godot.Collections.Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(query);

		if (hit.Count == 0)
		{
			point = Vector3.Zero;
			return false;
		}

		point = hit["position"].AsVector3();
		return true;
	}
}

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

	/// <summary>Hand the board its sentry once the race phase opens. Until then clicks are clicks.</summary>
	public void EnableSentry(SentryManager sentry)
	{
		_sentry = sentry;

		// The job just changed from roads to cars, so the camera changes with it: start over
		// the middle of the pack. The toggle on the sentry bar goes back to the other modes.
		SetCameraMode(BoardCameraMode.Pack);
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
			_ => "",
		});
	}

	public void CancelSentryAction()
	{
		if (!_sentryArmed)
			return;

		_sentryArmed = false;
		_chainFirstPick = null;
		EmitSignal(SignalName.SentryStatusChanged, "");
	}

	/// <summary>
	/// First look at input while armed. Returns true when the event was a sentry gesture, so
	/// the rest of the board's input handling stays out of it. Wheel and camera-look events
	/// return false on purpose — aiming a missile should not cost the sentry their zoom.
	/// </summary>
	private bool HandleSentryInput(InputEvent @event)
	{
		if (!_sentryArmed || _sentry == null)
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

				_sentry!.RequestBouncy(racer.OwnerPeerId);
				_sentryArmed = false;
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

				_sentry!.RequestChain(_chainFirstPick.OwnerPeerId, racer.OwnerPeerId);
				_sentryArmed = false;
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

				_sentry!.RequestMissile(point);
				_sentryArmed = false;
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

				_sentry!.RequestBarrel(point);
				_sentryArmed = false;
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

				_sentry!.RequestBooster(racer.OwnerPeerId);
				_sentryArmed = false;
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

				_sentry!.RequestWires(racer.OwnerPeerId);
				_sentryArmed = false;
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

				_sentry!.RequestOilSlick(point);
				_sentryArmed = false;
				EmitSignal(SignalName.SentryStatusChanged, "Oil poured.");
				return;
			}
		}
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

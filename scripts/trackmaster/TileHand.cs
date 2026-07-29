using System;
using Godot;
using MasterTrack.Tiles;

namespace MasterTrack.TrackMaster;

/// <summary>
/// The Track Master's hand: a fixed row of slots that fill themselves with random tiles over
/// time and empty as those tiles are spent on the track.
///
/// This is what turns building from "pick the perfect piece" into spending a resource. The
/// Track Master doesn't choose what they get — <see cref="TileCatalog.DrawIndex"/> does, by
/// weight — they choose what to do with it and when. Holding a Gap back for the corner where it
/// will hurt costs a slot the whole time it waits, and a hand that runs dry while the racers are
/// eating track is the pressure the role is built on.
///
/// The tiles are kept packed against the left of the row: spending one closes the gap behind it,
/// the way a hand of cards does. That keeps the empties in one run on the right, which is what
/// lets the countdown to the next tile live in a fixed spot at the end of the row.
///
/// The deal clock only runs when there is somewhere for a tile to go. A full hand is a stalled
/// hand, and it holds its progress rather than banking or losing it — spend a tile and the count
/// picks up where it stopped.
/// </summary>
public sealed class TileHand
{
	/// <summary>Value of a slot with no tile in it.</summary>
	public const int Empty = -1;

	private readonly int[] _slots;
	private readonly RandomNumberGenerator _rng = new();

	/// <summary>Seconds accumulated toward the next tile.</summary>
	private float _elapsed;

	/// <summary>
	/// A hand of <paramref name="slotCount"/> slots, dealing one tile every
	/// <paramref name="dealInterval"/> seconds, opening with <paramref name="startingTiles"/>
	/// already in it. The opening hand matters: with nothing to place, the Track Master would
	/// spend the first seconds of the race watching the cars drive off the end of the track.
	/// </summary>
	public TileHand(int slotCount, float dealInterval, int startingTiles = 0)
	{
		_slots = new int[Mathf.Max(1, slotCount)];
		DealInterval = Mathf.Max(0.05f, dealInterval);
		_rng.Randomize();

		// Drawn the same weighted way as everything after them, so an opening hand is a fair
		// sample of the deal rather than a scripted set.
		for (int i = 0; i < startingTiles && !IsFull; i++)
			Deal(null);
	}

	/// <summary>Seconds between one dealt tile and the next.</summary>
	public float DealInterval { get; set; }

	public int SlotCount => _slots.Length;

	/// <summary>How many tiles are in hand. They always occupy the first <c>Count</c> slots.</summary>
	public int Count { get; private set; }

	public bool IsFull => Count >= _slots.Length;

	/// <summary>Catalog index held in a slot, or <see cref="Empty"/> if that slot is empty.</summary>
	public int At(int slot) => slot >= 0 && slot < Count ? _slots[slot] : Empty;

	/// <summary>
	/// The rightmost empty slot — the end of the row — or -1 when the hand is full. This is
	/// where the countdown to the next tile is shown; because the tiles stay packed left it
	/// doesn't move around as the hand fills and empties.
	/// </summary>
	public int CooldownSlot => IsFull ? -1 : _slots.Length - 1;

	/// <summary>How far along the next tile is, 0 to 1. Frozen while the hand is full.</summary>
	public float DealProgress => Mathf.Clamp(_elapsed / DealInterval, 0.0f, 1.0f);

	/// <summary>Seconds until the next tile lands.</summary>
	public float TimeToNextDeal => Mathf.Max(0.0f, DealInterval - _elapsed);

	/// <summary>
	/// Advance the deal clock, dealing a tile if one is due. Returns true when the contents of
	/// the hand actually changed, so the caller only refreshes the tray when there's a reason.
	/// </summary>
	public bool Tick(float delta, Func<int, bool>? placeable = null)
	{
		// Nowhere to put it: the clock stops rather than running on and losing the progress.
		if (IsFull)
			return false;

		_elapsed += delta;
		if (_elapsed < DealInterval)
			return false;

		// Carried rather than zeroed, so a long frame doesn't quietly stretch the interval.
		_elapsed -= DealInterval;
		Deal(placeable);
		return true;
	}

	/// <summary>
	/// Put one weighted-random tile in the next free slot. Callers check for room.
	///
	/// Normally that is a draw across the whole catalog. If <paramref name="placeable"/> is given
	/// and the hand currently holds nothing that can go on the track, it is drawn from what can go
	/// on the track instead — the backstop that stops a Track Master sitting on a full hand of
	/// tiles they are not allowed to play while the racers eat the road in front of them.
	///
	/// The filter is only reached for when the hand is already stuck, so a healthy hand is dealt
	/// exactly as it always was and the rescue never shows up as a bias in what gets dealt.
	/// </summary>
	private void Deal(Func<int, bool>? placeable)
	{
		_slots[Count++] = placeable != null && !HasPlaceable(placeable)
			? TileCatalog.DrawIndex(_rng, placeable)
			: TileCatalog.DrawIndex(_rng);
	}

	/// <summary>Whether any tile in hand could go on the track as it stands.</summary>
	public bool HasPlaceable(Func<int, bool> placeable)
	{
		for (int i = 0; i < Count; i++)
		{
			if (placeable(_slots[i]))
				return true;
		}

		return false;
	}

	/// <summary>
	/// Deal a placeable tile straight away if the hand has none and there is room for one. Returns
	/// true when it did, so the caller knows to refresh the tray.
	///
	/// The filtered deal in <see cref="Deal"/> already guarantees the hand recovers, but only at the
	/// next interval — and at a 2.4 second deal that is over a hundred metres of road the racers are
	/// already driving into. This is what makes the recovery immediate.
	///
	/// It leaves the deal clock alone. The tile is a rescue, not an advance on the next one, so the
	/// countdown the Track Master is watching does not jump.
	/// </summary>
	public bool TopUp(Func<int, bool> placeable)
	{
		if (IsFull || HasPlaceable(placeable))
			return false;

		Deal(placeable);
		return true;
	}

	/// <summary>
	/// Spend the tile in a slot and close the hand up behind it. Returns the catalog index that
	/// was taken, or <see cref="Empty"/> if that slot held nothing.
	/// </summary>
	public int Take(int slot)
	{
		int index = At(slot);
		if (index == Empty)
			return Empty;

		for (int i = slot; i < Count - 1; i++)
			_slots[i] = _slots[i + 1];

		Count--;
		return index;
	}
}

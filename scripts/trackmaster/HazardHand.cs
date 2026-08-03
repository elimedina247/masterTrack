using Godot;
using MasterTrack.Tiles;

namespace MasterTrack.TrackMaster;

/// <summary>
/// The builder's hazard hand: a short row of slots that fill themselves with random furniture
/// hazards over time, the tile hand's little sibling. Its own list on purpose — a hazard never
/// competes with a tile for a draw, so taking the ramp is never the reason you didn't get the
/// corner you needed.
///
/// Deliberately slower and smaller than the tile hand. Tiles are the job; hazards are the
/// seasoning, and a hand that dealt them as fast as road would turn every straight into a
/// minefield by lap two. The cadence, not a price, is what rations them.
///
/// Same trust model as the tile hand: the hand lives on the builder's machine and the server
/// only ever validates that the placement itself is legal — which slot, which kind, is it
/// free. A client that lied about its hand could only give itself furniture the server was
/// happy to allow anyway.
/// </summary>
public sealed class HazardHand
{
	/// <summary>Value of a slot with no hazard in it.</summary>
	public const int Empty = -1;

	private readonly int[] _slots;
	private readonly RandomNumberGenerator _rng = new();
	private float _elapsed;

	public HazardHand(int slotCount, float dealInterval, int startingHazards = 0)
	{
		_slots = new int[Mathf.Max(1, slotCount)];
		DealInterval = Mathf.Max(0.05f, dealInterval);
		_rng.Randomize();

		for (int i = 0; i < startingHazards && !IsFull; i++)
			Deal();
	}

	/// <summary>Seconds between one dealt hazard and the next.</summary>
	public float DealInterval { get; set; }

	public int SlotCount => _slots.Length;

	/// <summary>How many hazards are in hand. They always occupy the first <c>Count</c> slots.</summary>
	public int Count { get; private set; }

	public bool IsFull => Count >= _slots.Length;

	/// <summary>The kind held in a slot as an int, or <see cref="Empty"/>.</summary>
	public int At(int slot) => slot >= 0 && slot < Count ? _slots[slot] : Empty;

	/// <summary>The rightmost empty slot, where the countdown shows — the tile hand's rule.</summary>
	public int CooldownSlot => IsFull ? -1 : _slots.Length - 1;

	/// <summary>How far along the next hazard is, 0 to 1. Frozen while the hand is full.</summary>
	public float DealProgress => Mathf.Clamp(_elapsed / DealInterval, 0.0f, 1.0f);

	/// <summary>Advance the deal clock. Returns true when the hand actually changed.</summary>
	public bool Tick(float delta)
	{
		if (IsFull)
			return false;

		_elapsed += delta;
		if (_elapsed < DealInterval)
			return false;

		_elapsed -= DealInterval;
		Deal();
		return true;
	}

	/// <summary>
	/// Put one hazard back in the hand — a lifted hazard coming home, or a placement the server
	/// refused. Returns false with a full hand, in which case the hazard is simply lost; rare
	/// enough not to deserve machinery.
	/// </summary>
	public bool Return(HazardKind kind)
	{
		if (IsFull)
			return false;

		_slots[Count++] = (int)kind;
		return true;
	}

	/// <summary>Spend the hazard in a slot and close the hand up behind it.</summary>
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

	/// <summary>An even draw across every kind there is. Two kinds today; the enum is the deck.</summary>
	private void Deal()
		=> _slots[Count++] = _rng.RandiRange(0, System.Enum.GetValues<HazardKind>().Length - 1);
}

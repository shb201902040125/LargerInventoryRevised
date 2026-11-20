using System;

namespace LargerInventory.Backend.Commands;

/// <summary>Tracks whether slot access can join the current bounded wave.</summary>
internal struct SlotConflictWave
{
    public const int MaxSlotsPerCommand = 16;
    public const int MaxCommands = 4;

    private ulong _occupied;
    private ulong _writes;

    public int Count { get; private set; }

    public bool TryAdd(ReadOnlySpan<int> slots, bool readOnly)
    {
        if (Count >= MaxCommands || slots.Length > MaxSlotsPerCommand)
        {
            return false;
        }

        ulong mask = 0;
        foreach (var slot in slots)
        {
            // Take the upper six bits of the multiplicative hash to spread nearby slots.
            var bit = (int)(unchecked((uint)slot * 2654435761u) >> 26);
            mask |= 1UL << bit;
        }

        // The same slot always sets the same bit. Aliases can only add conflicts.
        if ((mask & (readOnly ? _writes : _occupied)) != 0)
        {
            return false;
        }

        _occupied |= mask;
        if (!readOnly)
        {
            _writes |= mask;
        }

        Count++;
        return true;
    }
}

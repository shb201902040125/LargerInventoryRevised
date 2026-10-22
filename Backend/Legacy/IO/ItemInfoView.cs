using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.Default;

namespace LargerInventory.Backend.IO;

public class ItemInfoView : IEnumerable<int>
{
    private string _type;
    private int _quickType = -1;

    public int Type
    {
        get
        {
            if (_quickType != -1)
            {
                return _quickType;
            }

            if (int.TryParse(_type, out var res) && res < ItemID.Count)
            {
                return res;
            }
            else
            {
                return ModContent.TryFind<ModItem>(_type, out var modItem)
                    ? modItem.Type
                    : ModContent.ItemType<UnloadedItem>();
            }
        }
        set
        {
            _type = value < 0 ? 0.ToString() :
                value < ItemID.Count ? value.ToString() :
                value > ItemLoader.ItemCount ? 0.ToString() : ItemLoader.GetItem(value).FullName;
            _quickType = value < 0 ? 0 : value > ItemLoader.ItemCount ? 0 : value;
        }
    }

    public string FullName => Type < ItemID.Count ? Type.ToString() : _type;

    public int AllStack { get; internal set; }

    private BitArray _map = new(1024);

    public void WriteTo(BinaryWriter writer)
    {
        writer.Write(_type);
        writer.Write(AllStack);
        var buffer = ArrayPool<byte>.Shared.Rent(128);
        try
        {
            _map.CopyTo(buffer, 0);
            writer.Write(buffer, 0, 128);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void ReadFrom(BinaryReader reader)
    {
        _type = reader.ReadString();
        _quickType = -1;

        AllStack = reader.ReadInt32();
        _map = new BitArray(reader.ReadBytes(128));
    }

    internal bool this[int index]
    {
        get => _map[index];
        set => _map[index] = value;
    }

    internal static ItemInfoView CreateFromItem(Item item)
    {
        var res = new ItemInfoView
        {
            Type = item.type,
            AllStack = item.stack
        };
        return res;
    }

    internal bool TryCombine(ItemInfoView other, out ItemInfoView res)
    {
        res = null;
        if (Type != other.Type && _type != other._type)
        {
            return false;
        }

        res = new ItemInfoView()
        {
            Type = Type
        };

        var diff = int.MaxValue - AllStack;
        if (diff > other.AllStack)
        {
            res.AllStack = AllStack + other.AllStack;
        }
        else
        {
            res.AllStack = int.MaxValue;
        }

        for (var i = 0; i < 1024; i++)
        {
            res._map[i] = _map[i] | other._map[i];
        }

        return true;
    }

    public IEnumerator<int> GetEnumerator()
    {
        for (var i = 0; i < _map.Length; i++)
        {
            if (_map[i])
            {
                yield return i;
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.Default;
using Terraria.ModLoader.IO;

namespace LargerInventory.Backend.IO;

public class DataPage
{
    private static readonly byte[] HeadCheck = Encoding.ASCII.GetBytes("LIData");

    private Dictionary<int, ItemInfoView> _quickView = [];
    private Dictionary<string, ItemInfoView> _unloadedView = [];

    private bool _quickViewLoaded = false;
    internal bool ViewLoaded => _quickViewLoaded;
    private bool _quickViewError = false;
    private int _quickViewOffset = -1;
    private bool _quickViewDirty = false;

    private Item[] _items = new Item[1024];
    private bool _itemsLoaded = false;
    internal bool ItemsLoaded => _itemsLoaded;
    private bool _itemsError = false;
    private int _itemsOffset = -1;
    private bool _itemsDirty = false;

    private string _filePath;
    internal string FilePath => _filePath;
    private FileStream _fileStream;
    private BinaryReader _fileReader;

    internal DataPage(string filePath)
    {
        _filePath = filePath;
    }

    private bool CheckFileStream()
    {
        if (_fileStream is null)
        {
            _fileStream = File.Open(_filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            _fileReader = new BinaryReader(_fileStream);
        }
        else if (_fileReader is null)
        {
            _fileReader = new BinaryReader(_fileStream);
        }

        if (_quickViewOffset != -1 && _itemsOffset != -1)
        {
            return true;
        }

        if (!HeadCheck.AsSpan().SequenceEqual(_fileReader.ReadBytes(HeadCheck.Length).AsSpan()))
        {
            return Debug.IsStrict ? throw new InvalidDataException("Unknown data format") : false;
        }

        _itemsOffset = _fileReader.ReadInt32();
        _quickViewOffset = (int)_fileStream.Position;
        return true;
    }

    private void CloseFileStream()
    {
        _fileReader?.Close();
        _fileStream?.Close();
        _fileReader = null;
        _fileStream = null;
        _quickViewOffset = -1;
        _itemsOffset = -1;
    }

    void PushView(ItemInfoView view)
    {
        var key = view.Type;
        if (key == ModContent.ItemType<UnloadedItem>())
        {

            var fullName = view.FullName;
            if (_unloadedView.TryGetValue(fullName, out var unloadedInfo))
            {
                if (unloadedInfo.TryCombine(view, out var newInfo2))
                {
                    _unloadedView[fullName] = newInfo2;
                }
                else
                {
                    throw new UnreachableException();
                }
            }
            else
            {
                _unloadedView[fullName] = view;
            }
        }
        else
        {

            if (_quickView.TryGetValue(key, out var viewInfo))
            {
                if (viewInfo.TryCombine(view, out var newInfo))
                {
                    _quickView[key] = newInfo;
                }
                else
                {
                    throw new UnreachableException();
                }
            }
            else
            {
                _quickView.Add(key, view);
            }
        }
    }

    internal bool TryLoadView()
    {
        _quickViewLoaded = false;
        _quickViewError = false;
        try
        {
            if (!CheckFileStream())
            {
                return false;
            }

            _fileStream.Seek(_quickViewOffset, SeekOrigin.Begin);
            var viewCount = _fileReader.ReadInt32();
            _quickView.Clear();
            _unloadedView.Clear();
            for (var i = 0; i < viewCount; i++)
            {
                var view = new ItemInfoView();
                view.ReadFrom(_fileReader);
                PushView(view);
            }

            viewCount = _fileReader.ReadInt32();
            for (var i = 0; i < viewCount; i++)
            {
                var view = new ItemInfoView();
                view.ReadFrom(_fileReader);
                PushView(view);
            }

            _quickViewLoaded = true;
            return true;
        }
        catch (Exception e)
        {
            _quickViewError = true;
            _quickViewOffset = -1;
            if (Debug.IsStrict)
            {
                throw;
            }

            Debug.Error_Internal(e);
            return false;
        }
    }

    internal bool TryLoadItems()
    {
        _itemsLoaded = false;
        _itemsError = false;
        try
        {
            if (!CheckFileStream())
            {
                return false;
            }

            _fileStream.Seek(_itemsOffset, SeekOrigin.Begin);

            var tag = TagIO.FromStream(_fileStream);
            _items = tag.Get<Item[]>(".");

            _quickView.Clear();
            for (var i = 0; i < 1024; i++)
            {
                var item = _items[i];
                var newInfo = ItemInfoView.CreateFromItem(item);
                newInfo[i] = true;
                PushView(newInfo);
            }

            _itemsLoaded = true;
            _quickViewLoaded = true;
            return true;
        }
        catch (Exception e)
        {
            _itemsError = true;
            _itemsOffset = -1;
            if (Debug.IsStrict)
            {
                throw;
            }

            Debug.Error_Internal(e);
            return false;
        }
    }

    internal bool TrySaveDataPage()
    {
        try
        {
            if (!_itemsDirty)
            {
                return true;
            }

            CheckFileStream();
            _fileStream.Seek(0, SeekOrigin.Begin);
            using BinaryWriter writer = new(_fileStream);
            writer.Write(HeadCheck);
            writer.Write(0);
            _quickView.Clear();
            _unloadedView.Clear();
            for (var i = 0; i < 1024; i++)
            {
                var item = _items[i];
                var newInfo = ItemInfoView.CreateFromItem(item);
                newInfo[i] = true;
                PushView(newInfo);
            }

            writer.Write(_quickView.Count);
            foreach (var info in _quickView.Values)
            {
                info.WriteTo(writer);
            }

            writer.Write(_unloadedView.Count);
            foreach (var info in _unloadedView.Values)
            {
                info.WriteTo(writer);
            }

            var dataStart = _fileStream.Position;
            _fileStream.Seek(HeadCheck.Length, SeekOrigin.Begin);
            writer.Write(dataStart);
            _fileStream.Seek(dataStart, SeekOrigin.Begin);
            TagCompound tag = new()
            {
                ["."] = _items
            };
            TagIO.ToStream(tag, _fileStream);
            return true;
        }
        catch (Exception e)
        {
            if (Debug.IsStrict)
            {
                throw;
            }
            else
            {
                Debug.Error_Internal(e);
            }

            return false;
        }
    }

    private bool CheckItemLoaded()
    {
        if (_itemsLoaded && !_itemsError)
        {
            return true;
        }

        if (TryLoadItems())
        {
            CloseFileStream();
            return true;
        }
        else
        {
            return false;
        }
    }

    internal int TryPickItemByType(int type,int desireCount, out List<Item> items)
    {
        items = [];

        if (!CheckItemLoaded())
        {
            return 0;
        }

        if (!_quickView.TryGetValue(type, out var info))
        {
            return 0;
        }

        var tempCount = 0;
        foreach (var index in info)
        {
            var tempItem = _items[index];
            var pickThisCheck = Math.Min(tempItem.stack, desireCount - tempCount);
            if (pickThisCheck == tempItem.stack)
            {
                items.Add(tempItem);
                _items[index] = new Item(ItemID.None);
                info[index] = false;
                tempCount += tempItem.stack;
            }
            else
            {
                var clone = tempItem.Clone();
                clone.stack = pickThisCheck;
                tempItem.stack -= pickThisCheck;
                tempCount += pickThisCheck;
                items.Add(clone);
                break;
            }

            if (tempCount == desireCount)
            {
                break;
            }
        }

        if (tempCount == 0)
        {
            return 0;
        }

        if (info.AllStack != int.MaxValue)
        {
            info.AllStack -= tempCount;
        }
        else
        {
            var counter = 0;
            foreach (var index in info)
            {
                var diff = int.MaxValue - counter;
                if (diff > _items[index].stack)
                {
                    counter += _items[index].stack;
                }
                else
                {
                    counter = int.MaxValue;
                    break;
                }
            }

            info.AllStack = counter;
        }

        if (info.AllStack == 0)
        {
            _quickView.Remove(type);
        }

        _itemsDirty = true;
        return tempCount;
    }
}
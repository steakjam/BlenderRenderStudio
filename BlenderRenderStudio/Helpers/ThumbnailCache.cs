using System.Collections.Generic;
using Microsoft.UI.Xaml.Media;

namespace BlenderRenderStudio.Helpers;

/// <summary>
/// LRU 缩略图缓存（纯引用管理，不负责 Dispose）。
///
/// 【为什么不 Dispose】
/// 缓存淘汰的 SoftwareBitmapSource 可能仍被 GridView 的 Image 控件引用：
///   cache.Put(path201) → 淘汰 path1 的 source → ScheduleDispose
///   但 FrameThumbnail[0].Image 仍 == path1 的 source → 合成线程仍在渲染
///   → 2 秒后 Dispose → D2D 纹理释放 → 0xC000027B
///
/// 240px 缩略图约 230KB/张，200 张 ≈ 46MB，GC 终结器安全释放。
/// PreviewImage（960px）由 MainViewModel.ScheduleDispose 单独管理（安全：单一 Image 控件）。
/// </summary>
public sealed class ThumbnailCache
{
    private readonly int _maxEntries;
    private readonly LinkedList<CacheEntry> _lruList = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _map = new();
    private readonly object _lock = new();

    public ThumbnailCache(int maxEntries)
    {
        _maxEntries = maxEntries;
    }

    /// <summary>尝试从缓存获取，命中时提升到 MRU 位置</summary>
    public ImageSource? Get(string key)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                return node.Value.Source;
            }
            return null;
        }
    }

    /// <summary>放入缓存，超容量时淘汰 LRU 条目（不 Dispose，交给 GC）</summary>
    public void Put(string key, ImageSource source)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                existing.Value.Source = source;
                _lruList.Remove(existing);
                _lruList.AddFirst(existing);
                return;
            }

            // 淘汰最久未使用的条目（仅移除缓存引用，不 Dispose）
            while (_map.Count >= _maxEntries && _lruList.Last != null)
            {
                var evict = _lruList.Last!;
                _lruList.RemoveLast();
                _map.Remove(evict.Value.Key);
                evict.Value.Source = null;
            }

            var entry = new CacheEntry { Key = key, Source = source };
            var node = _lruList.AddFirst(entry);
            _map[key] = node;
        }
    }

    /// <summary>移除指定 key 的缓存条目</summary>
    public void Remove(string key)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lruList.Remove(node);
                _map.Remove(key);
                node.Value.Source = null;
            }
        }
    }

    /// <summary>清空缓存（仅解除引用，不 Dispose）</summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var node in _lruList)
                node.Source = null;
            _lruList.Clear();
            _map.Clear();
        }
    }

    public int Count
    {
        get { lock (_lock) return _map.Count; }
    }

    private class CacheEntry
    {
        public string Key { get; init; } = "";
        public ImageSource? Source { get; set; }
    }
}

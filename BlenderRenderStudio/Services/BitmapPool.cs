using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlenderRenderStudio.Helpers;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace BlenderRenderStudio.Services;

/// <summary>
/// SoftwareBitmapSource 对象池。核心安全规则：
/// 1. Source 对象从不被外部 Dispose —— 仅 Pool 在 Shutdown 时统一释放
/// 2. 复用通过 SetBitmapAsync 覆写同一 Source 的内容，不创建新对象
/// 3. 归还的 Source 延迟一帧后才进入可用队列（确保脱离渲染管线）
/// </summary>
public sealed class BitmapPool
{
    private readonly int _capacity;
    private readonly Queue<PooledBitmap> _available = new();
    private readonly Dictionary<string, PooledBitmap> _inUse = new();
    private readonly SafeDispatcher _safeDispatcher;
    private readonly object _lock = new();
    private int _totalCreated;

    public BitmapPool(int capacity, SafeDispatcher safeDispatcher)
    {
        _capacity = capacity;
        _safeDispatcher = safeDispatcher;
    }

    /// <summary>
    /// 租借一个 PooledBitmap。如果池中有空闲则复用，否则创建新对象（不超过容量）。
    /// 超出容量时淘汰最早的 in-use 项。
    /// </summary>
    public PooledBitmap? Rent(string key)
    {
        lock (_lock)
        {
            // 已有相同 key 的 in-use 项：直接返回（避免重复加载）
            if (_inUse.TryGetValue(key, out var existing))
            {
                existing.Version++;
                return existing;
            }

            PooledBitmap? bitmap = null;

            // 从可用队列取
            if (_available.Count > 0)
            {
                bitmap = _available.Dequeue();
            }
            // 未达容量上限：创建新的
            else if (_totalCreated < _capacity)
            {
                bitmap = new PooledBitmap(_totalCreated++);
            }
            // 容量已满：强制回收最老的 in-use 项
            else
            {
                // 找到最老的（version 最小的）
                PooledBitmap? oldest = null;
                string? oldestKey = null;
                foreach (var (k, v) in _inUse)
                {
                    if (oldest == null || v.Version < oldest.Version)
                    {
                        oldest = v;
                        oldestKey = k;
                    }
                }
                if (oldest != null && oldestKey != null)
                {
                    _inUse.Remove(oldestKey);
                    bitmap = oldest;
                }
            }

            if (bitmap == null) return null;

            bitmap.BoundKey = key;
            bitmap.Version++;
            bitmap.IsVisible = true;
            _inUse[key] = bitmap;
            return bitmap;
        }
    }

    /// <summary>
    /// 归还 PooledBitmap 到池中。延迟执行确保脱离渲染管线。
    /// </summary>
    public void Return(string key)
    {
        lock (_lock)
        {
            if (!_inUse.Remove(key, out var bitmap)) return;
            bitmap.IsVisible = false;
            bitmap.BoundKey = null;
            // 延迟一帧后放入可用队列（确保 WinUI 渲染管线不再引用）
            _safeDispatcher.Run(() =>
            {
                lock (_lock)
                {
                    if (!bitmap.IsVisible) // 确认仍然未被重新租借
                        _available.Enqueue(bitmap);
                }
            });
        }
    }

    /// <summary>归还所有 in-use 项</summary>
    public void ReturnAll()
    {
        lock (_lock)
        {
            foreach (var (_, bitmap) in _inUse)
            {
                bitmap.IsVisible = false;
                bitmap.BoundKey = null;
                _available.Enqueue(bitmap);
            }
            _inUse.Clear();
        }
    }

    /// <summary>内存压力时缩减池容量</summary>
    public void Trim(int keep)
    {
        lock (_lock)
        {
            while (_available.Count > keep)
            {
                var bitmap = _available.Dequeue();
                bitmap.Source.Dispose();
                _totalCreated--;
            }
        }
    }

    /// <summary>获取指定 key 当前是否在池中（已租借）</summary>
    public bool IsRented(string key)
    {
        lock (_lock) { return _inUse.ContainsKey(key); }
    }

    /// <summary>关闭池，释放所有资源</summary>
    public void Shutdown()
    {
        lock (_lock)
        {
            foreach (var (_, b) in _inUse) b.Source.Dispose();
            _inUse.Clear();
            while (_available.Count > 0) _available.Dequeue().Source.Dispose();
            _totalCreated = 0;
        }
    }

    public int ActiveCount { get { lock (_lock) return _inUse.Count; } }
    public int AvailableCount { get { lock (_lock) return _available.Count; } }
}

/// <summary>
/// 池化的 Bitmap 表面。Source 生命周期由 BitmapPool 统一管理。
/// </summary>
public sealed class PooledBitmap
{
    public int Id { get; }
    public SoftwareBitmapSource Source { get; } = new();
    public string? BoundKey { get; set; }
    public int Version { get; set; }
    public bool IsVisible { get; set; }

    public PooledBitmap(int id) { Id = id; }

    /// <summary>
    /// 安全地更新 Source 内容。在 UI 线程调用。
    /// </summary>
    public async Task SetBitmapAsync(SoftwareBitmap bitmap)
    {
        await Source.SetBitmapAsync(bitmap);
    }
}

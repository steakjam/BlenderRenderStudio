using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BlenderRenderStudio.Helpers;

namespace BlenderRenderStudio.Services;

public enum DecodePriority { High = 0, Normal = 1, Low = 2 }

public sealed class DecodeRequest
{
    public required string Key { get; init; }
    public required string FilePath { get; init; }
    public int DecodeWidth { get; init; } = 240;
    public DecodePriority Priority { get; init; } = DecodePriority.Normal;
    public int Version { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

public sealed class DecodeResult
{
    public required string Key { get; init; }
    public required int Version { get; init; }
    public DecodedImage? Decoded { get; init; }
    public bool FromDiskCache { get; init; }
}

/// <summary>
/// 后台多线程缩略图解码管线。
/// - 优先解码可见区域的帧（High 优先级）
/// - 滚出可见区域的 pending 任务通过 CancellationToken 取消
/// - 解码结果通过 Channel 送回 UI 线程消费
/// - 支持磁盘缓存加速（零 WIC 开销）
/// </summary>
public sealed class ThumbnailDecoder : IDisposable
{
    private readonly Channel<DecodeRequest> _highQueue = Channel.CreateBounded<DecodeRequest>(128);
    private readonly Channel<DecodeRequest> _normalQueue = Channel.CreateBounded<DecodeRequest>(512);
    private readonly Channel<DecodeResult> _resultChannel = Channel.CreateUnbounded<DecodeResult>();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingCts = new();
    private readonly string _diskCacheDir;
    private readonly int _workerCount;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task[] _workers;

    /// <summary>解码结果输出通道，UI 线程消费</summary>
    public ChannelReader<DecodeResult> Results => _resultChannel.Reader;

    public ThumbnailDecoder(string diskCacheDir, int? workerCount = null)
    {
        _diskCacheDir = diskCacheDir;
        _workerCount = workerCount ?? Math.Min(Environment.ProcessorCount, 8);
        _workers = new Task[_workerCount];

        for (int i = 0; i < _workerCount; i++)
            _workers[i] = Task.Run(WorkerLoop);
    }

    /// <summary>提交解码请求</summary>
    public CancellationTokenSource Enqueue(DecodeRequest request)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdownCts.Token, request.CancellationToken);

        // 替换同 key 的旧请求（取消旧的）
        if (_pendingCts.TryRemove(request.Key, out var oldCts))
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }
        _pendingCts[request.Key] = cts;

        var req = new DecodeRequest
        {
            Key = request.Key,
            FilePath = request.FilePath,
            DecodeWidth = request.DecodeWidth,
            Priority = request.Priority,
            Version = request.Version,
            CancellationToken = cts.Token,
        };

        var channel = request.Priority == DecodePriority.High ? _highQueue : _normalQueue;
        channel.Writer.TryWrite(req);
        return cts;
    }

    /// <summary>取消指定 key 的 pending 解码</summary>
    public void Cancel(string key)
    {
        if (_pendingCts.TryRemove(key, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    /// <summary>取消所有距离视口超过 threshold 的请求</summary>
    public void CancelOutOfRange(HashSet<string> visibleKeys)
    {
        foreach (var (key, cts) in _pendingCts)
        {
            if (!visibleKeys.Contains(key))
            {
                cts.Cancel();
                cts.Dispose();
                _pendingCts.TryRemove(key, out _);
            }
        }
    }

    /// <summary>取消所有 pending 请求</summary>
    public void CancelAll()
    {
        foreach (var (key, cts) in _pendingCts)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _pendingCts.Clear();
    }

    private async Task WorkerLoop()
    {
        var token = _shutdownCts.Token;
        while (!token.IsCancellationRequested)
        {
            DecodeRequest? request = null;

            // 优先从 High 队列取
            if (_highQueue.Reader.TryRead(out request) || _normalQueue.Reader.TryRead(out request))
            {
                // proceed
            }
            else
            {
                // 等待任一队列有数据
                try
                {
                    await Task.WhenAny(
                        _highQueue.Reader.WaitToReadAsync(token).AsTask(),
                        _normalQueue.Reader.WaitToReadAsync(token).AsTask());
                    continue; // 重新循环尝试读取
                }
                catch (OperationCanceledException) { break; }
            }

            if (request.CancellationToken.IsCancellationRequested)
            {
                _pendingCts.TryRemove(request.Key, out _);
                continue;
            }

            // 执行解码
            DecodedImage? decoded = null;
            bool fromCache = false;

            try
            {
                // 优先磁盘缓存
                var cacheKey = ImageHelper.GetCacheKey(request.FilePath, request.DecodeWidth);
                if (!string.IsNullOrEmpty(cacheKey) && !string.IsNullOrEmpty(_diskCacheDir))
                {
                    var cachePath = ImageHelper.GetCachePath(_diskCacheDir, cacheKey);
                    decoded = ImageHelper.LoadThumbnailCache(cachePath);
                    if (decoded != null) fromCache = true;
                }

                // 缓存未命中：WIC 解码
                if (decoded == null && !request.CancellationToken.IsCancellationRequested)
                {
                    decoded = await ImageHelper.DecodeAsync(request.FilePath, request.DecodeWidth);

                    // 写入磁盘缓存
                    if (decoded != null && !string.IsNullOrEmpty(_diskCacheDir))
                    {
                        var cacheKey2 = ImageHelper.GetCacheKey(request.FilePath, request.DecodeWidth);
                        if (!string.IsNullOrEmpty(cacheKey2))
                        {
                            var cachePath = ImageHelper.GetCachePath(_diskCacheDir, cacheKey2);
                            ImageHelper.SaveThumbnailCache(decoded, cachePath);
                        }
                    }
                }
            }
            catch { /* ignore decode failures */ }

            // 发布结果（即使已取消也发，让消费端处理 Dispose）
            _pendingCts.TryRemove(request.Key, out _);

            if (request.CancellationToken.IsCancellationRequested)
            {
                decoded?.Dispose();
                continue;
            }

            await _resultChannel.Writer.WriteAsync(new DecodeResult
            {
                Key = request.Key,
                Version = request.Version,
                Decoded = decoded,
                FromDiskCache = fromCache,
            }, token);
        }
    }

    public void Dispose()
    {
        _shutdownCts.Cancel();
        _highQueue.Writer.TryComplete();
        _normalQueue.Writer.TryComplete();
        _resultChannel.Writer.TryComplete();

        foreach (var (_, cts) in _pendingCts)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _pendingCts.Clear();
        _shutdownCts.Dispose();
    }
}

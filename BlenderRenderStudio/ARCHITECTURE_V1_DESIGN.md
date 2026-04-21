# Blender Render Studio v1.0 — 架构重构技术分析与设计方案

**撰写日期**：2026-04-20
**文档类型**：技术分析 + 架构设计
**范围**：图像预览系统重写 + 项目管理系统 + UI 交互重构

---

## 一、当前闪退根因分析

### 1.1 已知崩溃路径

| 场景 | 根因 | 技术细节 |
|------|------|---------|
| 单帧/网格快速切换 | SoftwareBitmapSource 生命周期竞争 | 切换视图时旧 Source 被 Dispose，但 UI 绑定仍持有引用，D2D 纹理已释放 → 访问违规 |
| 渲染中滚动网格 | 并发解码与 UI 线程 Source 创建竞争 | FrameSaved 触发的 UpdateGridThumbnailAsync 与用户滚动触发的 LoadMissingThumbnailsAsync 同时操作同一 FrameThumbnail.Image |
| 大量帧快速滚动 | 内存尖峰 + GC 压力 | 每帧创建新 SoftwareBitmapSource（~4MB/帧@240px），200帧 ≈ 800MB 瞬时内存，触发 OOM 或 GC 长暂停 |
| 预览倍率切换 | 旧 Source Dispose 时序 | PreviewImage setter Dispose 旧对象，但 Image 控件可能正在该帧的渲染周期中使用旧纹理 |

### 1.2 根本问题

当前架构**对每张图片创建独立的 SoftwareBitmapSource 对象**，这是一个 COM 包装的 D2D 表面。WinUI 3 的渲染管线在另一个线程消费这些表面，**Dispose 时序与渲染管线不同步**是闪退的根本原因。这不是能通过加锁或防抖解决的——需要从架构层面重新设计图像生命周期。

---

## 二、业界方案调研

### 2.1 Windows 文件资源管理器 (Explorer)

**核心架构**：Shell Thumbnail Cache + COM IThumbnailCache

| 层级 | 机制 | 特点 |
|------|------|------|
| L1 | 进程内 LRU（弱引用） | ~100 项，GC 可回收 |
| L2 | thumbcache_*.db（磁盘） | 系统级持久化，跨进程共享 |
| L3 | 按需 WIC 解码 | 仅在 L1+L2 未命中时触发 |

**关键设计**：
- **虚拟化容器回收**：滚出视口的 Item 回收容器，不销毁 Bitmap 但从可视树移除（解除绑定）
- **取消语义**：滚出可见区域的 pending 解码任务立即取消（CancellationToken）
- **Shell 协议**：IExtractImage / IThumbnailProvider 在独立进程(thumbcache.dll) 中解码，崩溃不影响主进程

### 2.2 Windows 照片 (Microsoft Photos)

**核心架构**：DirectX 图像管线 + 虚拟化 + 对象池

| 技术 | 作用 |
|------|------|
| Direct2D Image Effects | GPU 加速解码/缩放/色彩管理 |
| BitmapPool | 预分配固定数量的 D2D 表面，循环复用 |
| Progressive Decode | 先显示低分辨率缩略图，后台替换为高清版 |
| Prefetch Window | 预加载当前视口前后各 N 屏的缩略图 |
| Memory Budget | 总缓存上限（如 512MB），超限淘汰最远项 |

**关键设计**：
- **永远不 Dispose 正在显示的表面**——通过对象池轮转，旧表面标记为"可复用"而非"已销毁"
- 只在对象池耗尽时才真正释放最老的表面

### 2.3 Files App（第三方 WinUI 3 文件管理器）

**核心架构**：ItemsRepeater + CancellationToken + 双级缓存

| 设计模式 | 实现方式 |
|------|------|
| 虚拟化 | ItemsRepeater（非 GridView），自定义 Layout，仅实例化可见项 |
| 取消加载 | 每个缩略图任务绑定 CancellationTokenSource，滚出视口时 Cancel |
| 渐进加载 | 先显示文件图标占位符 → 后台解码完成后替换为缩略图 |
| 内存回收 | ScrollViewer.ViewChanged 事件中回收距离视口 >2 屏的缩略图 |
| 线程安全 | 解码在 ThreadPool，仅 `SetBitmapAsync` 在 UI 线程，且通过 version token 防止过期写入 |

**关键崩溃防护**：
```
// Files app 的核心模式
if (cancellationToken.IsCancellationRequested) return;
if (item.LoadVersion != currentVersion) return; // 容器已被回收给其他文件
await dispatcherQueue.EnqueueAsync(() => item.Thumbnail = source);
```

### 2.4 最佳实践总结

| 原则 | 说明 |
|------|------|
| **永不 Dispose 活跃表面** | 用对象池轮转代替销毁，等表面确认脱离渲染管线后才回收 |
| **取消优先于完成** | 滚出视口的解码任务应被取消，而非完成后丢弃结果 |
| **虚拟化 + 容器回收** | ItemsRepeater > GridView，容器脱离可视树时清空绑定 |
| **内存预算制** | 设定总缓存上限（如 300MB），超限时从最远处开始释放 |
| **渐进式加载** | 占位符 → 低分辨率 → 高分辨率，用户感知延迟降至 0 |
| **独立解码进程/线程** | 解码崩溃不应拖垮 UI 进程 |

---

## 三、新架构设计

### 3.1 系统结构总览

```
┌─────────────────────────────────────────────────────────┐
│                    Blender Render Studio                  │
├──────────────┬──────────────┬───────────────────────────┤
│  项目管理器   │   渲染引擎    │      图像预览子系统        │
│ ProjectMgr   │ RenderQueue  │    ImagePipeline          │
├──────────────┼──────────────┼───────────────────────────┤
│ • 项目CRUD   │ • 多项目队列  │ • BitmapPool (对象池)     │
│ • 项目配置   │ • 优先级调度  │ • ThumbnailDecoder (后台)  │
│ • 独立缓存   │ • 断点续渲   │ • VirtualGridLayout       │
│ • 最近项目   │ • 并行控制   │ • ProgressiveLoader       │
└──────────────┴──────────────┴───────────────────────────┘
```

### 3.2 图像预览子系统重构

#### 3.2.1 BitmapPool（对象池，解决闪退核心）

```csharp
/// <summary>
/// 固定容量的 SoftwareBitmapSource 对象池。
/// 表面从不被外部 Dispose —— 仅池管理器在确认安全时才回收。
/// </summary>
public sealed class BitmapPool
{
    private readonly int _capacity;           // 池容量（如 64 个 240px 表面）
    private readonly Queue<PooledBitmap> _available;
    private readonly Dictionary<string, PooledBitmap> _inUse;

    // 租借：返回一个可复用表面（或创建新的直到达到容量）
    public PooledBitmap Rent(string key);

    // 归还：标记表面可复用（不 Dispose！），延迟一帧后才真正放入可用队列
    public void Return(PooledBitmap bitmap);

    // 仅在内存压力时强制收缩
    public void Trim(int targetCount);
}

public sealed class PooledBitmap
{
    public SoftwareBitmapSource Source { get; }
    public string BoundKey { get; set; }  // 当前绑定的文件路径
    public int Version { get; set; }       // 防止过期写入
    public bool IsVisible { get; set; }    // 当前是否在可视树中
}
```

**核心规则**：
- UI 绑定的 Image.Source 永远指向池中的 PooledBitmap.Source
- 切换图片时：不销毁旧 Source，而是通过 `SetBitmapAsync` 用新数据覆写同一个 Source 对象
- 仅当容器滚出视口 + 2帧后，才将 PooledBitmap 归还到池中

#### 3.2.2 ThumbnailDecoder（后台解码管线）

```csharp
/// <summary>
/// 后台解码调度器。特点：
/// 1. 优先解码可见区域的帧
/// 2. 滚出可见区域的 pending 任务自动取消
/// 3. 解码结果通过 Channel 送回 UI 线程
/// </summary>
public sealed class ThumbnailDecoder
{
    private readonly Channel<DecodeRequest> _requestQueue;
    private readonly Channel<DecodeResult> _resultQueue;

    // 优先级：Visible > Prefetch > Background
    public void Enqueue(DecodeRequest request, DecodePriority priority);

    // 取消所有与指定 key 匹配的 pending 请求
    public void Cancel(string key);

    // 取消所有距离视口超过 threshold 的请求
    public void CancelOutOfRange(int visibleStart, int visibleEnd, int threshold);
}
```

#### 3.2.3 VirtualGridLayout（虚拟化网格）

替代当前的 `GridView + ObservableCollection<FrameThumbnail>` 方案：

```
当前方案的问题：
  - GridView 为所有 200+ 项创建 GridViewItem 容器
  - 每个容器持有一个 SoftwareBitmapSource 引用
  - 切换视图时所有容器同时存在，内存无法回收

新方案：ItemsRepeater + 自定义 UniformGridLayout
  - 仅实例化可见项 + 前后缓冲各 1 屏
  - 容器回收时清空 Image.Source 并归还 PooledBitmap
  - 容器复用时从缓存/磁盘重新加载（渐进式）
```

#### 3.2.4 预览切换安全机制

```
单帧 ↔ 网格切换时：
1. 取消所有 pending 解码
2. 单帧预览使用独立的 PooledBitmap（不与网格共享）
3. 切换动画期间（200ms）冻结加载——动画结束后再开始新视图的加载
4. 使用 x:Phase 延迟加载：Phase 0 = 占位符，Phase 1 = 缩略图
```

### 3.3 项目管理系统

#### 3.3.1 数据模型

```csharp
public class RenderProject
{
    public string Id { get; init; }          // GUID
    public string Name { get; set; }
    public string BlendFilePath { get; set; }
    public string OutputDirectory { get; set; }
    public RenderSettings Settings { get; set; }  // 帧范围/批大小/内存阈值...
    public RenderProgress Progress { get; set; }  // 已完成帧/中断位置
    public DateTime CreatedAt { get; init; }
    public DateTime LastRenderAt { get; set; }

    // 每个项目独立的缓存目录
    public string CacheDirectory => Path.Combine(
        AppDataDir, "Cache", Id);
}
```

#### 3.3.2 页面结构

```
App
├── ProjectListPage（首页 - 项目管理）
│   ├── 新建项目按钮
│   ├── 最近项目列表（卡片式 GridView）
│   │   └── 项目卡片：缩略图 + 名称 + 进度 + 最近渲染时间
│   └── 批量渲染队列入口
├── ProjectWorkspacePage（项目工作区 - 当前的主界面）
│   ├── 渲染控制面板
│   ├── 图像预览区（重构后）
│   └── 帧时间轴
├── BatchQueuePage（批量渲染队列）
│   ├── 队列列表（拖拽排序）
│   ├── 全局进度
│   └── 队列控制（开始/暂停/跳过当前）
└── SettingsPage（全局设置）
```

#### 3.3.3 批量渲染队列

```csharp
public class RenderQueueService
{
    private readonly Queue<RenderJob> _queue = new();
    private RenderJob? _currentJob;

    public void Enqueue(RenderProject project, RenderPriority priority);
    public void Reorder(int fromIndex, int toIndex);  // 拖拽排序
    public void Skip();      // 跳过当前项目，开始下一个
    public void PauseAll();  // 暂停队列（完成当前帧后暂停）

    // 队列执行逻辑：
    // 1. 取出队首项目
    // 2. 创建 RenderEngine 实例
    // 3. 执行渲染（支持断点续渲）
    // 4. 完成后自动开始下一个
    // 5. 失败时：标记错误 + 跳过 + 通知用户
}
```

### 3.4 UI 交互重构

#### 3.4.1 导航结构

```
NavigationView (Left, Compact)
├── 项目 (首页) — ProjectListPage
├── 队列 — BatchQueuePage
└── [Footer] 设置 — SettingsPage

进入项目后：NavigationView 切换为项目工作区模式
  → 返回箭头回到项目列表
```

#### 3.4.2 原生动画能力运用

| 场景 | 动画 | WinUI 3 API |
|------|------|-------------|
| 页面切换 | 连贯动画（Connected Animation） | ConnectedAnimationService |
| 项目卡片出现 | 交错入场 | ItemsRepeater + LinedFlowLayout + Stagger |
| 单帧 ↔ 网格切换 | 缩放过渡 | ThemeTransition + ScaleTransition |
| 缩略图加载 | 淡入 | OpacityTransition (0→1, 150ms) |
| 预览图切换 | 交叉淡入淡出 | CrossFadeNavigationTransitionInfo |
| 侧边栏展开/折叠 | 弹簧动画 | SpringVector3NaturalMotionAnimation |
| 拖拽排序 | 位移跟随 | DragItemsStarting + RepositionThemeTransition |

#### 3.4.3 具体动画实现示例

**项目卡片 → 工作区（Connected Animation）**：
```csharp
// 从项目列表进入项目
void ProjectCard_Click(object sender, RoutedEventArgs e)
{
    var animation = ConnectedAnimationService.GetForCurrentView()
        .PrepareToAnimate("projectToWorkspace", ProjectThumbnail);
    Frame.Navigate(typeof(ProjectWorkspacePage), project);
}

// 工作区页面接收动画
protected override void OnNavigatedTo(NavigationEventArgs e)
{
    var animation = ConnectedAnimationService.GetForCurrentView()
        .GetAnimation("projectToWorkspace");
    animation?.TryStart(WorkspacePreviewImage);
}
```

**缩略图淡入（隐式动画）**：
```xml
<Image Source="{x:Bind Thumbnail, Mode=OneWay}">
    <Image.Transitions>
        <TransitionCollection>
            <OpacityTransition Duration="0:0:0.15" />
        </TransitionCollection>
    </Image.Transitions>
</Image>
```

---

## 四、实施路线图

### Phase 1：图像管线重构（解决闪退）

| 步骤 | 内容 | 预期效果 |
|------|------|---------|
| 1.1 | 实现 BitmapPool 对象池 | 消除 Dispose 竞争闪退 |
| 1.2 | 替换 GridView 为 ItemsRepeater + UniformGridLayout | 真正虚拟化，内存从 800MB → <100MB |
| 1.3 | 实现 ThumbnailDecoder 后台解码管线 | 取消语义，滚动不卡顿 |
| 1.4 | 实现渐进式加载（Phase 0/1） | 即时响应感 |
| 1.5 | 单帧预览使用独立 PooledBitmap | 切换安全 |

### Phase 2：项目管理系统

| 步骤 | 内容 |
|------|------|
| 2.1 | RenderProject 数据模型 + JSON 持久化 |
| 2.2 | ProjectListPage UI（卡片列表 + 新建/删除/重命名） |
| 2.3 | 独立项目缓存目录隔离 |
| 2.4 | 项目工作区页面（从现有 HomePage 重构） |

### Phase 3：批量渲染队列

| 步骤 | 内容 |
|------|------|
| 3.1 | RenderQueueService 队列调度逻辑 |
| 3.2 | BatchQueuePage UI（列表 + 拖拽排序 + 控制按钮） |
| 3.3 | 队列持久化（断电恢复） |
| 3.4 | 通知机制（队列完成/项目失败时 Windows Toast） |

### Phase 4：UI 动画精修

| 步骤 | 内容 |
|------|------|
| 4.1 | Connected Animation（项目卡片 → 工作区） |
| 4.2 | 缩略图淡入 + 交错入场 |
| 4.3 | 视图切换 ScaleTransition |
| 4.4 | 时间轴拖拽弹簧动画 |
| 4.5 | Mica/Acrylic 材质精调 |

---

## 五、技术选型建议

| 领域 | 当前方案 | 建议方案 | 理由 |
|------|---------|---------|------|
| 网格虚拟化 | GridView | ItemsRepeater + UniformGridLayout | 真正的容器回收，内存可控 |
| 图像表面 | SoftwareBitmapSource（每张独立） | BitmapPool + WriteableBitmap | 池化复用，消除 Dispose 竞争 |
| 缩略图缓存 | 自定义 LRU + 磁盘 .raw | 保留磁盘缓存 + 弱引用内存缓存 | GC 可自动回收内存级缓存 |
| 后台解码 | Task.Run + SemaphoreSlim | System.Threading.Channels 管线 | 优先级队列 + 取消语义 |
| 页面导航 | Grid Visibility 切换 | Frame 导航 + NavigationTransitionInfo | 原生过渡动画 + 内存隔离 |
| 配置持久化 | 单文件 JSON | 项目级 JSON + 全局 JSON 分离 | 项目独立性 |
| 通知 | 无 | Microsoft.Windows.AppNotifications | 队列完成/错误通知 |

---

## 六、关键风险与对策

| 风险 | 对策 |
|------|------|
| ItemsRepeater 不支持选中状态 | 自定义 SelectionModel + 视觉状态管理 |
| BitmapPool 容量不足 | 动态扩缩 + 内存压力回调 (MemoryManager.AppMemoryUsageLimitChanging) |
| Connected Animation 源控件被回收 | 使用 PrepareToAnimate 前确保控件在可视树中 |
| WriteableBitmap 不支持 SetBitmapAsync | 改用 CanvasBitmap (Win2D) 或保留 SoftwareBitmapSource + 延迟回收 |
| 多项目并行渲染 Blender 进程冲突 | 队列串行执行，一次只运行一个 Blender 实例 |

---

*本文档为架构重构的技术预研与设计方案，供评审确认后分阶段实施。*

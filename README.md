# Blender Render Studio

一款基于 **WinUI 3** 的 Blender 渲染管理工具，为 Blender 用户提供可视化的批量渲染、帧预览与项目管理体验。

---

## 核心功能

### 项目管理
- 多项目独立管理，每个项目拥有独立的缓存目录与配置
- 项目导入/导出：自包含归档格式，支持导入配置向导与文件提取
- 项目卡片式一览，快速切换工作区

### 渲染控制
- 队列驱动的渲染流程，支持多项目排队、优先级调度
- 断点续渲：中断后自动从上次位置恢复
- PID 进程跟踪：Blender 闪退后自动检测并恢复监控
- 实时渲染进度与预计剩余时间显示

### 帧预览与时间轴
- 帧时间轴 Canvas：刻度尺 + 播放头 + 空格键播放
- 网格缩略图虚拟化显示，LRU 缓存策略保证流畅滚动
- 全窗口预览模式（ESC 退出）
- 黑帧自动检测

### 局域网分布式渲染（开发中）
- 设备管理页面：扫描局域网内可用渲染节点
- 计划支持帧拆分、任务分配、进度聚合与结果回传

### 全局设置
- Blender 路径自动检测与全局同步
- 输出路径拆分：独立配置输出目录与文件名前缀
- 渲染参数面板：批大小、内存阈值等可调

---

## 技术栈

| 层面 | 技术 |
|------|------|
| 框架 | WinUI 3 (Windows App SDK 1.8) |
| 运行时 | .NET 8 |
| 最低系统 | Windows 10 1809 (Build 17763) |
| 语言 | C# 12, XAML |
| 架构 | MVVM (ViewModel + Service 层) |
| 图像管线 | BitmapPool 对象池 + ThumbnailDecoder 后台解码 |
| 平台 | x86 / x64 / ARM64 |

---

## 项目结构

```
BlenderRenderStudio/
├── App.xaml(.cs)              # 应用入口
├── MainWindow.xaml(.cs)       # 主窗口 + 导航框架
├── Pages/                     # 页面
│   ├── ProjectListPage        # 项目列表（首页）
│   ├── ProjectWorkspacePage   # 项目工作区（渲染 + 预览）
│   ├── BatchQueuePage         # 批量渲染队列
│   ├── DeviceManagementPage   # 局域网设备管理
│   └── SettingsPage           # 全局设置
├── ViewModels/                # 视图模型
│   └── MainViewModel.cs
├── Services/                  # 核心服务
│   ├── RenderEngine.cs        # Blender 进程调度与渲染执行
│   ├── RenderQueueService.cs  # 渲染队列管理
│   ├── RenderRecovery.cs      # 闪退恢复与断点续渲
│   ├── ProjectService.cs      # 项目 CRUD 与持久化
│   ├── BitmapPool.cs          # 图像对象池（防闪退核心）
│   ├── ThumbnailDecoder.cs    # 后台缩略图解码管线
│   ├── FrameAnalyzer.cs       # 帧分析（黑帧检测等）
│   ├── MemoryMonitor.cs       # 内存监控与压力回调
│   ├── BlenderDetector.cs     # Blender 安装路径检测
│   ├── NetworkDiscoveryService.cs  # 局域网节点发现
│   ├── SettingsService.cs     # 配置读写
│   └── StartupService.cs      # 启动初始化
├── Models/                    # 数据模型
├── Converters/                # XAML 值转换器
├── Helpers/                   # 工具类
└── Properties/                # 程序集属性
```

---

## 快速开始

### 环境要求
- Windows 10 1809 或更高版本
- .NET 8 SDK
- Windows App SDK 1.8+
- Blender（需安装并可被检测到路径）

### 构建与运行

```bash
dotnet restore
dotnet build
dotnet run
```

---

## 已解决的关键技术挑战

- **SoftwareBitmapSource 闪退**：通过 BitmapPool 对象池替代逐张创建/销毁，消除 D2D 纹理与 UI 渲染管线的生命周期竞争
- **大量帧内存溢出**：网格缩略图改用 BitmapImage+URI 方案，由 XAML 内部管理 D2D 纹理池
- **WinUI 3 ScheduleDispose**：预览图使用 SoftwareBitmapSource + 延迟释放，确保渲染管线完成后再回收
- **多 GPU 环境兼容**：运行时动态检测 WARP/硬件适配器，自动降级

---

## 开发进度

### 已完成
- WinUI 3 完整重写（预览/黑帧检测/参数面板/配置持久化）
- 帧时间轴 Canvas + 播放控制
- 队列驱动渲染 UX + 进度追踪
- 断点续渲 + Blender 闪退自动恢复
- 项目导入/导出归档
- 局域网设备发现（Phase 1）
- 网格缩略图架构重写 + 性能深度优化
- 平级导航重构（项目列表/工作区/队列/设置独立导航）

### 进行中
- 分布式任务协调器（帧拆分/分配/进度聚合）
- 队列页全局进度条
- ItemsRepeater 虚拟化 + UI 动画精修

---

## 许可证

私有项目，保留所有权利。

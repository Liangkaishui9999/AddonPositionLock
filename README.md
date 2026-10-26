# AddonPositionLock

OmniToolbox 在线模块：把 FF14 的游戏原生窗口（addon）钉在固定位置，避免被其他插件窗口挤动或遮挡。

## 功能

- **自动接管**：遍历 `RaptureAtkUnitManager` 的全部已加载窗口，第一次出现时记下游戏摆放的位置，此后每帧钉住
- **右键锁定**：在任意游戏窗口上点右键，菜单里多出「锁定窗口 / 解除窗口锁定」
- **跟随分辨率**：以 1920 宽为基准存坐标，换 2K / 4K 屏自动等比缩放
- **排除规则**：HUD 节点（`_` 前缀）、动态弹窗（Tooltip / ContextMenu / ItemDetail 等）默认不接管

## 在线安装（推荐）

OmniToolbox → 插件设置 → **在线模块**，把下面任一个地址填进去：

```
https://raw.githubusercontent.com/Liangkaishui9999/AddonPositionLock/main/TreeHouseModules.json
```

或者直接填仓库地址（宿主会自动找根目录的 `TreeHouseModules.json`）：

```
https://github.com/Liangkaishui9999/AddonPositionLock
```

首次安装后默认关闭，在「树树妙妙屋 → 在线」里手动启用「窗口位置锁定」。

最低宿主版本：**1.1.8.1**

## 手动安装（本地模块）

1. 下载 [`Modules/AddonPositionLock.cs`](Modules/AddonPositionLock.cs)
2. 放到任意固定目录
3. OmniToolbox 设置 → 本地模块 → 添加，选中这个 `.cs` 文件
4. 重载 OmniToolbox，在模块列表里启用

## 用法

| 操作 | 效果 |
| --- | --- |
| 窗口上右键 → 锁定窗口 | 以窗口当前位置为基准钉住，拖动时每帧拉回 |
| 窗口上右键 → 解除窗口锁定 | 放开，拖动后自动记住新位置 |
| 设置面板 → 全部解除窗口锁定 | 一次性放开所有窗口 |
| 设置面板 → 清空全部记录 | 清空后每个窗口下次打开时重新按当时的游戏位置记录 |

## 实现要点

```
ModuleBase（OmniToolbox.Common 1.1.8.1）
├── OnEnable     挂 Framework.Update + UiBuilder.Draw + AddonLifecycle + ContextMenu
├── OnDisable    全部反挂并落盘
└── DrawSettings  启用锁定 / 跟随分辨率 / 全部解除 / 排除规则
```

**窗口枚举**

```csharp
var manager = RaptureAtkUnitManager.Instance();
var entries = manager->AllLoadedUnitsList.Entries;   // Span<Pointer<AtkUnitBase>>
for (var i = 0; i < entries.Length; i++)
{
    var addon = entries[i].Value;
    if (ShouldTrack(addon, out var name)) ApplyPin(addon, name);
}
```

**位置钉死**

```csharp
addon->SetPosition((short)targetX, (short)targetY);
```

坐标存的是 1920 基准值，运行时乘 `DisplaySize.X / 1920`。

**右键目标定位**

```csharp
var cursorNode = manager->CursorFocusNode;
var hit = manager->GetAddonByNode(cursorNode);   // 引擎的光标命中节点，最准
```

回退方案是「包含鼠标点的窗口里取面积最小的」，因为弹窗总比容器小。

**右键菜单**

```csharp
DService.Instance().ContextMenu.OnMenuOpened += OnContextMenuOpened;
args.AddMenuItem(new MenuItem { Name = "锁定窗口", OnClicked = _ => ... });
```

## 兼容性

| 项 | 版本 |
| --- | --- |
| OmniToolbox | 1.1.8.1 |
| Dalamud API | 15 |
| .NET | 10 |
| 模块接口 | `ModuleBase`（`OnEnable` / `OnDisable` / `DrawSettings`） |

注意：`OmniToolbox.Common` 仓库主线已经改成 `OmniModuleBase` + `OnStart(FeatureLifetime)` 的新接口，和 1.1.8.1 实际安装出来的 `ModuleBase` + `OnEnable` **不兼容**。本模块是照着 1.1.8.1 的实际 DLL 写的。

## 仓库结构

```
AddonPositionLock/
├── TreeHouseModules.json          在线模块清单（宿主入口）
├── Modules/
│   └── AddonPositionLock.cs       模块源码（30,995 B）
├── README.md
└── LICENSE
```

修改 `Modules/AddonPositionLock.cs` 之后，必须同步更新 `TreeHouseModules.json` 里的 `Sha256`（大写、无连字符），否则宿主会拒绝加载：

```powershell
(Get-FileHash Modules\AddonPositionLock.cs -Algorithm SHA256).Hash
```

## 已知限制

- 位置锁定依赖每帧 `SetPosition`，窗口如果由游戏内部逻辑强制定位（少数模态窗），可能仍会被拉回
- `DrawOrderIndex` 不能可靠反映窗口前后关系，右键目标定位优先用 `CursorFocusNode`
- 动态弹窗必须留在排除名单里，否则会被钉在第一次出现的位置

## 许可

MIT，见 [LICENSE](LICENSE)。

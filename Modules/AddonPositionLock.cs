using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.ContextMenu;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.UI;
using TreeTools.Lifecycle;

namespace OmniToolbox.TreeLocal;

/// <summary>
/// 窗口位置锁定（在线模块）。
/// 遍历游戏内全部原生窗口，第一次出现时记下游戏给出的位置并钉住，
/// 之后该窗口每次打开都回到这个位置；在窗口上点右键可锁定 / 解除锁定。
/// 目标宿主：OmniToolbox 1.1.8.9（OmniModuleBase / OnStart / OnStop）。
/// </summary>
[ModuleApiVersion(1)]
public sealed unsafe class AddonPositionLock : OmniModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Category = ModuleCategory.Interface,
        Author = "Liangkaishui9999"
    };

    public override ModuleLocalization Localization { get; } = ModuleLocalization.Create(
        new Dictionary<string, string>
        {
            ["Title"] = "窗口位置锁定",
            ["Description"] = "自动接管游戏内所有原生窗口：首次出现时记住位置并锁定，之后不再被拖动或被其他插件挤动。在窗口上点右键可锁定或解除锁定。"
        });

    public override bool HasSettings => true;

    /// <summary>以 1920 宽为基准的参考分辨率。</summary>
    private const float BaselineWidth = 1920f;

    /// <summary>窗口刚出现时游戏可能还在摆位置，这段时间内跟随而不是强钉。</summary>
    private const double SettleSeconds = 0.6;

    private readonly PinState state = new();
    private readonly HashSet<string> excludeNames = new(StringComparer.Ordinal);
    private char[] excludePrefixes = { '_' };

    private string configPath = string.Empty;

    private string Diagnostics
    {
        get => diagnostics;
        set => diagnostics = value;
    }
    private bool dirty;
    private double lastSaveTime = -100;
    private string diagnostics = string.Empty;
    private int pinCount;
    private string pinDetail = string.Empty;

    protected override void OnStart(FeatureLifetime activation)
    {
        var service = DService.Instance();
        configPath = Path.Combine(service.PI.GetPluginConfigDirectory(), "AddonPositionLock.json");
        Load();
        RebuildExclusions();

        service.Framework.Update += OnFrameworkUpdate;
        service.UIBuilder.Draw += OnDraw;
        service.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, OnAddonPostUpdate);
        service.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, OnAddonPostUpdate);
        service.ContextMenu.OnMenuOpened += OnContextMenuOpened;

        // 本次启用的资源全部登记到 activation，停用时由宿主统一回收
        activation.OnStopping(() =>
        {
            var s = DService.Instance();
            s.Framework.Update -= OnFrameworkUpdate;
            s.UIBuilder.Draw -= OnDraw;
            s.AddonLifecycle.UnregisterListener(AddonEvent.PostUpdate, OnAddonPostUpdate);
            s.AddonLifecycle.UnregisterListener(AddonEvent.PostRefresh, OnAddonPostUpdate);
            s.ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        });
    }

    protected override void OnStop()
    {
        if (dirty)
        {
            Save();
        }
    }

    #region 枚举与过滤

    private bool ShouldTrack(AtkUnitBase* addon, out string name)
    {
        name = string.Empty;

        if (addon == null || !addon->IsVisible)
        {
            return false;
        }

        // 没有窗口边框的对象（HUD 元素、提示气泡）不是可拖动窗口
        if (addon->WindowNode == null)
        {
            return false;
        }

        var addonName = addon->NameString;
        if (string.IsNullOrEmpty(addonName))
        {
            return false;
        }

        // HUD 节点以下划线开头
        foreach (var prefix in excludePrefixes)
        {
            if (addonName[0] == prefix)
            {
                return false;
            }
        }

        if (excludeNames.Contains(addonName))
        {
            return false;
        }

        name = addonName;
        return true;
    }
    /// <summary>游戏原生窗口右键菜单里追加「锁定 / 解除锁定」。</summary>
    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        try
        {
            var target = FindTopmostAtMouse();
            if (target is null)
            {
                return;
            }

            var (pointer, name) = target.Value;
            if (!state.Windows.TryGetValue(name, out var entry))
            {
                return;
            }

            _ = pointer;

            args.AddMenuItem(new MenuItem
            {
                Name = entry.Locked ? "解除窗口锁定" : "锁定窗口",
                OnClicked = _ =>
                {
                    if (entry.Locked)
                    {
                        entry.Locked = false;
                        Save();
                        DService.Instance().Chat.Print($"[窗口位置锁定] {name} 已解锁，可以自由拖动");
                        return;
                    }

                    // 点击时重新取指针，避免用右键那一刻缓存的地址
                    var manager2 = RaptureAtkUnitManager.Instance();
                    var live = manager2 == null ? null : manager2->GetAddonByName(name);
                    if (live == null || live->GetScaledWidth(true) <= 1f)
                    {
                        DService.Instance().Chat.PrintError($"[窗口位置锁定] {name} 当前不可用");
                        return;
                    }

                    var k = ScreenScale();
                    entry.X = ToBaseline(live->X, k);
                    entry.Y = ToBaseline(live->Y, k);
                    entry.Locked = true;
                    entry.FirstSeen = -100;
                    Save();

                    DService.Instance().Chat.Print(
                        $"[窗口位置锁定] {name} 已锁定在 ({live->X},{live->Y})");
                },
                IsEnabled = true,
                Priority = 0
            });
        }
        catch
        {
        }
    }

    /// <summary>鼠标位置处、层级最高的可接管窗口。</summary>
    private (nint Pointer, string Name)? FindTopmostAtMouse()
    {
        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null)
        {
            return null;
        }

        // 1) 游戏自己的光标命中节点，最准
        var cursorNode = manager->CursorFocusNode;
        if (cursorNode != null)
        {
            var hit = manager->GetAddonByNode(cursorNode);
            if (hit != null && ShouldTrack(hit, out var hitName))
            {
                return ((nint)hit, hitName);
            }
        }

        // 2) 回退：包含鼠标点的窗口里，面积最小的通常是最上面的弹窗
        var mouse = ImGui.GetIO().MousePos;
        var entries = manager->AllLoadedUnitsList.Entries;
        nint best = 0;
        var bestName = string.Empty;
        var bestArea = float.MaxValue;
        var bestOrder = int.MinValue;

        for (var i = 0; i < entries.Length; i++)
        {
            var addon = entries[i].Value;
            if (!ShouldTrack(addon, out var name))
            {
                continue;
            }

            var x = (float)addon->X;
            var y = (float)addon->Y;
            var width = addon->GetScaledWidth(true);
            var height = addon->GetScaledHeight(true);
            if (width <= 1f || height <= 1f)
            {
                continue;
            }

            if (mouse.X < x || mouse.X > x + width || mouse.Y < y || mouse.Y > y + height)
            {
                continue;
            }

            var area = width * height;
            var order = (int)addon->DrawOrderIndex;

            if (area < bestArea - 1f || (Math.Abs(area - bestArea) <= 1f && order > bestOrder))
            {
                best = (nint)addon;
                bestName = name;
                bestArea = area;
                bestOrder = order;
            }
        }

        return best == 0 ? null : (best, bestName);
    }
    private void DrawLockButton(string name, WindowState entry, int index, Vector2 position, float size)
    {
        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(size, size), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, new Vector2(1f, 1f));

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBringToFrontOnFocus;

        ImGui.Begin($"##addonLock{index}", flags);
        ImGui.SetCursorScreenPos(position);
        ImGui.InvisibleButton("##hit", new Vector2(size, size));

        var hovered = ImGui.IsItemHovered();
        var held = ImGui.IsItemActive();

        if (hovered || held || state.IdleAlpha > 0.01f)
        {
            var alpha = hovered || held ? 1f : state.IdleAlpha;
            DrawLockIcon(ImGui.GetWindowDrawList(), position, size, entry.Locked, alpha, hovered || held);
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            entry.Locked = !entry.Locked;
            entry.FirstSeen = -100;
            Save();
        }

        if (hovered)
        {
            ImGui.SetTooltip(entry.Locked
                ? $"{name}\n已锁定：位置固定\n左键点击 = 解锁（可自由拖动）"
                : $"{name}\n未锁定：移动后会记住新位置\n左键点击 = 锁定");
        }

        ImGui.End();
        ImGui.PopStyleVar(3);
    }

    private static void DrawLockIcon(ImDrawListPtr drawList, Vector2 origin, float size, bool locked, float alpha, bool highlight)
    {
        var background = ImGui.GetColorU32(new Vector4(0.05f, 0.06f, 0.08f, (highlight ? 0.9f : 0.55f) * alpha));
        var border = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.22f * alpha));
        drawList.AddRectFilled(origin, origin + new Vector2(size, size), background, 4f);
        drawList.AddRect(origin, origin + new Vector2(size, size), border, 4f, ImDrawFlags.None, 1f);

        var color = ImGui.GetColorU32(locked
            ? new Vector4(1.00f, 0.56f, 0.24f, alpha)
            : new Vector4(0.55f, 0.66f, 0.74f, alpha));

        var unit = size / 24f;
        var centerX = origin.X + size * 0.5f;

        drawList.AddRectFilled(
            new Vector2(centerX - 6f * unit, origin.Y + 12f * unit),
            new Vector2(centerX + 6f * unit, origin.Y + 20f * unit),
            color, 2f * unit);

        var radius = 4.6f * unit;
        var arcCenter = new Vector2(centerX + (locked ? 0f : 1.6f * unit), origin.Y + 12f * unit);
        var startAngle = locked ? MathF.PI : MathF.PI * 1.06f;
        var endAngle = locked ? MathF.PI * 2f : MathF.PI * 1.94f;

        drawList.PathArcTo(arcCenter, radius, startAngle, endAngle, 20);
        drawList.PathStroke(color, ImDrawFlags.None, 2f * unit);

        drawList.AddCircleFilled(
            new Vector2(centerX, origin.Y + 16f * unit),
            1.5f * unit,
            ImGui.GetColorU32(new Vector4(0.02f, 0.03f, 0.04f, 0.9f * alpha)),
            8);
    }

    private void RebuildExclusions()
    {
        excludeNames.Clear();
        foreach (var name in SplitList(state.ExcludeNames))
            excludeNames.Add(name);

        excludePrefixes = SplitList(state.ExcludePrefixes) 
            .Select(static item => item[0])
            .Distinct()
            .ToArray();
    }

    private static IEnumerable<string> SplitList(string text) =>
        (text ?? string.Empty)
            .Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static item => item.Trim())
            .Where(static item => item.Length > 0);

    #endregion

    #region 主循环

    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        if (state.Enabled)
        {
            var manager = RaptureAtkUnitManager.Instance();
            if (manager != null)
            {
                var entries = manager->AllLoadedUnitsList.Entries;
                for (var index = 0; index < entries.Length; index++)
                {
                    try
                    {
                        var addon = entries[index].Value;
                        if (ShouldTrack(addon, out var name))
                        {
                            ApplyPin(addon, name);
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        if (dirty && ImGui.GetTime() - lastSaveTime > 2.0)
        {
            Save();
        }
    }

    /// <summary>
    /// 每个 addon 自己更新完再钉位置。比 Framework.Update 晚一步，
    /// 否则游戏会在我们之后把窗口摆回它自己的坐标。
    /// </summary>
    private void OnAddonPostUpdate(AddonEvent type, AddonArgs args)
    {
        if (!state.Enabled)
        {
            return;
        }

        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (!ShouldTrack(addon, out var name))
            {
                return;
            }

            ApplyPin(addon, name);
        }
        catch
        {
            // 单个 addon 出问题不能拖垮整帧
        }
    }

    private void ApplyPin(AtkUnitBase* addon, string name)
    {
        var now = ImGui.GetTime();
        var k = ScreenScale();

        if (!addon->IsReady && addon->X == 0 && addon->Y == 0)
        {
            return;
        }

        if (!state.Windows.TryGetValue(name, out var entry))
        {
            entry = new WindowState
            {
                X = ToBaseline(addon->X, k),
                Y = ToBaseline(addon->Y, k),
                Locked = true,
                FirstSeen = now
            };
            state.Windows[name] = entry;
            dirty = true;
            return;
        }

        if (!entry.Locked)
        {
            var mx = ToBaseline(addon->X, k);
            var my = ToBaseline(addon->Y, k);
            if (mx != entry.X || my != entry.Y)
            {
                entry.X = mx;
                entry.Y = my;
                dirty = true;
            }

            return;
        }

        if (now - entry.FirstSeen < SettleSeconds)
        {
            entry.X = ToBaseline(addon->X, k);
            entry.Y = ToBaseline(addon->Y, k);
            dirty = true;
            return;
        }

        var targetX = Math.Clamp((int)MathF.Round(entry.X * k), short.MinValue, short.MaxValue);
        var targetY = Math.Clamp((int)MathF.Round(entry.Y * k), short.MinValue, short.MaxValue);

        var deltaX = Math.Abs(addon->X - targetX);
        var deltaY = Math.Abs(addon->Y - targetY);
        if (deltaX <= 2 && deltaY <= 2)
        {
            return;
        }

        addon->SetPosition((short)targetX, (short)targetY);

        pinDetail = $"{name} ({addon->X},{addon->Y})->({targetX},{targetY})";
    }
    private float ScreenScale()
    {
        if (!state.ScaleWithScreen)
        {
            return 1f;
        }

        var width = ImGui.GetIO().DisplaySize.X;
        return width <= 1f ? 1f : width / BaselineWidth;
    }

    private static int ToBaseline(int pixels, float scale) =>
        scale <= 0f || Math.Abs(scale - 1f) < 0.001f ? pixels : (int)MathF.Round(pixels / scale);

    #endregion

    #region 锁按钮（只在鼠标靠近窗口右缘时创建热区）

    private void OnDraw()
    {
        Diagnostics = $"已接管 {state.Windows.Count} 个窗口 · 右键窗口可锁定/解锁";

        if (!state.Enabled || !state.ShowLockIcon)
        {
            return;
        }

        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null)
        {
            return;
        }

        var mouse = ImGui.GetIO().MousePos;
        var display = ImGui.GetIO().DisplaySize;
        var size = Math.Clamp(state.LockSize, 14, 48);
        var entries = manager->AllLoadedUnitsList.Entries;
        var focused = manager->FocusedAddon;
        var lastCandidateCount = 0;
        var lastRejected = string.Empty;

        // 本轮可见的窗口（喂遮挡检测）；整屏大小的遮罩层不算遮挡源
        var visible = new List<(nint Pointer, int Index)>(32);
        for (var i = 0; i < entries.Length; i++)
        {
            var addon = entries[i].Value;
            if (!ShouldTrack(addon, out _))
            {
                continue;
            }

            visible.Add(((nint)addon, i));
        }

        // 1. 收集鼠标附近的候选窗口，先剔除被别的窗口压住的
        var candidates = new List<LockCandidate>(4);

        for (var i = 0; i < entries.Length; i++)
        {
            var addon = entries[i].Value;
            if (!ShouldTrack(addon, out var name))
            {
                continue;
            }

            if (!state.Windows.TryGetValue(name, out var entry))
            {
                continue;
            }

            var addonX = (float)addon->X;
            var addonY = (float)addon->Y;
            var width = addon->GetScaledWidth(true);

            // 鼠标不在窗口右上角附近就不参与，避免满屏热区
            if (mouse.X < addonX + width - 8f ||
                mouse.X > addonX + width + size + 48f ||
                mouse.Y < addonY - 12f ||
                mouse.Y > addonY + size + 24f)
            {
                continue;
            }

            // 鼠标所在位置必须正被这个窗口"压在最上面"，否则不给锁
            if (!state.IgnoreCovered && !IsTopmostAt(addon, mouse, visible))
            {
                lastRejected = name;
                lastCandidateCount++;
                continue;
            }

            var height = addon->GetScaledHeight(true);
            var mouseInside = mouse.X >= addonX && mouse.X <= addonX + width &&
                              mouse.Y >= addonY && mouse.Y <= addonY + height;

            candidates.Add(new LockCandidate
            {
                Addon = addon,
                Name = name,
                Entry = entry,
                Index = i,
                Depth = (int)addon->DrawOrderIndex,
                IsFocused = addon == focused,
                MouseInside = mouseInside
            });
        }

        var pin = string.IsNullOrEmpty(pinDetail) ? "无" : pinDetail;
        Diagnostics = lastCandidateCount > 0
            ? $"可见 {visible.Count} · 被上层挡住 {lastCandidateCount} [{lastRejected}] · 候选 {candidates.Count} · 最近钉 {pin}"
            : $"可见 {visible.Count} · 候选 {candidates.Count} · 最近钉 {pin}";

        if (candidates.Count == 0)
        {
            return;
        }

        // 2. 只取最前面的那个
        var flip = state.ReverseZOrder ? -1 : 1;
        candidates.Sort((left, right) =>
        {
            var byInside = right.MouseInside.CompareTo(left.MouseInside);
            if (byInside != 0) return byInside;

            var byFocus = right.IsFocused.CompareTo(left.IsFocused);
            if (byFocus != 0) return byFocus;

            var byDepth = (right.Depth - left.Depth) * flip;
            if (byDepth != 0) return byDepth;

            return (right.Index - left.Index) * flip;
        });

        var chosen = candidates[0];

        var buttonX = (float)chosen.Addon->X + chosen.Addon->GetScaledWidth(true) + 4f;
        if (buttonX + size > display.X)
        {
            buttonX = (float)chosen.Addon->X - size - 4f;
        }

        var buttonY = (float)chosen.Addon->Y + 4f;
        buttonX = Math.Clamp(buttonX, 0f, MathF.Max(0f, display.X - size));
        buttonY = Math.Clamp(buttonY, 0f, MathF.Max(0f, display.Y - size));

        DrawLockButton(chosen.Name, chosen.Entry, chosen.Index, new Vector2(buttonX, buttonY), size);
    }

    private struct LockCandidate
    {
        public AtkUnitBase* Addon;
        public string Name;
        public WindowState Entry;
        public int Index;
        public int Depth;
        public bool IsFocused;
        public bool MouseInside;
    }

    /// <summary>
    /// 用 7×7 网格采样估算被其它窗口覆盖的面积比例。
    /// 被压在下面的窗口不会遮挡上面的窗口，所以不需要知道 z 序方向。
    /// </summary>
    /// <summary>other 是否压在 target 之上（层级 > 绘制序 > 加载序）。</summary>
    private bool IsAbove(AtkUnitBase* other, int otherIndex, AtkUnitBase* target, int targetIndex)
    {
        var flip = state.ReverseZOrder ? -1 : 1;

        // DrawOrderIndex 已验证：招募板 119、技能菜单 118，数值大者在前
        var delta = (int)other->DrawOrderIndex - (int)target->DrawOrderIndex;
        if (delta != 0)
        {
            return delta * flip > 0;
        }

        return (otherIndex - targetIndex) * flip > 0;
    }

    /// <summary>只用压在目标之上的窗口算覆盖率，下面的窗口挡不住它。</summary>
    /// <summary>
    /// 鼠标点处是否就是这个窗口在最上层。
    /// 只要有任何"层级更高"的窗口也包含鼠标点，就说明它被压在下面。
    /// </summary>
    private bool IsTopmostAt(AtkUnitBase* target, Vector2 mouse, List<(nint Pointer, int Index)> visible)
    {
        var flip = state.ReverseZOrder ? -1 : 1;
        var targetOrder = (int)target->DrawOrderIndex;

        foreach (var item in visible)
        {
            var other = (AtkUnitBase*)item.Pointer;
            if (other == target)
            {
                continue;
            }

            var ox = (float)other->X;
            var oy = (float)other->Y;
            var ow = other->GetScaledWidth(true);
            var oh = other->GetScaledHeight(true);

            if (mouse.X < ox || mouse.X > ox + ow || mouse.Y < oy || mouse.Y > oy + oh)
            {
                continue;
            }

            if (((int)other->DrawOrderIndex - targetOrder) * flip > 0)
            {
                return false;
            }
        }

        return true;
    }
    #endregion

    #region 设置

    public override bool DrawSettings()
    {
        var enabled = state.Enabled;
        if (ImGui.Checkbox("启用锁定", ref enabled))
        {
            state.Enabled = enabled;
            Save();
        }

        ImGui.SameLine();
        var scale = state.ScaleWithScreen;
        if (ImGui.Checkbox("跟随分辨率缩放", ref scale))
        {
            state.ScaleWithScreen = scale;
            Save();
        }

        ImGui.Separator();

        var display = ImGui.GetIO().DisplaySize;
        var currentScale = ScreenScale();
        ImGui.TextDisabled($"已接管 {state.Windows.Count} 个窗口" +
                           (state.ScaleWithScreen ? $" · 基准 1920 宽 · 当前 {display.X:F0}×{display.Y:F0}（{currentScale:F3}×）"
                                                  : $" · 像素直用 · 当前 {display.X:F0}×{display.Y:F0}"));

        ImGui.TextDisabled("在游戏窗口上点右键，菜单里可锁定或解除锁定。");

        var lockedCount = 0;
        foreach (var item in state.Windows.Values)
        {
            if (item.Locked)
            {
                lockedCount++;
            }
        }

        ImGui.TextDisabled($"当前锁定 {lockedCount} 个窗口");

        if (ImGui.Button("全部解除窗口锁定") && lockedCount > 0)
        {
            foreach (var item in state.Windows.Values)
            {
                item.Locked = false;
            }

            Save();
            DService.Instance().Chat.Print("[窗口位置锁定] 已解除全部窗口锁定");
        }

        if (!string.IsNullOrEmpty(Diagnostics))
        {
            ImGui.TextDisabled(Diagnostics);
        }

        if (ImGui.CollapsingHeader("排除与重置"))
        {
            ImGui.TextDisabled("以这些前缀开头的窗口不接管（默认 _ 表示 HUD 节点）");
            var prefixes = state.ExcludePrefixes;
            if (ImGui.InputText("前缀", ref prefixes, 64))
            {
                state.ExcludePrefixes = prefixes;
                RebuildExclusions();
                dirty = true;
            }

            ImGui.TextDisabled("额外排除的窗口名，逗号分隔（动态弹窗建议留在这里）");
            var names = state.ExcludeNames;
            if (ImGui.InputTextMultiline("##excludeNames", ref names, 512, new Vector2(-1, 60)))
            {
                state.ExcludeNames = names;
                RebuildExclusions();
                dirty = true;
            }

            if (ImGui.Button("清空全部记录"))
            {
                state.Windows.Clear();
                Save();
            }

            ImGui.SameLine();
            if (ImGui.Button("立即保存"))
            {
                Save();
            }

            ImGui.TextDisabled("清空后每个窗口下次打开时会重新按它当时的游戏位置记录。");
        }

        return false;
    }

    #endregion

    #region 配置

    private void Load()
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<PinState>(File.ReadAllText(configPath));
            if (loaded is null)
            {
                return;
            }

            state.Enabled = loaded.Enabled;
            state.ShowLock = loaded.ShowLock;
            state.ShowLockIcon = loaded.ShowLockIcon;
            state.ScaleWithScreen = loaded.ScaleWithScreen;
            state.LockSize = loaded.LockSize;
            state.IdleAlpha = loaded.IdleAlpha;
            state.ExcludePrefixes = loaded.ExcludePrefixes;
            state.ExcludeNames = loaded.ExcludeNames;
            state.IgnoreCovered = loaded.IgnoreCovered;
            state.CoverThreshold = loaded.CoverThreshold;
            state.ReverseZOrder = loaded.ReverseZOrder;

            if (loaded.Windows is { Count: > 0 })
            {
                foreach (var (key, value) in loaded.Windows)
                {
                    value.FirstSeen = -100;
                    state.Windows[key] = value;
                }
            }
        }
        catch (Exception exception)
        {
            DService.Instance().Log.Warning(exception, "[AddonPositionLock] 读取配置失败，使用默认值");
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(configPath, JsonSerializer.Serialize(state,
                new JsonSerializerOptions { WriteIndented = true }));

            dirty = false;
            lastSaveTime = ImGui.GetTime();
        }
        catch (Exception exception)
        {
            DService.Instance().Log.Warning(exception, "[AddonPositionLock] 保存配置失败");
        }
    }

    private sealed class WindowState
    {
        public int X { get; set; }

        public int Y { get; set; }

        public bool Locked { get; set; } = true;

        /// <summary>首次见到的时间（运行期用，不落盘）。</summary>
        [JsonIgnore]
        public double FirstSeen { get; set; }
    }

    private sealed class PinState
    {
        public bool Enabled { get; set; } = true;

        public bool ShowLock { get; set; } = true;

        /// <summary>边缘锁图标，默认关闭；锁定走窗口右键菜单。</summary>
        public bool ShowLockIcon { get; set; }

        /// <summary>关闭后不再按遮挡剔除，任何窗口靠近都出锁。</summary>
        public bool IgnoreCovered { get; set; }

        /// <summary>被别的窗口盖住多少比例就不出锁。</summary>
        public float CoverThreshold { get; set; } = 0.5f;

        /// <summary>万一层级方向与实测不符时勾上，把"越靠前"的判定整体翻转。</summary>
        public bool ReverseZOrder { get; set; }

        public bool ScaleWithScreen { get; set; } = true;

        public int LockSize { get; set; } = 24;

        public float IdleAlpha { get; set; }

        /// <summary>不接管的窗口名前缀，默认排除 HUD 节点。</summary>
        public string ExcludePrefixes { get; set; } = "_";

        /// <summary>不接管的窗口名（逗号分隔）。</summary>
        public string ExcludeNames { get; set; } =
            "Tooltip,ContextMenu,ContextIconMenu,AddonContextMenuTitle,AddonContextSub," +
            "ItemDetail,ActionDetail,ActionDetail2,ActionDetail3,XBMItemDetail,MiragePrismPrismItemDetail," +
            "InputString,InputNumeric,SelectString,SelectYesno,SelectIconString,SelectKey,SelectOk," +
            "FadeBack,FadeMiddle,NowLoading,ScreenFrameSystem";

        public Dictionary<string, WindowState> Windows { get; set; } = new(StringComparer.Ordinal);
    }

    #endregion
}

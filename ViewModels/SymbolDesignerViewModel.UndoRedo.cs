using System;
using System.Collections.Generic;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

public partial class SymbolDesignerViewModel
{
    // ============== 撤销栈与手势状态 ==============

    /// <summary>是否正在执行撤销/重做恢复。恢复过程触发的大量集合/属性变化不应再进入历史。</summary>
    private bool _isRestoring;

    /// <summary>当前手势深度。&gt;0 时暂停去抖自动提交,由手势结束(EndEdit)统一收口,
    /// 避免拖动元素/顶点等长手势中途被 350ms 去抖切断,把一个手势拆成多个撤销步骤。</summary>
    private int _gestureDepth;

    /// <summary>手势开始(或第一次属性变化)时记录的前置快照;非 null = 有未提交的编辑。
    /// 提交时若与当前状态不同则压入撤销栈。</summary>
    private string? _pendingSnapshot;

    /// <summary>撤销栈(快照 = Primitives 的 DTO JSON 字符串,深拷贝无共享引用)。</summary>
    private readonly Stack<string> _undoStack = new();

    /// <summary>重做栈。</summary>
    private readonly Stack<string> _redoStack = new();

    /// <summary>撤销栈最大长度,防止长会话内存膨胀。</summary>
    private const int UndoLimit = 50;

    /// <summary>属性编辑去抖计时器:最后一次变化后 350ms 无新变化 → 自动提交一个快照。
    /// 把 Slider 连续拖动产生的大量中间值合并为一次撤销(用户松开后 350ms 才落栈)。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _undoCommitTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(350),
    };

    /// <summary>是否有可撤销的历史。</summary>
    [ObservableProperty]
    private bool _canUndo;

    /// <summary>是否有可重做的历史。</summary>
    [ObservableProperty]
    private bool _canRedo;

    // ============== 快照与栈操作 ==============

    /// <summary>生成当前 Primitives + ArrayInstances + Constraints 的深拷贝快照(DTO JSON)。
    /// 序列化顺序与内容完全确定性:相同状态 → 相同字符串,可直接比较。
    /// 把两个集合打包成单个 JSON 对象（Primitives + ArrayInstances + Constraints）→ 一个快照覆盖画布全部状态。</summary>
    private string SnapshotPrimitives()
    {
        var dtoList = new List<PrimitiveItemDto>(Primitives.Count);
        foreach (var p in Primitives)
            dtoList.Add(ToDto(p));
        var arrDtoList = new List<ArrayInstanceDto>(ArrayInstances.Count);
        foreach (var a in ArrayInstances)
            arrDtoList.Add(ArrayInstanceToDto(a));
        var conDtoList = new List<TangencyConstraintDto>(Constraints.Count);
        foreach (var c in Constraints)
            conDtoList.Add(ToConstraintDto(c));
        var snap = new ProjectSnapshotDto
        {
            Primitives = dtoList,
            ArrayInstances = arrDtoList,
            Constraints = conDtoList,
        };
        return System.Text.Json.JsonSerializer.Serialize(snap);
    }

    /// <summary>用快照整体替换 Primitives + ArrayInstances + Constraints(撤销/重做恢复)。
    /// 期间抑制一切历史记录。</summary>
    private void RestorePrimitives(string snapshot)
    {
        _isRestoring = true;
        try
        {
            var snap = System.Text.Json.JsonSerializer.Deserialize<ProjectSnapshotDto>(snapshot);
            var list = snap?.Primitives ?? new List<PrimitiveItemDto>();
            var arrList = snap?.ArrayInstances ?? new List<ArrayInstanceDto>();
            ClearAllCore(scheduleUndo: false);
            foreach (var dto in list)
                Primitives.Add(CreateFromDto(dto));
            ItemCount = Primitives.Count;
            ArrayInstances.Clear();
            foreach (var adto in arrList)
                ArrayInstances.Add(ArrayInstanceFromDto(adto));
            // 恢复约束：快照由本 VM 生成，图元引用必然存在；无约束的快照恢复为空集合
            Constraints.Clear();
            if (snap?.Constraints != null)
            {
                foreach (var cdto in snap.Constraints)
                {
                    var constraint = ConstraintFromDto(cdto);
                    if (constraint is not null)
                        Constraints.Add(constraint);
                }
                PruneConstraints();
            }
            // 恢复后选中/手柄全部清空,用户重新点击才显示
            SelectedItem = null;
            VertexHandles.Clear();
            SegmentHandles.Clear();
            OutlineCornerHandles.Clear();
            // 传送带几何不落盘：撤销/重做恢复图元后统一重算（否则皮带无几何 → 不可见）
            SolveAllBelts();
            IsDirty = true;
        }
        finally
        {
            _isRestoring = false;
            // 恢复期间 CollectionChanged 的 RefreshHiddenCount 被 _isRestoring 跳过 → 这里补刷一次
            RefreshHiddenCount();
        }
    }

    /// <summary>开始一个用户手势(拖动元素/顶点)。暂停去抖自动提交,防止手势中途被截断。</summary>
    public void BeginEdit()
    {
        _undoCommitTimer.Stop();
        if (_pendingSnapshot is null)
            _pendingSnapshot = SnapshotPrimitives();
        _gestureDepth++;
    }

    /// <summary>结束一个用户手势。手势深度归零时提交一次快照。</summary>
    public void EndEdit()
    {
        if (_gestureDepth > 0) _gestureDepth--;
        if (_gestureDepth == 0)
            ForceCommitEdit();
    }

    /// <summary>立即把未提交的编辑压入撤销栈(若有。内容无变化则丢弃)。</summary>
    public void ForceCommitEdit()
    {
        if (_pendingSnapshot is null) return;
        _undoCommitTimer.Stop();
        var current = SnapshotPrimitives();
        if (current != _pendingSnapshot)
        {
            _undoStack.Push(_pendingSnapshot);
            _redoStack.Clear();
            TrimUndoStack();
            UpdateCanUndoRedo();
        }
        _pendingSnapshot = null;
    }

    /// <summary>丢弃全部历史(加载/新建文档后调用)。未提交编辑一并丢弃。</summary>
    public void ResetUndoRedo()
    {
        _undoCommitTimer.Stop();
        _pendingSnapshot = null;
        _undoStack.Clear();
        _redoStack.Clear();
        UpdateCanUndoRedo();
    }

    /// <summary>属性/集合发生变化时调度"去抖提交"。恢复过程(撤销/重做)自动跳过。</summary>
    private void ScheduleUndoCommit()
    {
        if (_isRestoring || IsDrawing) return;
        if (_gestureDepth > 0) return;           // 手势中 → 由 EndEdit 收口
        if (_pendingSnapshot is null)
            _pendingSnapshot = SnapshotPrimitives();
        _undoCommitTimer.Stop();
        _undoCommitTimer.Start();
    }

    /// <summary>控制撤销栈长度。</summary>
    private void TrimUndoStack()
    {
        while (_undoStack.Count > UndoLimit)
            _undoStack.Pop();
    }

    private void UpdateCanUndoRedo()
    {
        if (CanUndo != _undoStack.Count > 0) CanUndo = _undoStack.Count > 0;
        if (CanRedo != _redoStack.Count > 0) CanRedo = _redoStack.Count > 0;
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>撤销:先提交未完成编辑(若用户刚拖完 Slider 未到 350ms),再回退一步。</summary>
    [RelayCommand]
    private void Undo()
    {
        ForceCommitEdit();
        if (_undoStack.Count == 0) return;
        _redoStack.Push(SnapshotPrimitives());
        RestorePrimitives(_undoStack.Pop());
        UpdateCanUndoRedo();
    }

    /// <summary>重做。</summary>
    [RelayCommand]
    private void Redo()
    {
        ForceCommitEdit();
        if (_redoStack.Count == 0) return;
        _undoStack.Push(SnapshotPrimitives());
        RestorePrimitives(_redoStack.Pop());
        UpdateCanUndoRedo();
    }
}
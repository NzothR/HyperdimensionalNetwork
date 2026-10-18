using System;
using System.Collections.Generic;
using System.Linq;

namespace HyperdimensionalNetwork;

public sealed class XUiC_HyperdimensionalGrid : XUiC_ItemStackGrid
{
    private const int PageSizeFallback = 63;
    private readonly List<StoredEntry> _allEntries = new List<StoredEntry>();
    private readonly List<StoredEntry> _visibleEntries = new List<StoredEntry>();
    private readonly Dictionary<ItemValue, StoredEntry> _entryIndex = new Dictionary<ItemValue, StoredEntry>(ItemValueComparer.Instance);
    private string _category;
    private string _search = string.Empty;
    private int _page;
    private long _revision = -1;
    private int _pageSize;

    public int EntryCount => _allEntries.Count;
    public int PageCount => Math.Max(1, (_visibleEntries.Count + Math.Max(1, _pageSize) - 1) / Math.Max(1, _pageSize));
    public int PagingElementCount => Math.Max(_pageSize, _visibleEntries.Count);
    public int VisibleSlotCount => _pageSize;
    public int Page => _page;

    public override XUiC_ItemStack.StackLocationTypes StackLocation => XUiC_ItemStack.StackLocationTypes.ToolBelt;

    public override void Init()
    {
        base.Init();
        _pageSize = Math.Max(1, itemControllers?.Length ?? PageSizeFallback);
        items = ItemStack.CreateArray(_pageSize);
        XUiC_ItemInfoWindow infoWindow = xui?.GetChildByType<XUiC_ItemInfoWindow>();
        for (int i = 0; i < itemControllers.Length; i++)
        {
            XUiC_HyperdimensionalSlot slot = itemControllers[i] as XUiC_HyperdimensionalSlot;
            if (slot == null) { NetworkLog.Error("Grid item controller is not XUiC_HyperdimensionalSlot at index " + i); continue; }
            slot.Bind(this, i);
            slot.SlotNumber = i;
            slot.InfoWindow = infoWindow;
            slot.StackLocation = StackLocation;
            slot.ViewComponent.IsVisible = true;
        }
        NetworkLog.Debug("Grid initialized. visibleSlots=" + _pageSize + "; infoWindow=" + (infoWindow != null));
        Reload();
    }

    public override ItemStack[] GetSlots() => items;

    public override void OnOpen() { base.OnOpen(); Reload(); }

    public override void Update(float _dt)
    {
        if (ViewComponent != null && ViewComponent.IsVisible)
        {
            long currentRevision = NetworkStorage.GetRevision(xui?.playerUI?.entityPlayer);
            if (currentRevision != _revision) Reload();
            if (IsDirty) { RefreshVisible(); IsDirty = false; }
        }
        base.Update(_dt);
    }

    public override void HandleSlotChangedEvent(int slotNumber, ItemStack stack) { }

    public void Reload()
    {
        EntityPlayer player = xui?.playerUI?.entityPlayer;
        if (!(bool)player) { NetworkLog.Debug("Grid reload skipped because player is unavailable."); return; }
        _allEntries.Clear();
        _allEntries.AddRange(NetworkStorage.GetEntries(player));
        _entryIndex.Clear();
        foreach (StoredEntry entry in _allEntries) _entryIndex[entry.Value] = entry;
        _revision = NetworkStorage.GetRevision(player);
        RebuildFiltered();
        IsDirty = true;
        NetworkLog.Debug("Grid projection refreshed. variants=" + _allEntries.Count + "; filtered=" + _visibleEntries.Count + "; revision=" + _revision + "; page=" + _page);
    }

    public void SetCategory(string category) { _category = category ?? string.Empty; _page = 0; RebuildFiltered(); RefreshPager(); }
    public void SetSearch(string search) { _search = (search ?? string.Empty).Trim(); _page = 0; RebuildFiltered(); RefreshPager(); }

    public void SetPage(int page)
    {
        _page = Math.Max(0, Math.Min(page, PageCount - 1));
        IsDirty = true;
        RefreshPager();
    }

    public bool AddFromMouse(ItemStack mouseStack, bool single)
    {
        if (mouseStack == null || mouseStack.IsEmpty()) return false;
        int requested = single ? 1 : Math.Max(1, mouseStack.count);
        ItemStack insertion = mouseStack.Clone();
        insertion.count = requested;
        if (!NetworkStorage.AddStack(xui.playerUI.entityPlayer, insertion)) return false;
        mouseStack.count -= requested;
        XUiC_DragAndDropWindow drag = xui?.DragAndDropWindow;
        drag?.SetCurrentStack(mouseStack.count > 0 ? mouseStack : ItemStack.Empty.Clone());
        if (drag != null) drag.PickUpType = StackLocation;
        SetProjectedCount(insertion.itemValue, NetworkStorage.GetCount(xui.playerUI.entityPlayer, insertion.itemValue));
        return true;
    }

    public bool TakeToMouse(int visibleIndex, bool half)
    {
        int filteredIndex = _page * _pageSize + visibleIndex;
        if (filteredIndex < 0 || filteredIndex >= _visibleEntries.Count) return false;
        XUiC_DragAndDropWindow drag = xui?.DragAndDropWindow;
        if (drag == null || (drag.CurrentStack != null && !drag.CurrentStack.IsEmpty())) return false;
        StoredEntry entry = _visibleEntries[filteredIndex];
        ItemClass itemClass = entry.Value.ItemClass;
        int stackLimit = Math.Max(1, itemClass?.Stacknumber?.Value ?? 1);
        int request = (int)Math.Min(Math.Min(entry.Count, stackLimit), int.MaxValue);
        if (half) request = Math.Max(1, request / 2);
        int removed = NetworkStorage.Remove(xui.playerUI.entityPlayer, entry.Value, request);
        if (removed <= 0) return false;
        drag.SetCurrentStack(new ItemStack(entry.Value.Clone(), removed));
        drag.PickUpType = StackLocation;
        SetProjectedCount(entry.Value, NetworkStorage.GetCount(xui.playerUI.entityPlayer, entry.Value));
        return true;
    }

    private void SetProjectedCount(ItemValue value, long actualCount)
    {
        ItemValue key = NetworkStorage.NormalizeKey(value);
        if (_entryIndex.TryGetValue(key, out StoredEntry entry))
        {
            entry.Count = Math.Max(0, actualCount);
            if (entry.Count <= 0)
            {
                _entryIndex.Remove(entry.Value);
                _allEntries.Remove(entry);
                _visibleEntries.Remove(entry);
            }
            else if (!string.IsNullOrEmpty(_category) || !string.IsNullOrEmpty(_search))
            {
                bool matches = MatchesFilters(entry.Value);
                if (matches && !_visibleEntries.Contains(entry)) InsertSorted(_visibleEntries, entry);
                else if (!matches) _visibleEntries.Remove(entry);
            }
        }
        else if (actualCount > 0)
        {
            entry = new StoredEntry { Value = key, Count = actualCount };
            _entryIndex[entry.Value] = entry;
            InsertSorted(_allEntries, entry);
            if (MatchesFilters(entry.Value)) InsertSorted(_visibleEntries, entry);
        }
        _revision = NetworkStorage.GetRevision(xui?.playerUI?.entityPlayer);
        IsDirty = true;
        RefreshPager();
        NetworkLog.Debug("Visible inventory reconciled from committed storage count. item=" + key.type + "; count=" + actualCount + "; variants=" + _allEntries.Count + "; revision=" + _revision);
    }

    private bool MatchesFilters(ItemValue value)
    {
        if (!string.IsNullOrEmpty(_category) && !MatchesCategory(value, _category)) return false;
        return string.IsNullOrEmpty(_search) || MatchesSearch(value, _search);
    }

    private static void InsertSorted(List<StoredEntry> entries, StoredEntry entry)
    {
        int low = 0, high = entries.Count;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (CompareOrder(entries[middle].Value, entry.Value) <= 0) low = middle + 1;
            else high = middle;
        }
        entries.Insert(low, entry);
    }

    private static int CompareOrder(ItemValue left, ItemValue right)
    {
        int type = left.type.CompareTo(right.type);
        return type != 0 ? type : left.Quality.CompareTo(right.Quality);
    }

    private void RebuildFiltered()
    {
        IEnumerable<StoredEntry> query = _allEntries;
        if (!string.IsNullOrEmpty(_category)) query = query.Where(entry => MatchesCategory(entry.Value, _category));
        if (!string.IsNullOrEmpty(_search)) query = query.Where(entry => MatchesSearch(entry.Value, _search));
        _visibleEntries.Clear();
        _visibleEntries.AddRange(query);
        _page = Math.Max(0, Math.Min(_page, PageCount - 1));
        IsDirty = true;
    }

    private void RefreshPager()
    {
        XUiC_Paging pager = null;
        for (XUiController parent = Parent; parent != null && pager == null; parent = parent.Parent) pager = parent.GetChildByType<XUiC_Paging>();
        if (pager != null)
        {
            pager.SetLastPageByElementsAndPageLength(PagingElementCount, Math.Max(1, _pageSize));
            pager.SetPage(_page);
        }
    }

    private void RefreshVisible()
    {
        for (int i = 0; i < itemControllers.Length; i++)
        {
            int index = _page * _pageSize + i;
            ItemStack projection = ItemStack.Empty.Clone();
            if (index < _visibleEntries.Count)
            {
                StoredEntry entry = _visibleEntries[index];
                int displayed = (int)Math.Min(entry.Count, int.MaxValue);
                projection = new ItemStack(entry.Value.Clone(), displayed);
            }
            if (i < items.Length) items[i] = projection;
            itemControllers[i].ForceSetItemStack(projection);
            XUiC_HyperdimensionalSlot slot = itemControllers[i] as XUiC_HyperdimensionalSlot;
            slot?.SetVisibleIndex(i);
        }
    }

    private static bool MatchesSearch(ItemValue value, string text)
    {
        ItemClass itemClass = value?.ItemClass;
        if (itemClass == null) return false;
        return (itemClass.Name ?? string.Empty).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0
            || (itemClass.GetLocalizedItemName() ?? string.Empty).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0
            || value.type.ToString().IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool MatchesCategory(ItemValue value, string category)
    {
        ItemClass itemClass = value?.ItemClass;
        if (itemClass == null) return false;
        bool isBlock = itemClass.IsBlock() || (value.type > 0 && value.type < Block.ItemsStartHere);
        if (category.Equals("Blocks", StringComparison.OrdinalIgnoreCase)) return isBlock;
        if (category.Equals("Items", StringComparison.OrdinalIgnoreCase)) return !isBlock;
        if (category.Equals("Ranged Weapons", StringComparison.OrdinalIgnoreCase) && itemClass.ItemTags.Test_AnySet(FastTags<TagGroup.Global>.Parse("ammo"))) return false;
        int blockId = value.type < Block.ItemsStartHere ? value.type : (itemClass.IsBlock() ? itemClass.GetBlock()?.blockID ?? -1 : -1);
        if (isBlock && blockId >= 0 && blockId < Block.list.Length && Contains(Block.list[blockId]?.GroupNames, category)) return true;
        return Contains(itemClass.Groups, category);
    }

    private static bool Contains(string[] groups, string category) => groups != null && groups.Any(group => string.Equals(group, category, StringComparison.OrdinalIgnoreCase));

    private sealed class ItemValueComparer : IEqualityComparer<ItemValue>
    {
        public static readonly ItemValueComparer Instance = new ItemValueComparer();
        public bool Equals(ItemValue left, ItemValue right) => ReferenceEquals(left, right) || (left != null && left.Equals(right));
        public int GetHashCode(ItemValue value)
        {
            if (value == null) return 0;
            unchecked
            {
                int hash = value.type;
                hash = hash * 397 ^ value.Quality;
                hash = hash * 397 ^ value.Seed;
                hash = hash * 397 ^ value.UseTimes.GetHashCode();
                hash = hash * 397 ^ value.Meta;
                hash = hash * 397 ^ value.SelectedAmmoTypeIndex;
                return hash * 397 ^ (value.Activated ? 1 : 0);
            }
        }
    }
}

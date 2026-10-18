using System;
using System.Collections.Generic;

namespace HyperdimensionalNetwork;

public sealed class XUiC_HyperdimensionalNetworkWindow : XUiController
{
    private XUiC_HyperdimensionalGrid _grid;
    private XUiC_CategoryList _categories;
    private XUiC_TextInput _search;
    private XUiC_Paging _pager;

    public override void Init()
    {
        base.Init();
        _grid = GetChildByType<XUiC_HyperdimensionalGrid>() ?? Parent?.GetChildByType<XUiC_HyperdimensionalGrid>();
        _pager = GetChildByType<XUiC_Paging>();
        if (_pager != null) _pager.OnPageChanged += OnPageChanged;
        _categories = GetChildById("categories") as XUiC_CategoryList;
        if (_categories != null) _categories.CategoryChanged += OnCategoryChanged;
        _search = GetChildById("searchInput") as XUiC_TextInput;
        if (_search == null) _search = Parent?.GetChildById("searchInput") as XUiC_TextInput;
        if (_search != null) _search.OnChangeHandler += OnSearchChanged;
        List<XUiC_ItemStack> slots = new List<XUiC_ItemStack>();
        GetChildrenByType(slots);
        foreach (XUiC_ItemStack slot in slots) slot.OnScroll += OnNetworkScroll;
        OnScroll += OnNetworkScroll;
        NetworkLog.Debug("Window controller initialized. grid=" + (_grid != null) + "; categories=" + (_categories != null) + "; search=" + (_search != null));
    }

    public override void OnOpen()
    {
        base.OnOpen();
        _grid?.Reload();
        RefreshPager();
        NetworkLog.Debug("Network window opened. entries=" + (_grid?.EntryCount ?? 0));
    }

    private void OnCategoryChanged(XUiC_CategoryEntry category) => _grid?.SetCategory(category?.CategoryName);
    private void OnSearchChanged(XUiController controller, string text, bool changeFromCode) => _grid?.SetSearch(text);
    private void OnPageChanged() { if (_pager != null) _grid?.SetPage(_pager.CurrentPageNumber); }

    private void OnNetworkScroll(XUiController sender, float delta)
    {
        if (_grid == null || delta == 0f) return;
        int next = _grid.Page + (delta < 0f ? 1 : -1);
        if (next == _grid.Page || next < 0 || next >= _grid.PageCount) return;
        _grid.SetPage(next);
        NetworkLog.Debug("Network page changed by mouse wheel. page=" + next + "; total=" + _grid.PageCount);
    }

    private void RefreshPager()
    {
        if (_pager == null || _grid == null) return;
        _pager.SetLastPageByElementsAndPageLength(_grid.PagingElementCount, Math.Max(1, _grid.VisibleSlotCount));
        _pager.SetPage(_grid.Page);
    }
}

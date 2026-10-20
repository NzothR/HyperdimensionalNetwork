using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace HyperdimensionalNetwork;

internal static class ForgeFeedSession
{
    internal static Vector3i CurrentForgePosition;
    internal static bool HasCurrentForge;
}

public sealed class XUiC_ForgeFeedButton : XUiController
{
    public override void Init()
    {
        base.Init();
        OnPress += (sender, mouse) =>
        {
            EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
            if (!(bool)player || XUiM_Recipes.DisableSmelter) return;
            XUiC_WorkstationMaterialInputWindow parent = GetParentByType<XUiC_WorkstationMaterialInputWindow>();
            TileEntityWorkstation currentForge = parent?.inputGrid?.WorkstationData?.TileEntity;
            ForgeFeedSession.HasCurrentForge = currentForge != null;
            if (currentForge != null) ForgeFeedSession.CurrentForgePosition = currentForge.ToWorldPos();
            NetworkLog.Debug("Forge feed button pressed. player=" + player.entityId + "; currentForge="
                + (currentForge == null ? "unavailable" : currentForge.ToWorldPos().ToString()));
            try { xui.playerUI.windowManager.Open("hyperdimensionalForgeFeed", _bModal: true); }
            catch (Exception ex)
            {
                ForgeFeedSession.HasCurrentForge = false;
                NetworkLog.Error("Forge feed panel could not open. player=" + player.entityId + "; exception=" + ex);
            }
        };
    }

    public override void OnOpen()
    {
        base.OnOpen();
        if (ViewComponent != null) ViewComponent.IsVisible = !XUiM_Recipes.DisableSmelter;
    }
}

public sealed class XUiC_ForgeFeedWindow : XUiController
{
    private const int MaterialRows = ForgeFeedService.MaximumMaterials;
    private const int ForgeRows = ForgeFeedService.MaximumForges;
    private readonly XUiController[] _materialButtons = new XUiController[MaterialRows];
    private readonly XUiController[] _materialLabels = new XUiController[MaterialRows];
    private readonly XUiController[] _materialSupply = new XUiController[MaterialRows];
    private readonly XUiController[] _forgeButtons = new XUiController[ForgeRows];
    private readonly XUiController[] _forgeNames = new XUiController[ForgeRows];
    private readonly XUiController[,] _forgeCells = new XUiController[ForgeRows, MaterialRows];
    private readonly HashSet<string> _selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Vector3i> _selectedForges = new HashSet<Vector3i>();
    private readonly HashSet<Vector3i> _highlightedForges = new HashSet<Vector3i>();
    private List<ForgeFeedStation> _forges = new List<ForgeFeedStation>();
    private List<ForgeFeedMaterial> _materials = new List<ForgeFeedMaterial>();
    private XUiC_TextInput _fillInput;
    private XUiController _status;
    private bool _requestPending;
    private float _nextRefresh;
    private Vector3i _currentForgePosition;
    private bool _hasCurrentForge;

    public override void Init()
    {
        base.Init();
        _status = GetChildById("forgeFeedStatus");
        _fillInput = GetChildById("forgeFeedFillInput") as XUiC_TextInput;
        for (int i = 0; i < MaterialRows; i++)
        {
            int index = i;
            _materialButtons[i] = GetChildById("btnForgeFeedMaterial" + i);
            _materialLabels[i] = GetChildById("forgeFeedMaterial" + i);
            _materialSupply[i] = GetChildById("forgeFeedSupply" + i);
            if (_materialButtons[i] != null) _materialButtons[i].OnPress += (_, _) => ToggleMaterial(index);
        }
        for (int row = 0; row < ForgeRows; row++)
        {
            _forgeButtons[row] = GetChildById("btnForgeFeedForge" + row);
            _forgeNames[row] = GetChildById("forgeFeedName" + row);
            int index = row;
            if (_forgeButtons[row] != null) _forgeButtons[row].OnPress += (_, _) => ToggleForge(index);
            for (int col = 0; col < MaterialRows; col++)
                _forgeCells[row, col] = GetChildById("forgeFeedCell" + row + "_" + col);
        }
        Bind("btnForgeFeedClose", (_, _) => xui.playerUI.windowManager.Close("hyperdimensionalForgeFeed"));
        Bind("btnForgeFeedExecute", (_, _) => Execute());
        Bind("btnForgeFeedSelectAll", (_, _) => SelectAllForges());
        Bind("btnForgeFeedInvert", (_, _) => InvertForges());
        int[] presets = { 1000, 2500, 5000, 10000, 0 };
        for (int i = 0; i < presets.Length; i++)
        {
            int fill = presets[i];
            Bind("btnForgeFeedPreset" + i, (_, _) =>
            {
                if (_fillInput != null) _fillInput.Text = fill.ToString(CultureInfo.InvariantCulture);
                NetworkLog.Debug("Forge feed target preset applied. fill=" + fill);
                Render();
            });
        }
    }

    private void Bind(string id, Action<XUiController, int> handler)
    {
        XUiController button = GetChildById(id);
        if (button != null) button.OnPress += (sender, mouse) =>
        {
            NetworkLog.Debug("Forge feed button pressed. id=" + id);
            handler(sender, mouse);
        };
        else NetworkLog.Error("Forge feed button was not found. id=" + id);
    }

    public override void OnOpen()
    {
        base.OnOpen();
        _currentForgePosition = ForgeFeedSession.CurrentForgePosition;
        _hasCurrentForge = ForgeFeedSession.HasCurrentForge;
        ForgeFeedSession.HasCurrentForge = false;
        _selectedForges.Clear();
        if (_fillInput != null && string.IsNullOrWhiteSpace(_fillInput.Text))
            _fillInput.Text = ForgeFeedService.DefaultFill.ToString(CultureInfo.InvariantCulture);
        Refresh();
        ForgeFeedStation initial = _hasCurrentForge
            ? _forges.FirstOrDefault(forge => forge.Position.Equals(_currentForgePosition))
            : null;
        if (initial == null) initial = _forges.FirstOrDefault();
        if (initial != null) _selectedForges.Add(initial.Position);
        RefreshHighlights();
        Render();
        if (_forges.Count > 0 && _materials.Count > 0)
            SetText(_status, Localization.Get("HyperdimensionalNetworkForgeFeedHelp"));
        NetworkLog.Debug("Forge feed panel opened. nearby=" + _forges.Count + "; materials=" + _materials.Count
            + "; current=" + (_hasCurrentForge ? _currentForgePosition.ToString() : "unavailable")
            + "; selected=" + string.Join(";", _selectedForges));
    }

    public override void OnClose()
    {
        ClearHighlights();
        _selectedForges.Clear();
        base.OnClose();
    }

    public override void Update(float dt)
    {
        base.Update(dt);
        if (windowGroup?.isShowing == true && !_requestPending && Time.realtimeSinceStartup >= _nextRefresh)
        {
            _nextRefresh = Time.realtimeSinceStartup + 1.5f;
            Refresh();
        }
    }

    private void Refresh()
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player || GameManager.Instance?.World == null) return;
        try
        {
            _forges = ForgeFeedService.FindNearby(GameManager.Instance.World, player);
            _materials = ForgeFeedService.BuildMaterials(_forges);
            HashSet<Vector3i> availablePositions = new HashSet<Vector3i>(_forges.Select(forge => forge.Position));
            _selectedForges.RemoveWhere(position => !availablePositions.Contains(position));
            RefreshHighlights();
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Forge feed preview failed. player=" + player.entityId + "; exception=" + ex);
            SetText(_status, Localization.Get("HyperdimensionalNetworkForgeFeedPreviewFailed"));
            return;
        }
        Render();
    }

    private void ToggleMaterial(int index)
    {
        if (_requestPending || index >= _materials.Count) return;
        string category = _materials[index].Category;
        if (!_selected.Add(category)) _selected.Remove(category);
        NetworkLog.Debug("Forge feed material toggled. category=" + category + "; selected=" + _selected.Contains(category));
        Render();
    }

    private void Render()
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        for (int col = 0; col < MaterialRows; col++)
        {
            bool present = col < _materials.Count;
            SetVisible(_materialButtons[col], present);
            SetVisible(_materialLabels[col], present);
            SetVisible(_materialSupply[col], present);
            if (!present) continue;
            ForgeFeedMaterial material = _materials[col];
            SetText(_materialLabels[col], (_selected.Contains(material.Category) ? "[x] " : "[ ] ") + material.Name);
            long bag = 0, network = 0;
            if ((bool)player)
                foreach (ItemClass raw in material.RawItems)
                {
                    for (int i = 0; i < player.bag.SlotCount; i++)
                    {
                        ItemStack stack = player.bag.ItemGrid[i];
                        if (stack != null && !stack.IsEmpty() && stack.itemValue.type == raw.Id
                            && ForgeFeedService.IsPlainRaw(stack.itemValue)
                            && (player.bag.LockedSlots == null || i >= player.bag.LockedSlots.Length || !player.bag.LockedSlots[i])) bag += stack.count;
                    }
                    IEnumerable<StoredEntry> entries = NetworkRemoteClient.IsRemoteClient
                        ? NetworkRemoteClient.GetEntriesForRequirements(player, new[] { new ItemStack(new ItemValue(raw.Id), 1) })
                        : NetworkStorage.GetEntriesForRequirements(player, new[] { new ItemStack(new ItemValue(raw.Id), 1) });
                    network += entries
                        .Where(entry => entry.Value.type == raw.Id && ForgeFeedService.IsPlainRaw(entry.Value))
                        .Sum(entry => entry.Count);
                }
            SetText(_materialSupply[col], "包 " + bag + " / 网 " + network);
        }
        for (int row = 0; row < ForgeRows; row++)
        {
            bool present = row < _forges.Count;
            SetVisible(_forgeButtons[row], present);
            SetVisible(_forgeNames[row], present);
            if (present)
            {
                ForgeFeedStation forge = _forges[row];
                SetText(_forgeNames[row], (_selectedForges.Contains(forge.Position) ? "[x] " : "[ ] ")
                    + (row + 1) + ". " + forge.Name + "  " + forge.Position);
            }
            for (int col = 0; col < MaterialRows; col++)
            {
                bool cellPresent = present && col < _materials.Count;
                SetVisible(_forgeCells[row, col], cellPresent);
                if (!cellPresent) continue;
                ForgeFeedMaterial material = _materials[col];
                bool accepted = _forges[row].Workstation.MaterialNames.Any(name =>
                    string.Equals(name, material.Category, StringComparison.OrdinalIgnoreCase));
                SetText(_forgeCells[row, col], accepted ? ForgeFeedService.CurrentLevel(_forges[row], material).ToString("N0") : "—");
            }
        }
        SetText(GetChildById("forgeFeedSelectAllLabel"), Localization.Get("HyperdimensionalNetworkForgeFeedSelectAll"));
        SetText(GetChildById("forgeFeedInvertLabel"), Localization.Get("HyperdimensionalNetworkForgeFeedInvert"));
        if (!_requestPending && _forges.Count == 0) SetText(_status, Localization.Get("HyperdimensionalNetworkForgeFeedNone"));
        else if (!_requestPending && _materials.Count == 0) SetText(_status, Localization.Get("HyperdimensionalNetworkForgeFeedNoMaterials"));
    }

    private void ToggleForge(int index)
    {
        if (_requestPending || index < 0 || index >= _forges.Count) return;
        Vector3i position = _forges[index].Position;
        if (!_selectedForges.Add(position)) _selectedForges.Remove(position);
        RefreshHighlights();
        NetworkLog.Debug("Forge feed station toggled. player=" + xui?.playerUI?.entityPlayer?.entityId
            + "; position=" + position + "; selected=" + _selectedForges.Contains(position));
        Render();
    }

    private void SelectAllForges()
    {
        if (_requestPending) return;
        _selectedForges.Clear();
        foreach (ForgeFeedStation forge in _forges) _selectedForges.Add(forge.Position);
        RefreshHighlights();
        NetworkLog.Debug("Forge feed selected all stations. count=" + _selectedForges.Count);
        Render();
    }

    private void InvertForges()
    {
        if (_requestPending) return;
        HashSet<Vector3i> previous = new HashSet<Vector3i>(_selectedForges);
        _selectedForges.Clear();
        foreach (ForgeFeedStation forge in _forges)
            if (!previous.Contains(forge.Position)) _selectedForges.Add(forge.Position);
        RefreshHighlights();
        NetworkLog.Debug("Forge feed inverted station selection. count=" + _selectedForges.Count);
        Render();
    }

    private void RefreshHighlights()
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        NavObjectManager manager = NavObjectManager.Instance;
        if (!(bool)player || manager == null) return;
        foreach (Vector3i position in _highlightedForges.Where(position => !_selectedForges.Contains(position)).ToList())
        {
            manager.UnRegisterNavObjectByPosition(new Vector3(position.x + 0.5f, position.y + 1.5f, position.z + 0.5f), "waypoint");
            _highlightedForges.Remove(position);
        }
        foreach (ForgeFeedStation forge in _forges.Where(forge => _selectedForges.Contains(forge.Position)
            && !_highlightedForges.Contains(forge.Position)))
        {
            Vector3 marker = new Vector3(forge.Position.x + 0.5f, forge.Position.y + 1.5f, forge.Position.z + 0.5f);
            try
            {
                manager.RegisterNavObject("waypoint", marker, "ui_game_symbol_forge", false, player.entityId, player);
                _highlightedForges.Add(forge.Position);
            }
            catch (Exception ex) { NetworkLog.Error("Could not add temporary forge highlight. position=" + forge.Position + "; exception=" + ex); }
        }
        manager.RefreshNavObjects();
    }

    private void ClearHighlights()
    {
        NavObjectManager manager = NavObjectManager.Instance;
        if (manager == null) { _highlightedForges.Clear(); return; }
        foreach (Vector3i position in _highlightedForges)
            try { manager.UnRegisterNavObjectByPosition(new Vector3(position.x + 0.5f, position.y + 1.5f, position.z + 0.5f), "waypoint"); }
            catch (Exception ex) { NetworkLog.Error("Could not remove temporary forge highlight. position=" + position + "; exception=" + ex); }
        _highlightedForges.Clear();
        manager.RefreshNavObjects();
    }

    private void Execute()
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (_requestPending || !(bool)player || _forges.Count == 0) return;
        if (!int.TryParse(_fillInput?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fill)
            || fill < 0 || fill > 100000)
        {
            SetText(_status, Localization.Get("HyperdimensionalNetworkForgeFeedInvalidFill"));
            return;
        }
        string[] categories = _materials.Where(material => _selected.Contains(material.Category))
            .Select(material => material.Category).ToArray();
        if (categories.Length == 0)
        {
            SetText(_status, Localization.Get("HyperdimensionalNetworkForgeFeedSelect"));
            return;
        }
        Vector3i[] forgePositions = _forges.Where(forge => _selectedForges.Contains(forge.Position))
            .Select(forge => forge.Position).ToArray();
        if (forgePositions.Length == 0)
        {
            SetText(_status, Localization.Get("HyperdimensionalNetworkForgeFeedSelectForge"));
            return;
        }
        _requestPending = true;
        SetText(_status, Localization.Get("HyperdimensionalNetworkForgeFeedWorking"));
        NetworkLog.Debug("Forge feed requested. player=" + player.entityId + "; selectedForges="
            + string.Join(";", forgePositions) + "; categories=" + string.Join(",", categories)
            + "; fill=" + fill + "; remote=" + NetworkRemoteClient.IsRemoteClient);
        if (NetworkRemoteClient.IsRemoteClient)
        {
            if (!NetworkRemoteClient.RequestForgeFeed(player, categories, forgePositions, fill, OnResult))
            {
                _requestPending = false;
                SetText(_status, Localization.Get("HyperdimensionalNetworkForgeFeedBusy"));
            }
            return;
        }
        NetworkRemoteRequest request = new NetworkRemoteRequest
        {
            EntityId = player.entityId, Operation = NetworkRequestOperation.ForgeFeed,
            Category = string.Join(",", categories), Count = fill, ForgePositions = forgePositions.ToList()
        };
        NetworkRemoteResult result;
        try { result = ForgeFeedService.Execute(GameManager.Instance.World, player, null, request); }
        catch (Exception ex)
        {
            NetworkLog.Error("Local forge feed failed. player=" + player.entityId + "; exception=" + ex);
            result = new NetworkRemoteResult { Error = "ForgeFeedFailed" };
        }
        OnResult(result);
    }

    private void OnResult(NetworkRemoteResult result)
    {
        _requestPending = false;
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (result?.Success == true && result.BagData != null && !NetworkInventorySync.Apply(player, result))
        {
            SetText(_status, Localization.Get("HyperdimensionalNetworkForgeFeedSyncFailed"));
            NetworkLog.Error("Forge feed server succeeded but the client bag snapshot could not be applied. player=" + player?.entityId
                + "; requestId=" + result.RequestId);
            Refresh();
            return;
        }
        if (result?.Success == true)
        {
            SetText(_status, string.Format(Localization.Get("HyperdimensionalNetworkForgeFeedDone"), result.Amount));
            NetworkLog.Debug("Forge feed confirmed. player=" + player?.entityId + "; moved=" + result.Amount + "; requestId=" + result.RequestId);
        }
        else
        {
            SetText(_status, Localization.Get("HyperdimensionalNetworkForgeFeedFailed") + " " + ErrorText(result?.Error));
            NetworkLog.Debug("Forge feed rejected. player=" + player?.entityId + "; error=" + result?.Error);
        }
        Refresh();
    }

    private static void SetText(XUiController controller, string value)
    {
        if (controller?.ViewComponent is XUiV_Label label && label.Text != (value ?? string.Empty))
        { label.Text = value ?? string.Empty; controller.IsDirty = true; }
    }

    private static string ErrorText(string error)
    {
        if (string.IsNullOrWhiteSpace(error)) return string.Empty;
        string key = "HyperdimensionalNetworkForgeFeedError" + error;
        string localized = Localization.Get(key);
        return localized == key ? error : localized;
    }

    private static void SetVisible(XUiController controller, bool visible)
    {
        if (controller?.ViewComponent != null && controller.ViewComponent.IsVisible != visible)
            controller.ViewComponent.IsVisible = visible;
    }
}

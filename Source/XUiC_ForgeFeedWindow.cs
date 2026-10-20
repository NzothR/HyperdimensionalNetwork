using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace HyperdimensionalNetwork;

public sealed class XUiC_ForgeFeedButton : XUiController
{
    public override void Init()
    {
        base.Init();
        OnPress += (sender, mouse) =>
        {
            EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
            if (!(bool)player || XUiM_Recipes.DisableSmelter) return;
            NetworkLog.Debug("Forge feed button pressed. player=" + player.entityId);
            try { xui.playerUI.windowManager.Open("hyperdimensionalForgeFeed", _bModal: true); }
            catch (Exception ex) { NetworkLog.Error("Forge feed panel could not open. player=" + player.entityId + "; exception=" + ex); }
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
    private readonly XUiController[] _forgeNames = new XUiController[ForgeRows];
    private readonly XUiController[,] _forgeCells = new XUiController[ForgeRows, MaterialRows];
    private readonly HashSet<string> _selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private List<ForgeFeedStation> _forges = new List<ForgeFeedStation>();
    private List<ForgeFeedMaterial> _materials = new List<ForgeFeedMaterial>();
    private XUiC_TextInput _fillInput;
    private XUiController _status;
    private bool _requestPending;
    private float _nextRefresh;

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
            _forgeNames[row] = GetChildById("forgeFeedName" + row);
            for (int col = 0; col < MaterialRows; col++)
                _forgeCells[row, col] = GetChildById("forgeFeedCell" + row + "_" + col);
        }
        Bind("btnForgeFeedClose", (_, _) => xui.playerUI.windowManager.Close("hyperdimensionalForgeFeed"));
        Bind("btnForgeFeedExecute", (_, _) => Execute());
        int[] presets = { 1000, 2500, 5000, 10000, 0 };
        for (int i = 0; i < presets.Length; i++)
        {
            int fill = presets[i];
            Bind("btnForgeFeedPreset" + i, (_, _) => { if (_fillInput != null) _fillInput.Text = fill.ToString(CultureInfo.InvariantCulture); Render(); });
        }
    }

    private void Bind(string id, Action<XUiController, int> handler)
    {
        XUiController button = GetChildById(id);
        if (button != null) button.OnPress += (sender, mouse) => handler(sender, mouse);
    }

    public override void OnOpen()
    {
        base.OnOpen();
        if (_fillInput != null && string.IsNullOrWhiteSpace(_fillInput.Text))
            _fillInput.Text = ForgeFeedService.DefaultFill.ToString(CultureInfo.InvariantCulture);
        Refresh();
        if (_forges.Count > 0 && _materials.Count > 0)
            SetText(_status, Localization.Get("HyperdimensionalNetworkForgeFeedHelp"));
        NetworkLog.Debug("Forge feed panel opened. nearby=" + _forges.Count + "; materials=" + _materials.Count);
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
                    network += NetworkStorage.GetEntriesForRequirements(player,
                        new[] { new ItemStack(new ItemValue(raw.Id), 1) })
                        .Where(entry => entry.Value.type == raw.Id && ForgeFeedService.IsPlainRaw(entry.Value))
                        .Sum(entry => entry.Count);
                }
            SetText(_materialSupply[col], "包 " + bag + " / 网 " + network);
        }
        for (int row = 0; row < ForgeRows; row++)
        {
            bool present = row < _forges.Count;
            SetVisible(_forgeNames[row], present);
            if (present)
            {
                ForgeFeedStation forge = _forges[row];
                SetText(_forgeNames[row], (row + 1) + ". " + forge.Name + "  " + forge.Position);
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
        if (!_requestPending && _forges.Count == 0) SetText(_status, Localization.Get("HyperdimensionalNetworkForgeFeedNone"));
        else if (!_requestPending && _materials.Count == 0) SetText(_status, Localization.Get("HyperdimensionalNetworkForgeFeedNoMaterials"));
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
        _requestPending = true;
        SetText(_status, Localization.Get("HyperdimensionalNetworkForgeFeedWorking"));
        NetworkLog.Debug("Forge feed requested. player=" + player.entityId + "; nearby=" + _forges.Count
            + "; categories=" + string.Join(",", categories) + "; fill=" + fill + "; remote=" + NetworkRemoteClient.IsRemoteClient);
        if (NetworkRemoteClient.IsRemoteClient)
        {
            if (!NetworkRemoteClient.RequestForgeFeed(player, categories, fill, OnResult))
            {
                _requestPending = false;
                SetText(_status, Localization.Get("HyperdimensionalNetworkForgeFeedBusy"));
            }
            return;
        }
        NetworkRemoteRequest request = new NetworkRemoteRequest
        {
            EntityId = player.entityId, Operation = NetworkRequestOperation.ForgeFeed,
            Category = string.Join(",", categories), Count = fill
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

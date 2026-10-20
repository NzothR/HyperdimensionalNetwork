using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HyperdimensionalNetwork;

internal static class BookAutomationClient
{
    private readonly struct Target
    {
        public readonly bool Toolbelt;
        public readonly int Slot;
        public Target(bool toolbelt, int slot) { Toolbelt = toolbelt; Slot = slot; }
        public override string ToString() => (Toolbelt ? "T" : "B") + Slot;
    }
    private static readonly Queue<Target> Pending = new Queue<Target>();
    private static readonly HashSet<string> Queued = new HashSet<string>(StringComparer.Ordinal);
    private static int _playerId = -1;
    private static bool _remoteSalePending;

    internal static void OnNetworkChanged(EntityPlayerLocal player)
    {
        Pending.Clear(); Queued.Clear(); _playerId = player?.entityId ?? -1;
        _remoteSalePending = false;
        NetworkLog.Debug("Automatic book watch reset; waiting for new inventory changes. player=" + _playerId);
    }

    internal static void OnSlotChanged(EntityPlayerLocal player, bool toolbelt, int slot, ItemStack previous, ItemStack current)
    {
        if ((bool)player && _playerId != player.entityId) OnNetworkChanged(player);
        if (!(bool)player || current == null || current.IsEmpty() || !IsBook(current.itemValue)) return;
        bool newItem = previous == null || previous.IsEmpty() || !previous.itemValue.Equals(current.itemValue);
        if (newItem || current.count > previous.count) QueueSlot(toolbelt, slot);
    }

    internal static bool ShouldOwnItem(EntityPlayerLocal player, ItemValue value)
    {
        RestockSettingsSnapshot settings = AutoRestockClient.CurrentSettings(player);
        return settings?.BookReadingEnabled == true && settings.PersonalAutoRead && IsBook(value);
    }

    internal static void Update(EntityPlayerLocal player)
    {
        if (!(bool)player) return;
        if (_playerId != player.entityId) OnNetworkChanged(player);
        RestockSettingsSnapshot settings = AutoRestockClient.CurrentSettings(player);
        if (settings == null || !settings.BookReadingEnabled || !settings.PersonalAutoRead || Pending.Count == 0
            || _remoteSalePending || NetworkRemoteClient.IsMutationPending || player.Progression == null
            || player.IsDead() || !player.Spawned || player.inventory.IsHoldingItemActionRunning()) return;
        Target target = Pending.Dequeue(); Queued.Remove(target.ToString());
        ItemStackGrid grid = target.Toolbelt ? player.inventory?.ItemGrid : player.bag?.ItemGrid;
        if (grid == null || target.Slot < 0 || target.Slot >= grid.Length) return;
        ItemStack stack = grid[target.Slot];
        if (stack == null || stack.IsEmpty() || !TryGetBookState(player, stack.itemValue, out bool unread)) return;
        try
        {
            if (unread || settings.ReadBookPolicy == ReadBookPolicy.ReadAll)
            {
                if (ReadPlayerSlot(player, target.Toolbelt, target.Slot, stack,
                    settings.ReadBookPolicy == ReadBookPolicy.ReadAll, 8) > 0) QueueSlot(target.Toolbelt, target.Slot);
                return;
            }
            if (settings.ReadBookPolicy == ReadBookPolicy.Scrap)
            {
                if (TryScrapPlayerSlot(player, target.Toolbelt, target.Slot, stack))
                    NetworkLog.Debug("Automatic read book sent an already-read stack through the vanilla scrap queue. player=" + player.entityId
                        + "; source=" + target + "; item=" + stack.itemValue.type + "; count=" + stack.count);
                return;
            }
            if (settings.ReadBookPolicy == ReadBookPolicy.Sell && settings.TraderEnabled && TraderTransactionService.CanSell(stack.itemValue))
                SellPlayerSlot(player, target, stack);
        }
        catch (Exception ex) { NetworkLog.Error("Automatic book processing failed safely. player=" + player.entityId + "; source=" + target + "; exception=" + ex); }
    }

    internal static bool IsBook(ItemValue value)
    {
        ItemClass item = value?.ItemClass;
        if (item == null) return false;
        if (item.Actions != null && item.Actions.Any(action => action is ItemActionLearnRecipe)) return true;
        if (string.IsNullOrEmpty(item.Unlocks) || item.Actions == null || item.Actions.Length == 0
            || item.Actions[0] is not ItemActionEat) return false;
        return item.HasAnyTags(FastTags<TagGroup.Global>.Parse("books"))
            || item.Groups?.Any(group => string.Equals(group, "Books", StringComparison.OrdinalIgnoreCase)
                || string.Equals(group, "SchematicsOnly", StringComparison.OrdinalIgnoreCase)) == true;
    }

    internal static bool TryGetBookState(EntityPlayer player, ItemValue value, out bool unread)
    {
        unread = false;
        if (!(bool)player || !IsBook(value)) return false;
        ItemClass item = value.ItemClass;
        ItemActionLearnRecipe learn = item.Actions?.OfType<ItemActionLearnRecipe>().FirstOrDefault();
        if (learn != null)
        {
            unread = learn.RecipesToLearn != null && learn.RecipesToLearn.Any(name =>
                !string.IsNullOrWhiteSpace(name) && player.GetCVar(name) == 0f);
            return true;
        }
        ProgressionValue progression = player.Progression?.GetProgressionValue(item.Unlocks);
        if (progression?.ProgressionClass != null
            && ((int)progression.ProgressionClass.Type == 5 || (int)progression.ProgressionClass.Type == 6))
        {
            unread = progression.Level < progression.ProgressionClass.MaxLevel;
            return true;
        }
        unread = player.GetCVar(item.Unlocks) == 0f;
        return true;
    }

    internal static int RemainingUnread(EntityPlayer player, ItemValue value)
    {
        if (!TryGetBookState(player, value, out bool unread) || !unread) return 0;
        ItemClass item = value.ItemClass;
        if (item.Actions?.OfType<ItemActionLearnRecipe>().Any() == true) return 1;
        ProgressionValue progression = player.Progression?.GetProgressionValue(item.Unlocks);
        if (progression?.ProgressionClass != null
            && ((int)progression.ProgressionClass.Type == 5 || (int)progression.ProgressionClass.Type == 6))
            return Math.Max(0, progression.ProgressionClass.MaxLevel - progression.Level);
        return 1;
    }

    internal static int ReadPlayerSlot(EntityPlayerLocal player, bool toolbelt, int slot, ItemStack stack,
        bool readAll, int budget)
    {
        if (!(bool)player || stack == null || stack.IsEmpty() || !IsBook(stack.itemValue)) return 0;
        ItemStackGrid grid = toolbelt ? player.inventory.ItemGrid : player.bag.ItemGrid;
        if (slot < 0 || slot >= grid.Length || !grid[slot].itemValue.Equals(stack.itemValue) || grid[slot].count < 1) return 0;
        int read = 0;
        using (AutoRestockClient.SuppressInventoryEvents(player))
        {
            while (read < budget)
            {
                ItemStack current = grid[slot];
                if (current == null || current.IsEmpty() || !current.itemValue.Equals(stack.itemValue)
                    || !TryGetBookState(player, current.itemValue, out bool unread) || !readAll && !unread) break;
                ApplyReadEffects(player, current.itemValue.Clone(), () =>
                {
                    ItemStack remaining = grid[slot].Clone(); remaining.count--;
                    if (toolbelt) player.inventory.SetItem(slot, remaining.count > 0 ? remaining : ItemStack.Empty.Clone());
                    else player.bag.SetSlot(slot, remaining.count > 0 ? remaining : ItemStack.Empty.Clone());
                });
                read++;
            }
        }
        if (read > 0)
        {
            AutoRestockClient.RefreshBaseline(player);
            AutoRestockClient.RefreshInventoryViews(player, refreshToolbelt: toolbelt);
            NetworkLog.Debug("Books read from player inventory. player=" + player.entityId + "; item=" + stack.itemValue.type
                + "; toolbelt=" + toolbelt + "; slot=" + slot + "; count=" + read);
        }
        return read;
    }

    internal static void ApplyReadEffects(EntityPlayerLocal player, ItemValue value, Action consume)
    {
        ItemClass item = value.ItemClass;
        ItemActionLearnRecipe learn = item?.Actions?.OfType<ItemActionLearnRecipe>().FirstOrDefault();
        if (learn?.RecipesToLearn != null)
        {
            FastTags<TagGroup.Global> learnable = FastTags<TagGroup.Global>.Parse("learnable");
            foreach (string name in learn.RecipesToLearn)
            {
                Recipe recipe = string.IsNullOrWhiteSpace(name) ? null : CraftingManager.GetRecipe(name);
                if (recipe != null && recipe.tags.Equals(learnable) && player.GetCVar(name) == 0f)
                    player.SetCVar(name, 1f);
            }
        }
        MinEventParams context = player.MinEventContext;
        MinEventParams saved = new MinEventParams();
        MinEventParams.CopyTo(context, saved);
        saved.ParentType = context.ParentType; saved.Challenge = context.Challenge; saved.IsLocal = context.IsLocal;
        try
        {
            context.ItemValue = value;
            value.FireEvent((MinEventTypes)24, context);
            player.FireEvent((MinEventTypes)24, false);
            consume?.Invoke();
            value.FireEvent((MinEventTypes)29, context);
            player.FireEvent((MinEventTypes)29, false);
            QuestEventManager.Current?.UsedItem(value);
        }
        finally
        {
            MinEventParams.CopyTo(saved, context);
            context.ParentType = saved.ParentType; context.Challenge = saved.Challenge; context.IsLocal = saved.IsLocal;
        }
    }

    internal static bool TryScrapPlayerSlot(EntityPlayerLocal player, bool toolbelt, int slot, ItemStack stack)
    {
        if (CraftingManager.GetScrapableRecipe(stack.itemValue, stack.count) == null) return false;
        XUiC_ItemStackGrid uiGrid = toolbelt
            ? player.playerUI?.xui?.GetChildByType<XUiC_Toolbelt>()
            : player.playerUI?.xui?.GetChildByType<XUiC_Backpack>();
        XUiC_ItemStack[] controllers = uiGrid?.GetItemStackControllers();
        if (controllers == null || slot < 0 || slot >= controllers.Length) return false;
        XUiC_ItemStack controller = controllers[slot];
        if (controller?.ItemStack == null || controller.ItemStack.IsEmpty()
            || !controller.ItemStack.itemValue.Equals(stack.itemValue)) return false;
        int before = controller.ItemStack.count;
        new ItemActionEntryScrap(controller).OnActivated();
        bool queued = controller.ItemStack == null || controller.ItemStack.IsEmpty() || controller.ItemStack.count < before;
        if (queued) AutoRestockClient.RefreshBaseline(player);
        return queued;
    }

    private static void SellPlayerSlot(EntityPlayerLocal player, Target target, ItemStack stack)
    {
        if (NetworkRemoteClient.IsRemoteClient)
        {
            _remoteSalePending = true;
            bool sent = NetworkRemoteClient.RequestTraderVoidSell(player, stack.itemValue, stack.count,
                networkSource: false, personalPayout: true,
                target.Toolbelt ? XUiC_ItemStack.StackLocationTypes.ToolBelt : XUiC_ItemStack.StackLocationTypes.Backpack,
                target.Slot, result =>
                {
                    _remoteSalePending = false;
                    if (result.Success)
                    {
                        NetworkInventorySync.Apply(player, result, suppressRestockEvents: true);
                        AutoRestockClient.RefreshInventoryViews(player, refreshToolbelt: target.Toolbelt);
                        NetworkLog.Debug("Automatic read book sold into player backpack. player=" + player.entityId
                            + "; source=" + target + "; item=" + stack.itemValue.type + "; proceeds=" + result.Amount);
                    }
                    else NetworkLog.Debug("Automatic read book sale skipped. player=" + player.entityId + "; error=" + result.Error);
                }, automation: true);
            if (!sent) _remoteSalePending = false;
            return;
        }
        NetworkRemoteResult sold = TraderTransactionService.ExecuteVoidSell(player, null, new NetworkRemoteRequest
        {
            EntityId = player.entityId, RequestId = DateTime.UtcNow.Ticks, Operation = NetworkRequestOperation.TraderVoidSell,
            Value = stack.itemValue.Clone(), Count = stack.count, SourceLocation = target.Toolbelt
                ? XUiC_ItemStack.StackLocationTypes.ToolBelt : XUiC_ItemStack.StackLocationTypes.Backpack,
            Slot = target.Slot, TraderPersonalPayout = true, TraderAutomation = true
        });
        if (sold.Success)
        {
            AutoRestockClient.RefreshBaseline(player);
            AutoRestockClient.RefreshInventoryViews(player, refreshToolbelt: target.Toolbelt);
        }
        else NetworkLog.Debug("Local automatic read book sale skipped. player=" + player.entityId + "; error=" + sold.Error);
    }

    private static void QueueSlot(bool toolbelt, int slot)
    {
        Target target = new Target(toolbelt, slot);
        if (Queued.Add(target.ToString())) Pending.Enqueue(target);
    }
}

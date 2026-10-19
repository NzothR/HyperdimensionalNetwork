using System;
using System.Collections.Generic;
using System.Linq;

namespace HyperdimensionalNetwork;

internal sealed class TraderCreativeCategory
{
    public string Name;
    public string FilterType;
    public string FilterText;
    public string DisplayName;
    public string Icon;
}

internal static class TraderCreativeCategories
{
    private static readonly List<TraderCreativeCategory> Definitions = CreateDefinitions();
    private static readonly Dictionary<string, TraderCreativeCategory> ByName = Definitions.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<TraderCreativeCategory> All => Definitions;

    public static TraderCreativeCategory Find(string name)
        => !string.IsNullOrEmpty(name) && ByName.TryGetValue(name, out TraderCreativeCategory category) ? category : null;

    public static bool Matches(ItemValue value, string name)
    {
        TraderCreativeCategory category = Find(name);
        if (category == null || value?.ItemClass == null) return false;
        ItemClass itemClass = value.ItemClass;
        Block block = itemClass.IsBlock() ? itemClass.GetBlock()
            : value.type > 0 && value.type < Block.ItemsStartHere && value.type < Block.list.Length ? Block.list[value.type] : null;
        if (category.FilterType == "itemgroups")
        {
            if (string.IsNullOrEmpty(category.FilterText)) return block == null;
            if (block != null) return false;
            if (category.FilterText == "Ranged Weapons" && itemClass.ItemTags.Test_AnySet(FastTags<TagGroup.Global>.Parse("ammo"))) return false;
            return itemClass.Groups != null && itemClass.Groups.Any(group => string.Equals(group, category.FilterText, StringComparison.OrdinalIgnoreCase));
        }
        if (category.FilterType == "blockfiltertags")
            return block?.FilterTags != null && block.FilterTags.Any(tag => string.Equals(tag, category.FilterText, StringComparison.OrdinalIgnoreCase));
        if (category.FilterType == "autoshapes")
        {
            if (block == null) return false;
            string helperName = category.FilterText + ":" + ShapesFromXml.VariantHelperName;
            Block helper = Block.GetBlockByName(helperName);
            return helper != null && (block == helper || block.GetAutoShapeHelperBlock() == helper);
        }
        return false;
    }

    private static List<TraderCreativeCategory> CreateDefinitions()
    {
        List<TraderCreativeCategory> categories = new List<TraderCreativeCategory>();
        Add(categories, "itemgroups", "", "xuiItems", "ui_game_symbol_backpack");
        Add(categories, "blockfiltertags", "MC_building", "xuiPropsBuilding", "ui_game_symbol_chair");
        Add(categories, "blockfiltertags", "MC_outdoor", "xuiPropsOutdoor", "ui_game_symbol_tree");
        Add(categories, "blockfiltertags", "MC_helpers", "xuiHelpers", "ui_game_symbol_tool_smithing");
        Add(categories, "blockfiltertags", "MC_playerBlocks", "xuiPlayerBlocks", "ui_game_symbol_player");
        AddNamed(categories, "autoshapes", "woodShapes", "木制形状", "ui_game_symbol_wood");
        AddNamed(categories, "autoshapes", "brickShapes", "砖制形状", "ui_game_symbol_brick");
        AddNamed(categories, "autoshapes", "corrugatedMetalShapes", "波纹金属形状", "ui_game_symbol_forge");
        AddNamed(categories, "autoshapes", "concreteShapes", "混凝土形状", "ui_game_symbol_cement");
        AddNamed(categories, "autoshapes", "steelShapes", "钢制形状", "ui_game_symbol_ibeam");
        AddNamed(categories, "autoshapes", "awningShapes", "遮阳棚形状", "ui_game_symbol_map_campsite");
        AddNamed(categories, "autoshapes", "bulletproofShapes", "防弹形状", "ui_game_symbol_windows");
        Add(categories, "itemgroups", "Resources", "lblCategoryResources", "ui_game_symbol_resource");
        Add(categories, "itemgroups", "Tools/Traps", "lblCategoryTools", "ui_game_symbol_tool");
        Add(categories, "itemgroups", "Melee Weapons", "lblCategoryMeleeWeapons", "ui_game_symbol_knife");
        Add(categories, "itemgroups", "Ranged Weapons", "lblCategoryRangeWeapons", "ui_game_symbol_rifle");
        Add(categories, "itemgroups", "Tool/Weapon Mods", "lblCategoryToolWeaponMods", "ui_game_symbol_assemble");
        Add(categories, "itemgroups", "Robotics", "lblCategoryRobotics", "ui_game_symbol_junk_turret");
        Add(categories, "itemgroups", "Ammo", "lblCategoryAmmo", "ui_game_symbol_shape_ammo");
        Add(categories, "itemgroups", "Armor", "lblCategoryArmor", "ui_game_symbol_armor_iron");
        Add(categories, "itemgroups", "Food/Cooking", "lblCategoryFoodCooking", "ui_game_symbol_fork");
        Add(categories, "itemgroups", "Science", "lblCategoryScience", "ui_game_symbol_science");
        Add(categories, "itemgroups", "BooksOnly", "lblCategoryBooksOnly", "ui_game_symbol_book");
        Add(categories, "itemgroups", "SchematicsOnly", "lblCategorySchematicsOnly", "ui_game_symbol_shape_schematics");
        Add(categories, "itemgroups", "Special Items", "lblCategorySpecialItems", "ui_game_symbol_quest");
        Add(categories, "blockfiltertags", "SC_construction", "xuifConstruction", "ui_game_symbol_resource");
        Add(categories, "blockfiltertags", "SC_destruction", "xuifDestruction", "ui_game_symbol_destruction");
        Add(categories, "blockfiltertags", "SC_electrical", "xuifElectrical", "ui_game_symbol_electric_plugin");
        Add(categories, "blockfiltertags", "SC_lighting", "xuifLighting", "ui_game_symbol_lightbulb");
        Add(categories, "blockfiltertags", "SC_decor", "xuifDecor", "ui_game_symbol_chair");
        Add(categories, "blockfiltertags", "SC_industrial", "xuifIndustrial", "ui_game_symbol_shape_factory");
        Add(categories, "blockfiltertags", "SC_commercial", "xuifCommercial", "ui_game_symbol_map_trader");
        Add(categories, "blockfiltertags", "SC_residential", "xuifResidential", "ui_game_symbol_map_house");
        Add(categories, "blockfiltertags", "SC_loot", "xuifLoot", "ui_game_symbol_loot_sack");
        Add(categories, "blockfiltertags", "SC_automotive", "xuifAutomotive", "ui_game_symbol_service");
        Add(categories, "blockfiltertags", "SC_plumbing", "xuifPlumbing", "ui_game_symbol_wrench");
        Add(categories, "blockfiltertags", "SC_doors", "xuifDoors", "ui_game_symbol_door");
        Add(categories, "blockfiltertags", "SC_windows", "xuifWindows", "ui_game_symbol_windows");
        Add(categories, "blockfiltertags", "SC_traps", "xuifTraps", "ui_game_symbol_traps");
        Add(categories, "blockfiltertags", "SC_fences", "xuifFences", "ui_game_symbol_shape_outdoor_props");
        Add(categories, "blockfiltertags", "SC_trees", "xuifTrees", "ui_game_symbol_tree");
        Add(categories, "blockfiltertags", "SC_shrubbery", "xuifFoliage", "ui_game_symbol_foliage");
        Add(categories, "blockfiltertags", "SC_crops", "xuifCrops", "ui_game_symbol_crops");
        Add(categories, "blockfiltertags", "SC_terrain", "xuifTerrain", "ui_game_symbol_map_cave");
        Add(categories, "blockfiltertags", "SC_signs", "xuifSigns", "ui_game_symbol_signs");
        Add(categories, "blockfiltertags", "SC_helperIndustrial", "xuifIndustrialHelpers", "ui_game_symbol_shape_factory");
        Add(categories, "blockfiltertags", "SC_helperCommercial", "xuifCommercialHelpers", "ui_game_symbol_map_trader");
        Add(categories, "blockfiltertags", "SC_helperResidential", "xuifResidentialHelpers", "ui_game_symbol_map_house");
        Add(categories, "blockfiltertags", "SC_helperOutdoor", "xuifOutdoorHelpers", "ui_game_symbol_tree");
        Add(categories, "blockfiltertags", "SC_helperElectrical", "xuifElectricalHelpers", "ui_game_symbol_electric_plugin");
        Add(categories, "blockfiltertags", "SC_helperSleeper", "xuifSleeperHelpers", "ui_game_symbol_zombie");
        Add(categories, "blockfiltertags", "SC_treasureRoom", "xuifLootRoom", "ui_game_symbol_treasure");
        Add(categories, "blockfiltertags", "SC_questblocks", "xuifQuestBlocks", "ui_game_symbol_quest");
        return categories;
    }

    private static void Add(List<TraderCreativeCategory> categories, string filterType, string filterText, string displayKey, string icon)
        => AddNamed(categories, filterType, filterText, Localization.Get(displayKey), icon);

    private static void AddNamed(List<TraderCreativeCategory> categories, string filterType, string filterText, string displayName, string icon)
    {
        string name = "cm:" + filterType + ":" + filterText;
        categories.Add(new TraderCreativeCategory
        {
            Name = name, FilterType = filterType, FilterText = filterText,
            DisplayName = displayName, Icon = icon
        });
    }
}

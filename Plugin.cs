using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using UnityEngine;
using System.Collections.Generic;

namespace SinglesSlinger
{
    [BepInPlugin("com.kritterbizkit.singlesslinger", "SinglesSlinger", "1.8.7")]
    public class Plugin : BaseUnityPlugin
    {
        internal static Dictionary<ECardExpansionType, ConfigEntry<bool>> EnabledExpansions;

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> SkipVintageTables;

        private readonly Harmony harmony = new Harmony("com.kritterbizkit.singlesslinger");

        internal static ConfigEntry<float> SellOnlyGreaterThanMP;
        internal static ConfigEntry<float> SellOnlyLessThanMP;
        internal static ConfigEntry<KeyboardShortcut> SetOutCardsKey;
        internal static ConfigEntry<int> KeepCardQty;
        internal static ConfigEntry<int> GradedKeepCardQty;
        internal static ConfigEntry<bool> ShowProgressPopUp;
        internal static ConfigEntry<bool> OnlyPlaceMostExpensive;

        internal static ConfigEntry<bool> ShouldSellTetramonCards;
        internal static ConfigEntry<bool> ShouldSellDestinyCards;
        internal static ConfigEntry<bool> ShouldSellGhostCards;
        internal static ConfigEntry<bool> ShouldSellDestinyGhostCards;

        internal static ConfigEntry<bool> TriggerOnCustomerCardPickup;
        internal static ConfigEntry<bool> TriggerOnDayStart;

        internal static ConfigEntry<bool> TryTriggerAutoSetPricesMod;
        internal static ConfigEntry<KeyboardShortcut> SetOutGradedCardsKey;
        internal static ConfigEntry<bool> GradedOnlyToVintageTable;

        internal static ConfigEntry<float> GradedSellOnlyGreaterThanMP;
        internal static ConfigEntry<float> GradedSellOnlyLessThanMP;


        private void Awake()
        {
            Log = base.Logger;

            InitConfig();
            harmony.PatchAll();

            Log.LogInfo("SinglesSlinger loaded!");
        }

        private void OnDestroy()
        {
            StaticCoroutine.StopAll();
            harmony.UnpatchSelf();
            Log.LogInfo("SinglesSlinger unloaded!");
        }

        private void InitConfig()
        {
            OnlyPlaceMostExpensive = Config.Bind(
    "Placement",
    "Only Place Most Expensive",
    false,
    "If enabled, SinglesSlinger will always place the highest market price eligible card available."
);


            SellOnlyGreaterThanMP = Config.Bind("General", "SellOnlyGreaterThan", 0.5f,
                "Ignore cards in the album with a market value below this.");

            SellOnlyLessThanMP = Config.Bind("General", "SellOnlyLessThan", 100f,
                "Ignore cards in the album with a market value above this.");

            SetOutCardsKey = Config.Bind("General", "SetOutCardsKey",
                new KeyboardShortcut(KeyCode.F9),
                "Keyboard shortcut to set out cards.");

            

            // Graded cards use their own price window
            GradedSellOnlyGreaterThanMP = Config.Bind("Graded", "SellOnlyGreaterThan", 0.5f,
                "Ignore graded cards with a market value below this.");

            GradedSellOnlyLessThanMP = Config.Bind("Graded", "SellOnlyLessThan", 2000f,
                "Ignore graded cards with a market value above this.");



            SetOutGradedCardsKey = Config.Bind(
    "General",
    "SetOutGradedCardsKey",
    new KeyboardShortcut(KeyCode.F10),
    "Keyboard shortcut to set out graded cards."
);

            KeepCardQty = Config.Bind("General", "KeepCardQty", 0,
                "Keep at least this many duplicates in the album");

            GradedKeepCardQty = Config.Bind(
                "Graded",
                "KeepCardQty",
                0,
                "Keep at least this many duplicates of each graded card in the album. This is separate from ungraded KeepCardQty."
                );

            ShowProgressPopUp = Config.Bind("General", "ShowPopUpForNumCardsSet", false,
                "When triggered, show info about how many cards matched and how many were placed.");

            EnabledExpansions = new Dictionary<ECardExpansionType, ConfigEntry<bool>>();

            foreach (ECardExpansionType expansion in Enum.GetValues(typeof(ECardExpansionType)))
            {
                if (expansion == ECardExpansionType.None)
                    continue;

                EnabledExpansions[expansion] = Config.Bind(
                    "Filters - Expansions",
                    $"Enable {expansion} Cards",
                    true,
                    $"Allow SinglesSlinger to place cards from the {expansion} expansion."
                );
            }



            TriggerOnCustomerCardPickup = Config.Bind("Triggers", "ShouldTriggerOnCardPickup", false,
                "Automatically fill shelves when a customer picks up a card?");

            TriggerOnDayStart = Config.Bind("Triggers", "ShouldTriggerOnDayStart", true,
                "Automatically fill shelves when the day begins?");

            TryTriggerAutoSetPricesMod = Config.Bind("Mod_Integration", "ShouldTriggerAutoSetPricesMod", true,
                "If Auto Set Prices mod is installed, ask it to set the price of cards placed on shelves.");

            SkipVintageTables = Config.Bind(
    "Placement",
    "Skip Vintage Tables",
    true,
    "If enabled, SinglesSlinger will not place cards on vintage card tables."
);

            GradedOnlyToVintageTable = Config.Bind(
    "Placement",
    "Graded Only To Vintage Table",
    true,
    "If enabled, SinglesSlinger will only place graded cards onto vintage card tables."
);

        }
    }
}

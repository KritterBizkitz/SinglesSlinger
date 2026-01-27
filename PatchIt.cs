using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;

namespace SinglesSlinger
{
    [HarmonyPatch]
    public static class PatchIt
    {
        private static bool isRunning;

        private static List<CardData> GetCompatibleCards(ECardExpansionType expansionType, bool findGhostDimensionCards = false)
        {
            var results = new List<CardData>();
            int keepQty = Plugin.KeepCardQty.Value;

            List<int> collected = CPlayerData.GetCardCollectedList(expansionType, findGhostDimensionCards);

            for (int i = 0; i < collected.Count; i++)
            {
                int ownedQty = collected[i];
                if (ownedQty <= keepQty) continue;

                try
                {
                    CardData cardData = CPlayerData.GetCardData(i, expansionType, findGhostDimensionCards);
                    float mp = CPlayerData.GetCardMarketPrice(cardData);

                    if (mp > Plugin.SellOnlyGreaterThanMP.Value && mp < Plugin.SellOnlyLessThanMP.Value)
                    {
                        results.Add(cardData);
                        Plugin.Log.LogInfo("Adding " + cardData.monsterType + " MP=" + mp + " Qty=" + ownedQty);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError(ex);
                }
            }

            return results;
        }

        private static async Task<List<CardData>> GetCompatibleCards()
        {
            return await Task.Run(() =>
            {
                var all = new List<CardData>();

                foreach (var kvp in Plugin.EnabledExpansions)
                {
                    if (!kvp.Value.Value)
                        continue;

                    ECardExpansionType expansion = kvp.Key;

                    Plugin.Log.LogInfo("Expansion enabled. Searching: " + expansion);
                    all.AddRange(GetCompatibleCards(expansion, false));
                }
                if (Plugin.EnabledExpansions.TryGetValue(ECardExpansionType.Ghost, out var ghostEnabled) && ghostEnabled.Value)
                {
                    Plugin.Log.LogInfo("Ghost enabled. Also searching Ghost dimension cards...");
                    all.AddRange(GetCompatibleCards(ECardExpansionType.Ghost, true));
                }


                return all;
            });
        }

        private static async Task<int> CountMatchingCards(List<CardData> allMatchingCards)
        {
            return await Task.Run(() =>
            {
                int totalPlaceable = 0;
                int keepQty = Plugin.KeepCardQty.Value;

                foreach (CardData cardData in allMatchingCards)
                {
                    int owned = CPlayerData.GetCardAmount(cardData);
                    if (owned > keepQty) totalPlaceable += (owned - keepQty);
                }

                return totalPlaceable;
            });
        }

        private static IEnumerator SetCardsOnShelfPerFrame(List<CardData> allCardsSorted, int totalMatchingCards)
        {
            int placedCards = 0;
            List<CardShelf> shelves = CSingleton<ShelfManager>.Instance.m_CardShelfList;

            foreach (CardShelf shelf in shelves)
            {
                List<InteractableCardCompartment> compartments = shelf.GetCardCompartmentList();
                yield return null;

                for (int i = 0; i < compartments.Count; i++)
                {
                    InteractableCardCompartment cardCompart = compartments[i];

                    if (cardCompart.m_StoredCardList.Count == 0 && !cardCompart.m_ItemNotForSale && !shelf.GetIsBoxedUp())
                    {
                        CardData cardData = allCardsSorted.FirstOrDefault();

                        if (cardData != null && cardData.monsterType != EMonsterType.None)
                        {
                            Card3dUIGroup cardUI = CSingleton<Card3dUISpawner>.Instance.GetCardUI();
                            InteractableCard3d card3d = ShelfManager.SpawnInteractableObject(EObjectType.Card3d).GetComponent<InteractableCard3d>();

                            cardUI.m_IgnoreCulling = true;
                            cardUI.m_CardUI.SetFoilCullListVisibility(true);
                            cardUI.m_CardUI.ResetFarDistanceCull();
                            cardUI.m_CardUI.SetCardUI(cardData);

                            cardUI.transform.position = card3d.transform.position;
                            cardUI.transform.rotation = card3d.transform.rotation;

                            card3d.SetCardUIFollow(cardUI);
                            card3d.SetEnableCollision(false);

                            cardCompart.SetCardOnShelf(card3d);
                            cardUI.m_IgnoreCulling = false;

                            CPlayerData.ReduceCard(cardData, 1);

                            int keepQty = Plugin.KeepCardQty.Value;
                            if (CPlayerData.GetCardAmount(cardData) == keepQty)
                                allCardsSorted.Remove(cardData);

                            placedCards++;
                            yield return null;

                            if (Plugin.TryTriggerAutoSetPricesMod.Value)
                                TryTellAutoSetPrices(cardCompart);

                            yield return null;
                        }
                    }

                    yield return null;
                }

                yield return null;
            }

            isRunning = false;

            if (Plugin.ShowProgressPopUp.Value)
            {
                string text = totalMatchingCards == 0
                    ? "SinglesSlinger: No cards matching configured filters!"
                    : placedCards + " cards of " + totalMatchingCards + " possible matching cards placed.";

                var popup = CSingleton<NotEnoughResourceTextPopup>.Instance;
                for (int j = 0; j < popup.m_ShowTextGameObjectList.Count; j++)
                {
                    if (!popup.m_ShowTextGameObjectList[j].activeSelf)
                    {
                        popup.m_ShowTextList[j].text = text;
                        popup.m_ShowTextGameObjectList[j].gameObject.SetActive(true);
                        break;
                    }
                }
            }
        }

        private static async void DoShelfPut()
        {
            if (isRunning) return;

            isRunning = true;

            List<CardData> allCardsSorted = await GetCompatibleCards();
            int totalMatchingCards = await CountMatchingCards(allCardsSorted);

            StaticCoroutine.Start(SetCardsOnShelfPerFrame(allCardsSorted, totalMatchingCards));
        }

        private static void TryTellAutoSetPrices(InteractableCardCompartment cardCompart)
        {
            try
            {
                var t = AccessTools.TypeByName("AutoSetPrices.AllPatchs");
                if (t == null) return;

                var m = AccessTools.Method(t, "CardCompartOnMouseButtonUpPostfix");
                if (m == null) return;

                object[] args = new object[] { cardCompart };
                m.Invoke(null, args);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("AutoSetPrices hook failed:\r\n" + ex);
            }
        }

        [HarmonyPatch(typeof(Customer), "TakeCardFromShelf")]
        [HarmonyPostfix]
        private static void OnCustomerTakeCardFromShelfPostFix(Customer __instance, InteractableCardCompartment ___m_CurrentCardCompartment)
        {
            if (!Plugin.TriggerOnCustomerCardPickup.Value) return;
            if (___m_CurrentCardCompartment == null) return;

            if (___m_CurrentCardCompartment.m_StoredCardList.Count < 1)
                DoShelfPut();
        }

        [HarmonyPatch(typeof(PriceChangeManager), "OnDayStarted")]
        [HarmonyPostfix]
        private static void OnOnDayStarted()
        {
            if (Plugin.TriggerOnDayStart.Value)
                DoShelfPut();
        }

        [HarmonyPatch(typeof(CGameManager), "Update")]
        [HarmonyPostfix]
        public static void OnGameManagerUpdatePostfix()
        {
            if (Plugin.SetOutCardsKey.Value.IsDown())
                DoShelfPut();
        }
    }
}

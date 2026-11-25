using System.Collections.Generic;
using UnityEngine;
#if IL2CPP
using Il2CppScheduleOne;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Product;
#else
using ScheduleOne;
using ScheduleOne.Economy;
using ScheduleOne.ItemFramework;
using ScheduleOne.Product;
#endif

namespace Middleman;

/// <summary>
/// Automatically selects the best recruited player dealer for a deal.
/// </summary>
internal static class DealerSelector
{
    /// <summary>
    /// Picks the conscious, recruited player dealer who has enough acceptable-quality
    /// packaged product in stock, preferring the fewest active deals and breaking ties
    /// by proximity to the delivery location.
    /// </summary>
    public static bool TrySelectDealer(
        Customer customer,
        ProductList? productList,
        DeliveryLocation? deliveryLocation,
        out Dealer? selectedDealer,
        out string failureReason)
    {
        selectedDealer = null;
        failureReason = string.Empty;

        if (!GameCompat.IsAlive(customer) || customer.CustomerData == null)
        {
            failureReason = "Customer data is not ready yet.";
            return false;
        }

        if (productList == null || productList.entries == null || productList.entries.Count == 0)
        {
            failureReason = "This deal has no requested products.";
            return false;
        }

        var allDealers = Dealer.AllPlayerDealers;
        if (allDealers == null || allDealers.Count == 0)
        {
            failureReason = "You don't have any recruited dealers yet.";
            return false;
        }

        EQuality baseQuality = customer.CustomerData.Standards.GetCorrespondingQuality();
        EQuality minQuality = ItemQuality.ShiftQuality(baseQuality, Dealer.NegativeQualityTolerance);
        EQuality maxQuality = ItemQuality.ShiftQuality(baseQuality, Dealer.PositiveQualityTolerance);

        int recruitedCount = 0;
        int consciousCount = 0;

        Dealer? bestDealer = null;
        int bestActiveCount = int.MaxValue;
        float bestDistance = float.MaxValue;

        for (int i = 0; i < allDealers.Count; i++)
        {
            Dealer dealer = allDealers[i];
            if (!GameCompat.IsAlive(dealer) || dealer.DealerData == null)
            {
                continue;
            }

            if (dealer.DealerData.DealerType != EDealerType.PlayerDealer || !dealer.IsRecruited)
            {
                continue;
            }

            recruitedCount++;

            if (!dealer.IsConscious)
            {
                continue;
            }

            consciousCount++;

            if (!DealerHasRequiredStock(dealer, productList, minQuality, maxQuality))
            {
                continue;
            }

            int activeCount = dealer.ActiveContracts != null ? dealer.ActiveContracts.Count : 0;
            float distance = GetDistanceToDelivery(dealer, deliveryLocation);

            if (bestDealer == null
                || activeCount < bestActiveCount
                || (activeCount == bestActiveCount && distance < bestDistance))
            {
                bestDealer = dealer;
                bestActiveCount = activeCount;
                bestDistance = distance;
            }
        }

        if (bestDealer != null)
        {
            selectedDealer = bestDealer;
            return true;
        }

        if (recruitedCount == 0)
        {
            failureReason = "You don't have any recruited dealers yet.";
        }
        else if (consciousCount == 0)
        {
            failureReason = "None of your dealers are currently available.";
        }
        else
        {
            failureReason = $"No dealer has {FormatRequestedProducts(productList)} in stock.";
        }

        return false;
    }

    private static bool DealerHasRequiredStock(
        Dealer dealer,
        ProductList productList,
        EQuality minQuality,
        EQuality maxQuality)
    {
        for (int i = 0; i < productList.entries.Count; i++)
        {
            ProductList.Entry entry = productList.entries[i];
            if (entry == null || string.IsNullOrEmpty(entry.ProductID))
            {
                continue;
            }

            int available = dealer.GetOrderableProductQuantity(entry.ProductID, minQuality, maxQuality);
            if (available < entry.Quantity)
            {
                return false;
            }
        }

        return true;
    }

    private static float GetDistanceToDelivery(Dealer dealer, DeliveryLocation? deliveryLocation)
    {
        if (!GameCompat.IsAlive(deliveryLocation)
            || !GameCompat.IsAlive(deliveryLocation!.CustomerStandPoint)
            || !GameCompat.IsAlive(dealer.Avatar))
        {
            return float.MaxValue;
        }

        return Vector3.Distance(dealer.Avatar.CenterPoint, deliveryLocation.CustomerStandPoint.position);
    }

    private static string FormatRequestedProducts(ProductList productList)
    {
        List<string> parts = new();
        for (int i = 0; i < productList.entries.Count; i++)
        {
            ProductList.Entry entry = productList.entries[i];
            if (entry == null || string.IsNullOrEmpty(entry.ProductID))
            {
                continue;
            }

            ItemDefinition? item = Registry.GetItem(entry.ProductID);
            string name = GameCompat.IsAlive(item) && !string.IsNullOrEmpty(item!.Name)
                ? item.Name
                : entry.ProductID;

            parts.Add($"{entry.Quantity}x {name}");
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "the requested product";
    }
}

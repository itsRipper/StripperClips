using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Modding.Custom;

namespace StripperClips.Server;

[Injectable(TypePriority = OnLoadOrder.Preload + 1)]
public class OnLoad(ISptLogger<OnLoad> logger, CustomItemService items, TradersTable traders, TemplateTable templates) : IOnLoad
{
    private const string MagazineParent = "5448bc234bdc2d3c308b4569";
    private const string MagazineHandbook = "5b5f754a86f774094242f19b";
    private const string Prapor = "54cb50c76803fa8b248b4571";
    private const string Peacekeeper = "5935c25fb3acc3127c3d8cd9";
    private const string Roubles = "5449016a4bdc2d6f028b456f";
    private const string Dollars = "5696686a4bdc2da3298b456a";
    private const string StanagClip = "6738a1c0f1e2d3c4b5a69783";

    // Price is the handbook and flea value in roubles; the trader offer is OfferPrice in Currency.
    private sealed record Clip(string Id, string Base, string AssortId, string Name, string ShortName, string Description, double Weight, int Price, string Prefab, string VisibleAmmo,
        string Trader, string TraderName, string Currency, int OfferPrice);

    private static readonly Clip[] Clips =
    {
        new("6738a1c0f1e2d3c4b5a69781", "5ae0973a5acfc4001562206c", "6738a1c0f1e2d3c4b5a69791",
            "7.62x54R 5-round stripper clip", "54R clip",
            "A five-round steel stripper clip for 7.62x54R ammunition. Originally developed for the Mosin-Nagant rifle, the design was also used with Soviet self-loading rifles such as the SVT-40. Simple, inexpensive and remarkably enduring.",
            0.011, 450, "stripperclips/item_stripper_clip_762x54r.bundle", "0-5", Prapor, "Prapor", Roubles, 450),
        new("6738a1c0f1e2d3c4b5a69782", "587df3a12459772c28142567", "6738a1c0f1e2d3c4b5a69792",
            "7.62x39 10-round stripper clip", "39 clip",
            "A ten-round steel stripper clip for 7.62x39 ammunition, commonly associated with the Soviet SKS carbine. Its compact stamped-steel construction makes it a practical alternative to carrying loose cartridges. A rather elegant solution for something so simple.",
            0.014, 550, "stripperclips/item_stripper_clip_762x39.bundle", "0-10", Prapor, "Prapor", Roubles, 550),
        new(StanagClip, "5aaa5e60e5b5b000140293d6", "6738a1c0f1e2d3c4b5a69793",
            "5.56x45 NATO 10-round stripper clip", "5.56 clip",
            "A steel clip that holds up to ten rounds of 5.56x45 NATO ammunition.",
            0.014, 550, "stripperclips/item_stripper_clip_556x45.bundle", "0-10", Peacekeeper, "Peacekeeper", Dollars, 6),
    };

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var registered = 0;
        foreach (var clip in Clips)
        {
            Slot[]? cartridges = null;
            if (clip.Id == StanagClip)
            {
                cartridges = StanagCartridges(clip);
                if (cartridges == null)
                {
                    logger.Error("StripperClips: the 5.56 clip base template or its NATO cartridge filter is missing.");
                    continue;
                }
            }
            var result = items.CreateItemFromClone(new NewItemFromCloneDetails
            {
                NewId = clip.Id,
                ItemTplToClone = clip.Base,
                ParentId = MagazineParent,
                NewItemName = clip.ShortName,
                HandbookParentId = MagazineHandbook,
                HandbookPriceRoubles = clip.Price,
                FleaPriceRoubles = clip.Price,
                Locales = new Dictionary<string, LocaleDetails>
                {
                    ["en"] = new() { Name = clip.Name, ShortName = clip.ShortName, Description = clip.Description },
                },
                OverrideProperties = new TemplateItemProperties
                {
                    Width = 1,
                    Height = 1,
                    Weight = clip.Weight,
                    ExaminedByDefault = true,
                    ReloadMagType = ReloadMode.ExternalMagazine,
                    LoadUnloadModifier = -20,
                    CheckOverride = 1,
                    Prefab = new Prefab { Path = clip.Prefab, Rcid = "" },
                    // Cover every round count so EFT refreshes the inventory icon.
                    VisibleAmmoRangesString = clip.VisibleAmmo,
                    ItemSound = "magazine_metal",
                    Cartridges = cartridges,
                },
            }, assembly);
            if (!result.Success)
            {
                logger.Error($"StripperClips: {clip.ShortName} not created: {string.Join("; ", result.Errors ?? new List<string>())}");
                continue;
            }
            Sell(clip);
            registered++;
        }
        logger.Info($"StripperClips: {registered} clips added (7.62 clips at Prapor, 5.56 clip at Peacekeeper)");
        return Task.CompletedTask;
    }

    private Slot[]? StanagCartridges(Clip clip)
    {
        if (!templates.Items.TryGetValue(new MongoId(clip.Base), out var template)) return null;
        var slot = template.Properties?.Cartridges?.FirstOrDefault(candidate => candidate.Name == "cartridges");
        if (slot?.Properties?.Filters == null) return null;
        var allowed = slot.Properties.Filters.SelectMany(filter => filter.Filter ?? [])
            .Where(id => templates.Items.TryGetValue(id, out var ammo) && ammo.Properties?.Caliber == "Caliber556x45NATO")
            .ToHashSet();
        if (allowed.Count == 0) return null;
        return [slot with
        {
            Id = new MongoId("6738a1c0f1e2d3c4b5a697a3"),
            Parent = new MongoId(clip.Id),
            MaxCount = 10,
            Properties = slot.Properties with { Filters = [new SlotFilter { Filter = allowed }] },
        }];
    }

    private void Sell(Clip clip)
    {
        if (!traders.TryGetValue(clip.Trader, out var trader)) { logger.Error($"StripperClips: {clip.TraderName} not found; {clip.ShortName} is not sold"); return; }
        var assort = trader.Assort;
        var id = new MongoId(clip.AssortId);
        assort.Items.Add(new Item
        {
            Id = id,
            Template = clip.Id,
            ParentId = "hideout",
            SlotId = "hideout",
            Upd = new Upd { UnlimitedCount = true, StackObjectsCount = 999999 },
        });
        assort.BarterScheme[id] = [[new BarterScheme { Template = clip.Currency, Count = clip.OfferPrice }]];
        assort.LoyalLevelItems[id] = 1;
    }
}

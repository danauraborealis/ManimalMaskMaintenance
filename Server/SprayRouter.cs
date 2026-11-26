using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace MaskMaintenance;

public record SprayRequest : IRequestData
{
    [JsonPropertyName("clinId")]
    public string ClinId { get; set; } = "";
}

// spends one charge of a Clin bottle in the stash. in raid the client changes the bottle itself
// and the raid-end save carries it; out of raid nothing else would tell the server.
[Injectable(TypePriority = OnLoadOrder.Routers + 1)]
public class SprayRouter(JsonUtil jsonUtil, ProfileHelper profileHelper, ISptLogger<SprayRouter> logger)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<SprayRequest>(
                "/maskmaintenance/spray",
                (url, info, sessionId, output, cancellationToken) =>
                {
                    var items = profileHelper.GetPmcProfile(sessionId)?.Inventory?.Items;
                    Item? bottle = null;
                    if (items != null)
                    {
                        foreach (var item in items)
                        {
                            if (item.Id.ToString() == info.ClinId)
                            {
                                bottle = item;
                                break;
                            }
                        }
                    }

                    if (bottle == null || bottle.Template != Clin.Tpl)
                    {
                        logger.Warning($"[MaskMaintenance] spray: no Clin bottle '{info.ClinId}' in the profile");
                        return new ValueTask<string>("{\"ok\":false}");
                    }

                    // a bottle from before the mod has no resource saved: that is a full one
                    bottle.Upd ??= new Upd();
                    bottle.Upd.Resource ??= new UpdResource { Value = Clin.Charges };
                    var left = bottle.Upd.Resource.Value ?? Clin.Charges;
                    if (left < 1)
                    {
                        return new ValueTask<string>("{\"ok\":false}");
                    }

                    bottle.Upd.Resource.Value = left - 1;
                    return new ValueTask<string>("{\"ok\":true}");
                }
            ),
        ]
    ) { }

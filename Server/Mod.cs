using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace MaskMaintenance;

// guid matches the client plugin. version comes from Directory.Build.props through the assembly.
public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.manimal.maskmaintenance";
    public string Name { get; init; } = "MaskMaintenance";
    public string Author { get; init; } = "Manimal";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } =
        new(typeof(ModMetadata).Assembly.GetName().Version!.ToString(3));
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.5");
    public bool HasPrepatcher { get; init; } = false;
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; } = "https://github.com/danauraborealis/ManimalMaskMaintenance";
    public string License { get; init; } = "MIT";
}

internal static class Clin
{
    public static readonly MongoId Tpl = new("59e358a886f7741776641ac3");
    public const int Charges = 3;
}

// the client draws a resource bar on any barter item whose template has one, so this is all
// it takes to turn the bottle into a 3/3 item
[Injectable(TypePriority = OnLoadOrder.Preload + 2)]
public class ClinCharges(TemplateTable templateTable, ISptLogger<ClinCharges> logger) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        if (templateTable.Items == null
            || !templateTable.Items.TryGetValue(Clin.Tpl, out var clin)
            || clin.Properties == null)
        {
            logger.Error("[MaskMaintenance] Clin window cleaner is not in the item database, it will have no charges");
            return Task.CompletedTask;
        }

        clin.Properties.MaxResource = Clin.Charges;
        clin.Properties.Resource = Clin.Charges;
        logger.Info($"[MaskMaintenance] Clin window cleaner has {Clin.Charges} charges");
        return Task.CompletedTask;
    }
}

using Dalamud.Plugin.Services;
using Lumina.Excel.GeneratedSheets;
using System.Collections.Generic;
using System.Linq;
using Umbra.Common;
using Umbra.Game;
using Umbra.Markers;

namespace Konngitas.StaticGatheringMarkersPlugin.Markers;

[Service]
internal class GatheringNodeMarkerFactory(
    IDataManager dataManager,
    IPlayer player,
    IZoneManager zoneManager
) : WorldMarkerFactory
{
    public override string Id { get; } = "StaticGatheringNodeMarkers";
    public override string Name { get; } = "Static Gathering Markers";
    public override string Description { get; } = "Shows Gathering Markers even when not a DoL";

    private int _displayIndex;
    private readonly List<GatheringNode> _gatheringNodes = [];

    public override List<IMarkerConfigVariable> GetConfigVariables()
    {
        return [
            ..DefaultStateConfigVariables,
            new BooleanMarkerConfigVariable(
                "ShowContents",
                "Show Contents",
                "Whether to show a gathering node's contents",
                true
            ),
            new BooleanMarkerConfigVariable(
                "ShowMarkersForCurrentClass",
                "Show markers for current DoL class",
                "Whether to show the markers for the current DoL class if you are currently a DoL",
                false
                ),
            ..DefaultFadeConfigVariables
        ];
    }

    protected override void OnInitialized()
    {
        Logger.Info("Initialized. Fetching markers");
        if (zoneManager.HasCurrentZone)
        {
            var zone = zoneManager.CurrentZone;
            OnZoneChanged(zone);
        }
    }

    protected override void OnConfigUpdated(string name)
    {
        SetMarkers();
    }

    private void SetMarkers()
    {
        RemoveAllMarkers();
        if (!GetConfigValue<bool>("Enabled"))
        {
            return;
        }

        GatheringJob? job = player.JobId switch
        {
            16 => GatheringJob.Miner,
            17 => GatheringJob.Botanist,
            18 => GatheringJob.Fisher,
            _ => null
        };

        var filterNodes = !GetConfigValue<bool>("ShowMarkersForCurrentClass");

        var filteredNodes = job != null && filterNodes? _gatheringNodes.Where(node => node.Job != job) : _gatheringNodes;

        foreach (GatheringNode node in filteredNodes)
        {
            var fadeDist = GetConfigValue<int>("FadeDistance");
            var fadeAttn = GetConfigValue<int>("FadeAttenuation");
            var showCtns = GetConfigValue<bool>("ShowContents");

            SetMarker(
                new()
                {
                    MapId = zoneManager.CurrentZone.Id,
                    Key = node.Key,
                    Position = new(node.Coordinates.X, 0, node.Coordinates.Y),
                    IconId = node.IconId,
                    Label = node.Label,
                    SubLabel = showCtns ? (node.Items.Count > 0 ? $"{node.Items[_displayIndex % node.Items.Count]}" : null) : null,
                    ShowOnCompass = node.ShowDirection && GetConfigValue<bool>("ShowOnCompass"),
                    FadeDistance = new(fadeDist, fadeDist + fadeAttn)
                }
            );
        }
    }

    //Removes old skybuilders nodes outside of Diadem
    private bool IsDeprecated(GatheringPoint gp)
    {
        //Quest 74 marks skybuilders stuff.
        //TerritoryType 939, 929, 901 is the most recent, and old versions of diadem
        var territoryType = gp.TerritoryType.Row;
        return gp.GatheringSubCategory?.Value?.Quest.Row == 74 && ( territoryType != 939 || territoryType != 929 || territoryType != 901);
    }
  

    protected override void OnZoneChanged(IZone zone)
    {
        Logger.Info("Got zone change. Fetching markers");
        RemoveAllMarkers();
        if (!GetConfigValue<bool>("Enabled"))
        {
            return;
        }
        UpdateStaticGatheringNodes(zone);

        SetMarkers();
    }

    private void UpdateStaticGatheringNodes(IZone zone)
    {
        _gatheringNodes.Clear();


        Dictionary<uint, GatheringPointBase>? gatheringPointsForZone = dataManager.GetExcelSheet<GatheringPoint>()?.Where(gp => gp.TerritoryType.Row == zone.TerritoryId && !IsDeprecated(gp))?.Select(gp => gp.GatheringPointBase.Value!)
            .Distinct()
            .ToDictionary(gp => gp.RowId);
 


        var coords = dataManager.GetExcelSheet<ExportedGatheringPoint>();

        if (gatheringPointsForZone == null || coords == null)
        {
            Logger.Warning($"Failed to load gathering points or coordinates. points - {(gatheringPointsForZone != null ? "present" : "null")} | coordinates - {(coords != null ? "present" : "null")}");
            return;
        }

        Dictionary<uint, Coords> coordsForPoints = coords.Where(c => gatheringPointsForZone.ContainsKey(c.RowId)).ToDictionary(c => c.RowId, c => new Coords
        {
            X = c.X,
            Y = c.Y
        });
        Logger.Info($"Loaded {gatheringPointsForZone.Count} gathering points for zone {zone.Name} - {zone.TerritoryId}");
        Logger.Info($"Loaded {coordsForPoints.Count} coordinates for zone {zone.Name} - {zone.TerritoryId}");

        foreach ((uint row, GatheringPointBase point) in gatheringPointsForZone)
        {
            coordsForPoints.TryGetValue(row, out Coords coordinates);
            var node = CreateNodeFromObject(point, coordinates);

            _gatheringNodes.Add(node);
        }
    }

    private GatheringNode CreateNodeFromObject(GatheringPointBase point, Coords coords)
    {
        List<string> items = point
            .Item.Select(
                i =>
                {
                    if (i == 0) return null;

                    var gItem = dataManager.GetExcelSheet<GatheringItem>()!.GetRow((uint)i);

                    return gItem == null
                        ? null
                        : dataManager.GetExcelSheet<Item>()!.GetRow((uint)gItem.Item)?.Name.ToString();
                }
            )
            .Where(i => i != null)
        .ToList()!;

        return new GatheringNode
        {
            Key = $"GN_{coords.X:N0}_{coords.Y:N0}",
            Coordinates = coords,
            IconId = (uint)(point.GatheringType.Value?.IconMain ?? 0),
            Label = $"Lv.{point.GatheringLevel} Gathering Point",
            Items = items,
            ShowDirection = !(!player.IsDiving && point.GatheringType.Row == 5),
            Job = JobIdToJob(point.GatheringType.Row)
        };
    }

    [OnTick(interval: 2000)]
    internal void Update()
    {
        _displayIndex++;

        if (_displayIndex > 1000) _displayIndex = 0;
        SetMarkers();
    }

    private struct GatheringNode
    {
        public string Key;
        public Coords Coordinates;
        public uint IconId;
        public string Label;
        public List<string> Items;
        public bool ShowDirection;
        public GatheringJob? Job;
    }

    private struct Coords
    {
        public float X;
        public float Y;
    }

    private enum GatheringJob
    {
        Miner, Botanist, Fisher
    }

    private static GatheringJob? JobIdToJob(uint jobId)
    {
        return jobId switch
        {
            0 => GatheringJob.Miner,
            1 => GatheringJob.Miner,
            2 => GatheringJob.Botanist,
            3 => GatheringJob.Botanist,
            4 => GatheringJob.Fisher,
            5 => GatheringJob.Botanist,
            6 => GatheringJob.Miner,
            7 => GatheringJob.Fisher,
            _ => null,
        };
    }
}

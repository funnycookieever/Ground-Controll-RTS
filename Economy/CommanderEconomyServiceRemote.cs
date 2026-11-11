using System;
using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// A guest's BUILD window, carried out on the host. Buildings are spawned server-side, so the
/// guest sends what and where, and the host puts it down through the same
/// <see cref="CommitBuild"/> its own click uses: same site rules, same prices, same funds check.
/// Building levels live on the host too, because income and production are paid there, so they
/// are copied back to the guest for its selection bar and naval gate.
/// </summary>
internal sealed partial class CommanderEconomyService
{
    private readonly List<UnitDefinition> remoteProduction = new();
    private readonly List<Unit> remoteBuilders = new();

    private void RegisterRemote()
    {
        CommanderNetwork.OnHost("build", HostBuild);
        CommanderNetwork.OnHost("build.assign", HostAssignBuilders);
        CommanderNetwork.OnHost("build.cancel", HostCancelBlueprint);
        CommanderNetwork.OnHost("upgrade", HostUpgrade);
        CommanderNetwork.OnHost("demolish", HostDemolish);
        CommanderNetwork.OnHost("depot.up", HostUpgradeDepot);
        CommanderNetwork.OnSync(SyncLevels);
        CommanderNetwork.OnGuest("levels", ApplyLevels);
        CommanderNetwork.OnSync(SyncBlueprints);
        CommanderNetwork.OnGuest("blueprints", ApplyBlueprints);
        CommanderNetwork.OnGuest("st.eco", args => StatusText = CommanderNetwork.Arg(args, 1));

        // The host pays the income, so a guest's tick bar runs on the host's clock, not its own
        // load-time phase: otherwise the money lands while the guest's bar is still filling.
        CommanderNetwork.OnSync((_, messages) => messages.Add(new[] { "eco.tick", CommanderNetwork.Num(nextIncomeAt - Time.time) }));
        CommanderNetwork.OnGuest("eco.tick", args => nextIncomeAt = Time.time + CommanderNetwork.Float(args, 1));
    }

    private void HostBuild(FactionHQ hq, string[] args)
    {
        CommanderBuildKind kind = (CommanderBuildKind)CommanderNetwork.Int(args, 1);
        if (kind == CommanderBuildKind.None
            || !Enum.IsDefined(typeof(CommanderBuildKind), kind)
            || !CommanderNetwork.TryPosition(args, 4, out GlobalPosition position))
        {
            return;
        }

        BuildingDefinition? structure = null;
        if (kind == CommanderBuildKind.Structure)
        {
            string key = CommanderNetwork.Arg(args, 2);
            RefreshCatalog();
            structure = catalog.Find(candidate => candidate.jsonKey == key);
            if (structure == null)
            {
                CommanderNotice.Fail("The host's building catalogue has no such structure.");
                return;
            }
        }

        // What a new factory makes is the guest's pick, checked against that faction's own roster.
        string productionKey = CommanderNetwork.Arg(args, 3);
        CollectFactoryProducts(remoteProduction, hq);
        UnitDefinition? production = remoteProduction.Find(candidate => candidate.jsonKey == productionKey);
        if (kind == CommanderBuildKind.Factory && production == null)
        {
            CommanderNotice.Fail("That factory has nothing your faction can produce.");
            return;
        }

        Quaternion rotation = Quaternion.Euler(0f, CommanderNetwork.Float(args, 5), 0f);
        RunForGuest(() =>
        {
            if (!CommitBuild(hq, kind, structure, production, position, rotation))
            {
                CommanderNotice.Fail(StatusText.Length > 0 ? StatusText : "Could not build there.");
            }
        });
    }

    private void HostUpgrade(FactionHQ hq, string[] args)
    {
        if (!CommanderNetwork.TryUnit(args, 1, out Unit unit) || !ReferenceEquals(unit.NetworkHQ, hq))
        {
            return;
        }

        RunForGuest(() =>
        {
            if (incomeLevels.ContainsKey(unit))
            {
                UpgradeIncome(unit);
            }
            else if (dockLevels.ContainsKey(unit))
            {
                UpgradeNavalDock(unit);
            }
            else if (unit.TryGetComponent(out Factory factory))
            {
                UpgradeFactory(factory);
            }
        });
    }

    private void HostDemolish(FactionHQ hq, string[] args)
    {
        // Only what a commander built: a guest must not be able to level mission buildings.
        if (CommanderNetwork.TryUnit(args, 1, out Unit unit)
            && ReferenceEquals(unit.NetworkHQ, hq)
            && builtUnits.Contains(unit))
        {
            RunForGuest(() => Demolish(unit));
        }
    }

    private void HostUpgradeDepot(FactionHQ hq, string[] args)
    {
        if (CommanderNetwork.TryUnit(args, 1, out Unit unit) && ReferenceEquals(unit.NetworkHQ, hq))
        {
            RunForGuest(() => UpgradeDepot(unit));
        }
    }

    /// <summary>A guest's cancel: the site's centre, looked up among that guest's faction's blueprints only.</summary>
    private void HostCancelBlueprint(FactionHQ hq, string[] args)
    {
        if (CommanderNetwork.TryPosition(args, 1, out GlobalPosition point) && FindSiteAt(point, hq) is ConstructionSite site)
        {
            RunForGuest(() => CancelBlueprint(site));
        }
    }

    /// <summary>A guest's right-click on a blueprint: "position id id ...", only the guest's own vehicles count.</summary>
    private void HostAssignBuilders(FactionHQ hq, string[] args)
    {
        if (!CommanderNetwork.TryPosition(args, 1, out GlobalPosition site))
        {
            return;
        }

        remoteBuilders.Clear();
        for (int i = 2; i < args.Length; i++)
        {
            if (CommanderNetwork.TryUnit(args, i, out Unit unit) && ReferenceEquals(unit.NetworkHQ, hq))
            {
                remoteBuilders.Add(unit);
            }
        }

        RunForGuest(() =>
        {
            if (!TryAssignBuilders(remoteBuilders, site))
            {
                CommanderNotice.Fail("Those vehicles could not be sent: the blueprint is already finished.");
            }
        });
        remoteBuilders.Clear();
    }

    /// <summary>
    /// This guest's faction's blueprints: the host's build time, then six fields per site (kind,
    /// building, where, progress, builders on site, facing), so the guest can draw them rising.
    /// </summary>
    private void SyncBlueprints(FactionHQ hq, List<string[]> messages)
    {
        List<string> args = new() { "blueprints", CommanderNetwork.Num(ConstructionSeconds) };
        for (int i = 0; i < constructionSites.Count; i++)
        {
            ConstructionSite site = constructionSites[i];
            if (!ReferenceEquals(site.Hq, hq))
            {
                continue;
            }

            args.Add(CommanderNetwork.Num((int)site.Kind));
            args.Add(site.Definition.jsonKey);
            args.Add(CommanderNetwork.Position(site.Site));
            args.Add(CommanderNetwork.Num(site.Progress));
            args.Add(CommanderNetwork.Num(site.Working));
            args.Add(CommanderNetwork.Num(site.Rotation.eulerAngles.y));
        }

        messages.Add(args.ToArray());
    }

    /// <summary>
    /// The guest's side of <see cref="SyncBlueprints"/>. The copies go into the guest's own list, so
    /// the placement ghost and a right-click see them like the host's; TickConstruction only draws them.
    /// </summary>
    private void ApplyBlueprints(string[] args)
    {
        constructionSites.Clear();
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null)
        {
            return;
        }

        float seconds = Mathf.Max(0.5f, CommanderNetwork.Float(args, 1));
        for (int i = 2; i + 5 < args.Length; i += 6)
        {
            CommanderBuildKind kind = (CommanderBuildKind)CommanderNetwork.Int(args, i);
            BuildingDefinition? definition = CommanderNetwork.Definition<BuildingDefinition>(CommanderNetwork.Arg(args, i + 1));
            if (definition == null || !CommanderNetwork.TryPosition(args, i + 2, out GlobalPosition position))
            {
                continue;
            }

            string label = kind == CommanderBuildKind.Structure ? GetStructureLabel(definition) : BuildLabel(kind);
            constructionSites.Add(new ConstructionSite(kind, definition, null, hq, position, 0f, label, GetRingRadius(definition))
            {
                Progress = CommanderNetwork.Float(args, i + 3),
                RemoteRate = CommanderNetwork.Int(args, i + 4) / seconds,
                Rotation = Quaternion.Euler(0f, CommanderNetwork.Float(args, i + 5), 0f),
            });
        }
    }

    /// <summary>
    /// Runs a guest's request and sends that guest whatever the BUILD status line then said,
    /// leaving the host's own status line as it was.
    /// </summary>
    private void RunForGuest(Action request)
    {
        string hostStatus = StatusText;
        StatusText = string.Empty;
        try
        {
            request();
            if (StatusText.Length > 0)
            {
                CommanderNetwork.Reply("st.eco", StatusText);
            }
        }
        finally
        {
            StatusText = hostStatus;
        }
    }

    /// <summary>Every building this guest's faction has had built, as "id kind level".</summary>
    private void SyncLevels(FactionHQ hq, List<string[]> messages)
    {
        List<string> args = new() { "levels" };
        foreach (Unit unit in builtUnits)
        {
            if (unit == null || !ReferenceEquals(unit.NetworkHQ, hq))
            {
                continue;
            }

            string kind;
            if (incomeLevels.TryGetValue(unit, out int level))
            {
                kind = GetIncomeKind(unit) switch
                {
                    CommanderBuildKind.FuelExchange => "e",
                    CommanderBuildKind.GasTerminal => "t",
                    _ => "m",
                };
            }
            else if (dockLevels.TryGetValue(unit, out level))
            {
                kind = "d";
            }
            else if (factoryLevels.TryGetValue(unit, out level))
            {
                kind = "f";
            }
            else
            {
                kind = "b";
                level = 0;
            }

            args.Add($"{CommanderNetwork.Id(unit)} {kind} {CommanderNetwork.Num(level)}");
        }

        // Upgraded depots, mission-built ones included: a guest's depot queue locks artillery and
        // heavy units behind the level, and read its own empty copy it never unlocked.
        foreach (KeyValuePair<Unit, int> entry in depotLevels)
        {
            if (entry.Key != null && ReferenceEquals(entry.Key.NetworkHQ, hq))
            {
                args.Add($"{CommanderNetwork.Id(entry.Key)} p {CommanderNetwork.Num(entry.Value)}");
            }
        }

        messages.Add(args.ToArray());
    }

    /// <summary>The guest's side of <see cref="SyncLevels"/>: the host's word replaces its own.</summary>
    private void ApplyLevels(string[] args)
    {
        incomeLevels.Clear();
        incomeKinds.Clear();
        factoryLevels.Clear();
        dockLevels.Clear();
        depotLevels.Clear();
        builtUnits.Clear();
        for (int i = 1; i < args.Length; i++)
        {
            string[] fields = args[i].Split(' ');
            if (fields.Length != 3 || !CommanderNetwork.TryUnit(fields[0], out Unit unit))
            {
                continue;
            }

            int level = CommanderNetwork.Int(fields, 2);
            if (fields[1] == "p")
            {
                depotLevels[unit] = level;
                continue;
            }

            builtUnits.Add(unit);
            CommanderBuiltHangar.EnsureFloor(unit, preview);
            switch (fields[1])
            {
                case "m":
                    incomeLevels[unit] = level;
                    break;
                case "e":
                    incomeLevels[unit] = level;
                    incomeKinds[unit] = CommanderBuildKind.FuelExchange;
                    break;
                case "t":
                    incomeLevels[unit] = level;
                    incomeKinds[unit] = CommanderBuildKind.GasTerminal;
                    break;
                case "d":
                    dockLevels[unit] = level;
                    break;
                case "f":
                    factoryLevels[unit] = level;
                    break;
            }
        }

        RefreshFriendlyLists();
    }
}

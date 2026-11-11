using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The three income buildings, cheapest first: the Fuel Exchange (the game's aviation fuel depot),
/// the Gas Export Terminal (the in-ground storage tank) and the Gold Mine (the refinery). Each one
/// is a plain building to the game; what it pays is the mod's, levelled in <c>incomeLevels</c> and
/// told apart by <c>incomeKinds</c>.
/// </summary>
/// <remarks>
/// Prices are a fixed balance table, not config. Tier 2 costs less than tier 1 on purpose, so the
/// self-check does not ask for a rising price ladder here.
/// The two fuel prefabs are not supply stores: <c>CommanderSupplyStoreService</c> asks
/// <see cref="IsIncomeBuildingUnit"/> first, so they hold no fuel and sell none, like a gold mine.
/// </remarks>
internal sealed partial class CommanderEconomyService
{
    /// <summary>The most of its balance an AI commander puts into one income building.</summary>
    private const float EnemyIncomeSpendShare = 0.5f;

    /// <summary>Fully upgraded fuel buildings an AI commander has before it may build a gold mine, fewer
    /// only when fewer starters can be built at all.</summary>
    private const int EnemyMaxedStartersBeforeMine = 2;

    /// <summary>Income at which an AI commander saves for its first gold mine, or one it lost, rather
    /// than another gas terminal: four minutes of it buys the 600 mine.</summary>
    private const float EnemyMineSavingIncome = 150f;

    /// <summary>Income from which an AI commander buys only gold mines.</summary>
    private const float EnemyMinesOnlyIncome = 850f;

    /// <summary>Cheapest first, which is the order the BUILD window lists them in.</summary>
    private static readonly IncomeStage[] IncomeStages =
    {
        new(CommanderBuildKind.FuelExchange, "Fuel Exchange", new[] { "fuelTank1" }, new[] { 40f, 20f, 30f }, 5f),
        new(CommanderBuildKind.GasTerminal, "Gas Export Terminal", new[] { "storageTank" }, new[] { 160f, 100f, 130f }, 25f),
        new(CommanderBuildKind.Mine, MineDisplayName, new[] { "refinery_main", "factory_large" }, new[] { 600f, 400f, 500f }, 100f),
    };

    /// <summary>Which income building each levelled unit is. Missing means a gold mine.</summary>
    private readonly Dictionary<Unit, CommanderBuildKind> incomeKinds = new();

    private readonly Dictionary<CommanderBuildKind, BuildingDefinition?> incomeDefinitions = new();

    internal static IReadOnlyList<CommanderBuildKind> IncomeKinds { get; } =
        new[] { CommanderBuildKind.FuelExchange, CommanderBuildKind.GasTerminal, CommanderBuildKind.Mine };

    /// <summary>Any faction's income building. The supply stores ask, so a Fuel Exchange is not a fuel shelf.</summary>
    internal static bool IsIncomeBuildingUnit(Unit? unit)
    {
        return unit != null && Instance != null && Instance.incomeLevels.ContainsKey(unit);
    }

    /// <summary>What a faction's income buildings pay a minute, before the AI multiplier.</summary>
    internal static float GetEnemyIncomePerMinute(FactionHQ hq)
    {
        return Instance == null ? 0f : Instance.GetIncomePerMinute(hq);
    }

    internal static bool IsIncomeKind(CommanderBuildKind kind)
    {
        return kind is CommanderBuildKind.Mine or CommanderBuildKind.FuelExchange or CommanderBuildKind.GasTerminal;
    }

    internal static string GetIncomeName(CommanderBuildKind kind)
    {
        return GetStage(kind).Name;
    }

    /// <summary>Build price at <paramref name="level"/> 0, else the price of the next level up.</summary>
    internal static float GetIncomeCost(CommanderBuildKind kind, int level)
    {
        float[] costs = GetStage(kind).Costs;
        return costs[Mathf.Clamp(level, 0, costs.Length - 1)];
    }

    internal static float GetIncomePerMinute(CommanderBuildKind kind, int level)
    {
        return GetStage(kind).IncomePerLevel * level * CommanderPriceProfile.IncomeMultiplier;
    }

    internal bool IsIncomeBuilding(Unit? unit)
    {
        return unit != null && incomeLevels.ContainsKey(unit);
    }

    internal int GetIncomeLevel(Unit unit)
    {
        return incomeLevels.TryGetValue(unit, out int level) ? level : 0;
    }

    internal CommanderBuildKind GetIncomeKind(Unit unit)
    {
        return incomeKinds.TryGetValue(unit, out CommanderBuildKind kind) ? kind : CommanderBuildKind.Mine;
    }

    /// <summary>"GAS EXPORT TERMINAL   LVL 2/3   +50/min", for the BUILD list and the selection bar.</summary>
    internal string DescribeIncome(Unit unit)
    {
        CommanderBuildKind kind = GetIncomeKind(unit);
        int level = GetIncomeLevel(unit);
        return $"{GetIncomeName(kind).ToUpperInvariant()}   LVL {level}/{MaxLevel}   "
            + $"+{FundsLabel(GetIncomePerMinute(kind, level))}/min";
    }

    internal float GetIncomeUpgradeCost(Unit unit)
    {
        return GetIncomeCost(GetIncomeKind(unit), GetIncomeLevel(unit));
    }

    internal void UpgradeIncome(Unit building)
    {
        int level = GetIncomeLevel(building);
        if (level is <= 0 or >= MaxLevel)
        {
            return;
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        CommanderBuildKind kind = GetIncomeKind(building);
        float cost = GetIncomeCost(kind, level);
        if (hq == null || hq.factionFunds < cost)
        {
            StatusText = "Insufficient faction funds for that upgrade.";
            return;
        }

        if (CommanderNetwork.Forward("upgrade", CommanderNetwork.Id(building)))
        {
            StatusText = CommanderNetwork.HostHasMod ? "Upgrading." : CommanderNetwork.HostMissingReason;
            return;
        }

        hq.AddFunds(-cost);
        incomeLevels[building] = level + 1;
        StatusText = $"{GetIncomeName(kind)} at level {level + 1}. +{FundsLabel(GetIncomePerMinute(kind, level + 1))}/min.";
    }

    /// <summary>
    /// The dearest income building costing at most <see cref="EnemyIncomeSpendShare"/> of the
    /// balance, skipping any starter that is <paramref name="starterFull"/>. With nothing that cheap
    /// it saves for the cheapest starter still open, and once every starter is built it saves for the
    /// gold mine. Taking the dearest affordable one outright sent every commander straight to gold
    /// mines, because even a 100 M match's head start covers one; a poor commander now starts on
    /// fuel and gas and climbs, and the cap stops it filling its bases with 5/min buildings.
    /// </summary>
    private static CommanderBuildKind PickIncomeKind(
        float funds, System.Func<CommanderBuildKind, bool> starterFull, bool mineOpen = true)
    {
        for (int i = IncomeStages.Length - 1; i >= 0; i--)
        {
            CommanderBuildKind kind = IncomeStages[i].Kind;
            bool open = kind == CommanderBuildKind.Mine ? mineOpen : !starterFull(kind);
            if (open && IncomeStages[i].Costs[0] <= funds * EnemyIncomeSpendShare)
            {
                return kind;
            }
        }

        for (int i = 0; i < IncomeStages.Length; i++)
        {
            if (IncomeStages[i].Kind != CommanderBuildKind.Mine && !starterFull(IncomeStages[i].Kind))
            {
                return IncomeStages[i].Kind;
            }
        }

        // Every starter is up but the gold mine is still shut: one more gas terminal is more income,
        // where saving for a mine it may not build is none.
        return mineOpen ? CommanderBuildKind.Mine : CommanderBuildKind.GasTerminal;
    }

    private static bool WantsGoldMine(bool mineOpen, float income, int goldMines, bool rebuilding)
    {
        return income >= EnemyMinesOnlyIncome
            || (mineOpen && income >= EnemyMineSavingIncome && (goldMines == 0 || rebuilding));
    }

    /// <summary>A rich commander builds gold mines, a poor one starts on fuel and climbs, and one
    /// with every starter up saves for the gold mine rather than sitting on starters.</summary>
    private static void CheckEnemyIncomeChoice()
    {
        if (PickIncomeKind(3000f, _ => false) != CommanderBuildKind.Mine
            || PickIncomeKind(300f, _ => false) != CommanderBuildKind.FuelExchange
            || PickIncomeKind(400f, _ => false) != CommanderBuildKind.GasTerminal
            || PickIncomeKind(400f, kind => kind == CommanderBuildKind.GasTerminal) != CommanderBuildKind.FuelExchange
            || PickIncomeKind(300f, kind => kind == CommanderBuildKind.FuelExchange) != CommanderBuildKind.GasTerminal
            || PickIncomeKind(10f, _ => false) != CommanderBuildKind.FuelExchange
            || PickIncomeKind(300f, _ => true) != CommanderBuildKind.Mine
            || PickIncomeKind(3000f, _ => false, mineOpen: false) != CommanderBuildKind.GasTerminal
            || PickIncomeKind(300f, _ => true, mineOpen: false) != CommanderBuildKind.GasTerminal
            || !WantsGoldMine(true, 150f, 0, false)
            || !WantsGoldMine(true, 150f, 1, true)
            || !WantsGoldMine(false, 850f, 1, false)
            || WantsGoldMine(true, 600f, 1, false)
            || WantsGoldMine(false, 600f, 0, false))
        {
            CommanderPlugin.Log.LogError("Economy self-check FAILED: the enemy's income building choice is wrong.");
        }
    }

    private static IncomeStage GetStage(CommanderBuildKind kind)
    {
        for (int i = 0; i < IncomeStages.Length; i++)
        {
            if (IncomeStages[i].Kind == kind)
            {
                return IncomeStages[i];
            }
        }

        return IncomeStages[IncomeStages.Length - 1];
    }

    /// <summary>Income never falls as a building levels up, and every price is positive. Tier 2
    /// being cheaper than tier 1 is the design, so that is not checked.</summary>
    private static void CheckIncomeLadder()
    {
        for (int i = 0; i < IncomeStages.Length; i++)
        {
            IncomeStage stage = IncomeStages[i];
            if (stage.Costs.Length != MaxLevel || GetStage(stage.Kind) != stage)
            {
                CommanderPlugin.Log.LogError($"Economy self-check FAILED: the {stage.Name} price table is malformed.");
                return;
            }

            for (int level = 1; level <= MaxLevel; level++)
            {
                if (stage.Costs[level - 1] <= 0f
                    || GetIncomePerMinute(stage.Kind, level) <= GetIncomePerMinute(stage.Kind, level - 1))
                {
                    CommanderPlugin.Log.LogError($"Economy self-check FAILED: the {stage.Name} ladder does not pay more per level.");
                    return;
                }
            }
        }
    }

    /// <summary>
    /// The income building an AI commander puts up next. Only the gold mine counts toward its mine
    /// target, the two cheaper ones are starters.
    /// </summary>
    private CommanderBuildKind ChooseEnemyIncomeKind(FactionHQ hq)
    {
        // The balance and the starter gate are both about affording a mine, which a free one skips.
        if (HasFreeMine(hq) || IsGodEconomy(hq))
        {
            return CommanderBuildKind.Mine;
        }

        // A gold mine it has lost is back in reach without maxing the starters again: raided, a
        // commander never climbed back to two maxed starters and never replaced its mine.
        bool mineOpen = IsEnemyMineOpen(hq) || IsRebuildingIncome(hq);

        // No limit on starters, so the income decides when to stop buying them for the first mine, which
        // the depot upgrade and the dock wait on. After that gas terminals are the better buy until
        // EnemyMinesOnlyIncome makes a mine's 600 small.
        if (WantsGoldMine(
            mineOpen, GetIncomePerMinute(hq), CountMines(hq, CommanderBuildKind.Mine), IsRebuildingIncome(hq)))
        {
            return CommanderBuildKind.Mine;
        }

        // Gas terminals: one pays 25/min per level for 160 where the exchange pays 5 for 40. An exchange
        // is only for a commander with no income that cannot afford a terminal.
        bool poorStart = CountMines(hq) == 0 && hq.factionFunds < GetIncomeCost(CommanderBuildKind.GasTerminal, 0);
        return PickIncomeKind(
            hq.factionFunds,
            kind => kind == CommanderBuildKind.FuelExchange && !poorStart,
            mineOpen);
    }

    /// <summary>Income is short of what the mine target's maxed gold mines would pay. Asked in income,
    /// not in mines: gas terminals count toward it, and a count of gold mines would never be met and
    /// hold every rung behind it.</summary>
    private bool IsBelowEnemyIncomeTarget(FactionHQ hq)
    {
        return GetIncomePerMinute(hq)
            < GetEnemyMineTarget(hq) * GetIncomePerMinute(CommanderBuildKind.Mine, MaxLevel);
    }

    /// <summary>
    /// A gold mine waits until <see cref="EnemyMaxedStartersBeforeMine"/> fuel buildings are at the
    /// top level: two maxed gas terminals pay 150/min for 780, a fresh gold mine 100/min for 600.
    /// </summary>
    private bool IsEnemyMineOpen(FactionHQ hq)
    {
        // A rich start (big authored balance, head start) skips the starters: the climb is for a
        // commander that cannot afford a mine, not one sitting on several.
        if (IsGodEconomy(hq) || hq.factionFunds >= GetIncomeCost(CommanderBuildKind.Mine, 0) * EnemyReserveMultiple)
        {
            return true;
        }

        int maxed = 0;
        foreach (KeyValuePair<Unit, int> entry in incomeLevels)
        {
            if (entry.Value >= MaxLevel
                && entry.Key != null
                && !entry.Key.disabled
                && entry.Key.NetworkHQ == hq
                && GetIncomeKind(entry.Key) != CommanderBuildKind.Mine)
            {
                maxed++;
            }
        }

        int buildable = 0;
        for (int i = 0; i < IncomeKinds.Count; i++)
        {
            if (IncomeKinds[i] != CommanderBuildKind.Mine && ResolveIncomeDefinition(IncomeKinds[i]) != null)
            {
                buildable++;
            }
        }

        return maxed >= Mathf.Min(EnemyMaxedStartersBeforeMine, buildable);
    }

    /// <summary>
    /// The AI's next income upgrade: the one that pays its price back soonest, the cheaper on a tie.
    /// Every upgrade pays back in 4 to 6 minutes and every new building in 6 to 8, so owned buildings
    /// climb before new ones go up.
    /// </summary>
    private Unit? FindEnemyIncomeUpgrade(FactionHQ hq, out float cost)
    {
        Unit? best = null;
        float bestPayback = float.MaxValue;
        cost = 0f;
        foreach (KeyValuePair<Unit, int> entry in incomeLevels)
        {
            if (entry.Value <= 0
                || entry.Value >= MaxLevel
                || entry.Key == null
                || entry.Key.disabled
                || entry.Key.NetworkHQ != hq)
            {
                continue;
            }

            CommanderBuildKind kind = GetIncomeKind(entry.Key);
            float price = GetIncomeCost(kind, entry.Value);
            float payback = price / GetStage(kind).IncomePerLevel;
            if (payback < bestPayback || (payback == bestPayback && price < cost))
            {
                best = entry.Key;
                bestPayback = payback;
                cost = price;
            }
        }

        return best;
    }

    /// <summary>Buys <see cref="FindEnemyIncomeUpgrade"/> when the balance holds its price times
    /// <paramref name="reserveMultiple"/>.</summary>
    private bool TryUpgradeEnemyIncome(FactionHQ hq, float reserveMultiple)
    {
        Unit? building = FindEnemyIncomeUpgrade(hq, out float cost);
        if (building == null
            || hq.factionFunds < cost * reserveMultiple
            || !CommanderGameAccess.TrySpend(hq, cost))
        {
            return false;
        }

        int level = incomeLevels[building] + 1;
        incomeLevels[building] = level;
        CommanderPlugin.Log.LogInfo(
            $"Enemy commander ({hq.faction.name}) upgraded a {GetIncomeName(GetIncomeKind(building))} to level {level}.");
        return true;
    }

    /// <summary>
    /// The depot upgrade waits on income, not on a count of buildings: what two fully upgraded fuel
    /// buildings pay, so a commander on starters reaches it without a gold mine.
    /// </summary>
    private bool HasDepotUpgradeIncome(FactionHQ hq)
    {
        // Heavy vehicles wait for the first gold mine.
        return GetIncomePerMinute(hq) >= GetIncomePerMinute(CommanderBuildKind.FuelExchange, MaxLevel)
                + GetIncomePerMinute(CommanderBuildKind.GasTerminal, MaxLevel)
            && CountMines(hq, CommanderBuildKind.Mine) > 0;
    }

    /// <summary>What this faction's income buildings pay a minute, before the AI multiplier.</summary>
    private float GetIncomePerMinute(FactionHQ hq)
    {
        // The AI stipend counts: a Champions commander on 100 + 75 per player is not poor, and
        // reading only its buildings had it play like one.
        float income = GetBaseIncomePerMinute() + GetAiStipendPerMinute(hq, CommanderGameAccess.GetLocalHq());
        foreach (KeyValuePair<Unit, int> entry in incomeLevels)
        {
            if (entry.Key != null && !entry.Key.disabled && entry.Key.NetworkHQ == hq)
            {
                income += GetIncomePerMinute(GetIncomeKind(entry.Key), entry.Value);
            }
        }

        return income;
    }

    /// <summary>
    /// The two fuel prefabs have no fallback: a Fuel Exchange that silently wears some other
    /// building is worse than a button that says it cannot be built. The mine falls back to any
    /// building, as it always has.
    /// </summary>
    private BuildingDefinition? ResolveIncomeDefinition(CommanderBuildKind kind)
    {
        if (incomeDefinitions.TryGetValue(kind, out BuildingDefinition? cached))
        {
            return cached;
        }

        Encyclopedia? encyclopedia = Encyclopedia.i;
        if (encyclopedia?.buildings == null)
        {
            return null;
        }

        BuildingDefinition? resolved = FindBuilding(encyclopedia, GetStage(kind).PrefabKeys)
            ?? (kind == CommanderBuildKind.Mine ? FindAnyBuilding(encyclopedia) : null);
        incomeDefinitions[kind] = resolved;
        CommanderPlugin.Log.LogInfo(resolved != null
            ? $"{GetIncomeName(kind)} will be built from the '{resolved.jsonKey}' prefab."
            : $"No prefab is available for a {GetIncomeName(kind)}, so it cannot be built.");
        return resolved;
    }

    private sealed class IncomeStage
    {
        internal IncomeStage(CommanderBuildKind kind, string name, string[] prefabKeys, float[] costs, float incomePerLevel)
        {
            Kind = kind;
            Name = name;
            PrefabKeys = prefabKeys;
            Costs = costs;
            IncomePerLevel = incomePerLevel;
        }

        internal CommanderBuildKind Kind { get; }

        internal string Name { get; }

        internal string[] PrefabKeys { get; }

        /// <summary>Index 0 builds it at level 1, index n takes it from level n to n + 1.</summary>
        internal float[] Costs { get; }

        internal float IncomePerLevel { get; }
    }
}

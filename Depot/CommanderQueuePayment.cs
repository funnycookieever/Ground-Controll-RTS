using System;
using System.Collections.Generic;
using System.Globalization;
using Mirage;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Pays for a guest's queued unit when it is queued, the way the host's own clicks are paid, so a
/// teammate cannot spend the balance during the build time.
/// </summary>
/// <remarks>
/// The host keeps what it took per guest: a queued unit spends from this deposit at the door, a
/// cancel draws it back into the faction's funds, and a guest who leaves pays the rest to the team.
/// </remarks>
internal static class CommanderQueuePayment
{
    private static readonly Dictionary<INetworkPlayer, Deposit> Deposits = new();
    private static readonly Dictionary<int, Action<float>> Waiting = new();
    private static int nextToken;

    internal static void Initialize()
    {
        CommanderNetwork.OnHost("pay", HostPay);
        CommanderNetwork.OnHost("pay.back", HostPayBack);
        CommanderNetwork.OnGuest("pay.done", OnPaid);
    }

    internal static void ResetSession()
    {
        Deposits.Clear();
        Waiting.Clear();
    }

    /// <summary>The exact amount, for the wire: <see cref="CommanderNetwork.Num(float)"/> keeps two
    /// decimals, and a deposit has to come back to the cent.</summary>
    internal static string Text(float amount)
    {
        return amount.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>Guest: asks the host to charge for <paramref name="definition"/> now. <paramref name="answer"/>
    /// gets what was paid, or 0 when the host refused (its notice has already said why).</summary>
    internal static void Buy(UnitDefinition definition, Action<float> answer)
    {
        if (!CommanderNetwork.HostHasMod)
        {
            CommanderNotice.Fail(CommanderNetwork.HostMissingReason);
            return;
        }

        int token = nextToken++;
        Waiting[token] = answer;
        CommanderNetwork.Forward("pay", CommanderNetwork.Num(token), definition.jsonKey);
    }

    /// <summary>Guest: hands back what a cancelled unit paid, and returns true. On the host and in
    /// singleplayer returns false, and the caller refunds the faction itself.</summary>
    internal static bool Return(float amount)
    {
        return CommanderNetwork.Forward("pay.back", Text(amount));
    }

    /// <summary>Host, inside a guest's request: this guest has <paramref name="amount"/> on deposit.</summary>
    internal static bool Holds(float amount)
    {
        return amount > 0f
            && CommanderNetwork.Sender is { } sender
            && Deposits.TryGetValue(sender, out Deposit deposit)
            && deposit.Held >= amount;
    }

    /// <summary>Host, inside a guest's request: spends <paramref name="amount"/> of the deposit, if it is there.</summary>
    internal static bool Draw(float amount)
    {
        if (!Holds(amount))
        {
            return false;
        }

        Deposits[CommanderNetwork.Sender!].Held -= amount;
        return true;
    }

    /// <summary>Host: a guest has gone, and its queues with it, so its deposit goes back to its team.</summary>
    internal static void NoteGuestLeft(INetworkPlayer guest)
    {
        if (Deposits.TryGetValue(guest, out Deposit deposit))
        {
            Deposits.Remove(guest);
            if (deposit.Hq != null && deposit.Held > 0d)
            {
                deposit.Hq.AddFunds((float)deposit.Held);
            }
        }
    }

    private static void HostPay(FactionHQ hq, string[] args)
    {
        UnitDefinition? definition = CommanderNetwork.Definition<UnitDefinition>(CommanderNetwork.Arg(args, 2));
        if (definition is VehicleDefinition vehicle && !CommanderFactionVehicleService.CanPurchase(vehicle, hq))
        {
            CommanderNotice.Fail($"{vehicle.unitName} is not available to this faction.");
            CommanderNetwork.Reply("pay.done", CommanderNetwork.Arg(args, 1), Text(0f));
            return;
        }

        float cost = definition != null ? Mathf.Max(0f, definition.value) : 0f;
        if (definition == null || cost <= 0f || !CommanderGameAccess.TrySpend(hq, cost))
        {
            string label = definition != null ? definition.unitName : "THAT UNIT";
            CommanderNotice.Fail($"NOT ENOUGH FUNDS FOR {label}");
            CommanderNetwork.Reply("pay.done", CommanderNetwork.Arg(args, 1), Text(0f));
            return;
        }

        INetworkPlayer sender = CommanderNetwork.Sender!;
        if (!Deposits.TryGetValue(sender, out Deposit deposit))
        {
            deposit = new Deposit();
            Deposits[sender] = deposit;
        }

        deposit.Hq = hq;
        deposit.Held += cost;
        CommanderNetwork.Reply("pay.done", CommanderNetwork.Arg(args, 1), Text(cost));
    }

    private static void HostPayBack(FactionHQ hq, string[] args)
    {
        float amount = CommanderNetwork.Float(args, 1);
        if (Draw(amount))
        {
            hq.AddFunds(amount);
        }
    }

    private static void OnPaid(string[] args)
    {
        int token = CommanderNetwork.Int(args, 1);
        if (Waiting.TryGetValue(token, out Action<float> answer))
        {
            Waiting.Remove(token);
            answer(CommanderNetwork.Float(args, 2));
        }
    }

    private sealed class Deposit
    {
        /// <summary>A double, so that a long match of adding and taking floats stays exact.</summary>
        internal double Held { get; set; }

        internal FactionHQ? Hq { get; set; }
    }
}

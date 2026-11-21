using System.Collections.Generic;

namespace GroundControlRts;

/// <summary>
/// One runnable check for <see cref="CommanderServiceRegistry"/>: registration order really is
/// execution order, the advanced tier really is skipped when the feature gate is off, and a
/// service that throws does not stop the ones registered after it.
/// <para>
/// It runs once from <see cref="CommanderPlugin"/> at load and logs to the BepInEx console,
/// because a Unity plugin has nowhere else to run a test. It touches no game API and costs
/// microseconds.
/// </para>
/// </summary>
internal static class CommanderServiceRegistryCheck
{
    internal static void Run()
    {
        CommanderServiceRegistry registry = new();
        List<string> log = new();
        registry.Register(new Probe("core1", log));
        registry.Register(new Probe("adv", log), CommanderTier.Advanced);
        registry.Register(new Probe("core2", log));

        List<string> failures = new();
        Expect(failures, "activate gated off", Record(log, () => registry.Activate(false)), "core1:Activate core2:Activate");
        Expect(failures, "activate gated on", Record(log, () => registry.Activate(true)), "core1:Activate adv:Activate core2:Activate");
        Expect(failures, "advanced only", Record(log, registry.ActivateAdvanced), "adv:Activate");
        Expect(failures, "tick gated off", Record(log, () => registry.TickActive(false)), "core1:TickActive core2:TickActive");
        Expect(failures, "tick gated on", Record(log, () => registry.TickActive(true)), "core1:TickActive adv:TickActive core2:TickActive");
        Expect(failures, "persistent gated off", Record(log, () => registry.TickPersistent(false)), "core1:TickPersistent core2:TickPersistent");

        // Teardown ignores the gate on purpose: the gate may have flipped since activation.
        Expect(failures, "deactivate ignores gate", Record(log, registry.Deactivate), "core1:Deactivate adv:Deactivate core2:Deactivate");
        Expect(failures, "reset ignores gate", Record(log, registry.ResetSession), "core1:ResetSession adv:ResetSession core2:ResetSession");

        // A service that throws must not take the services after it down with it.
        CommanderServiceRegistry faulty = new() { LogFailures = false };
        faulty.Register(new Probe("core1", log));
        faulty.Register(new Probe("bad", log, throws: true));
        faulty.Register(new Probe("core2", log));
        Expect(failures, "tick survives a throw", Record(log, () => faulty.TickPersistent(false)), "core1:TickPersistent bad:TickPersistent core2:TickPersistent");
        Expect(failures, "deactivate survives a throw", Record(log, faulty.Deactivate), "core1:Deactivate bad:Deactivate core2:Deactivate");
        Expect(failures, "throws counted", faulty.Failures.ToString(System.Globalization.CultureInfo.InvariantCulture), "2");

        if (failures.Count == 0)
        {
            return;
        }

        for (int i = 0; i < failures.Count; i++)
        {
            CommanderPlugin.Log.LogError($"Service registry self-check FAILED: {failures[i]}");
        }
    }

    private static string Record(List<string> log, System.Action phase)
    {
        log.Clear();
        phase();
        return string.Join(" ", log.ToArray());
    }

    private static void Expect(List<string> failures, string name, string actual, string expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected '{expected}', got '{actual}'");
        }
    }

    private sealed class Probe
        : ICommanderActivate, ICommanderDeactivate, ICommanderTickActive, ICommanderTickPersistent, ICommanderResetSession
    {
        private readonly string name;
        private readonly List<string> log;
        private readonly bool throws;

        internal Probe(string name, List<string> log, bool throws = false)
        {
            this.name = name;
            this.log = log;
            this.throws = throws;
        }

        public void Activate()
        {
            log.Add($"{name}:Activate");
            Fail();
        }

        public void Deactivate()
        {
            log.Add($"{name}:Deactivate");
            Fail();
        }

        public void TickActive()
        {
            log.Add($"{name}:TickActive");
            Fail();
        }

        public void TickPersistent()
        {
            log.Add($"{name}:TickPersistent");
            Fail();
        }

        public void ResetSession()
        {
            log.Add($"{name}:ResetSession");
            Fail();
        }

        private void Fail()
        {
            if (throws)
            {
                throw new System.InvalidOperationException("self-check probe");
            }
        }
    }
}

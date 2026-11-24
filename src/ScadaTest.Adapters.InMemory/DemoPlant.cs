using ScadaTest.Core;

namespace ScadaTest.Adapters.InMemory;

/// <summary>
/// A demo model matching the sample scenarios: pump P-101, level LT-301 with a Hi alarm at 85,
/// and temperature TT-201. Select it with the adapter setting <c>model: demo</c>.
/// </summary>
public static class DemoPlant
{
    public static InMemoryPlant Configure(InMemoryPlant plant) => plant
        .WithTag("P-101.Mode", "Auto")
        .WithTag("P-101.StartCmd", false)
        .WithTag("P-101.StopCmd", false)
        .WithTag("P-101.Running", false)
        .WithTag("P-101.Fault", false)
        .WithTag("LT-301.PV", 50.0)
        .WithTag("TT-201.PV", 20.0)
        .WithHiAlarm("LT-301", "LT-301.PV", 85, 500, "LT-301 level high")
        .OnWrite((p, tag, value) =>
        {
            if (tag.Name.Equals("P-101.StartCmd", StringComparison.OrdinalIgnoreCase) && ValueComparer.AreEqual(value, true))
            {
                if (!ValueComparer.AreEqual(p.Get("P-101.Fault"), true))
                    p.Set("P-101.Running", true);
                p.Set("P-101.StartCmd", false);
            }
            else if (tag.Name.Equals("P-101.StopCmd", StringComparison.OrdinalIgnoreCase) && ValueComparer.AreEqual(value, true))
            {
                p.Set("P-101.Running", false);
                p.Set("P-101.StopCmd", false);
            }
            return Task.CompletedTask;
        })
        .WithFault("trip", (p, device) =>
        {
            p.Set($"{device}.Fault", true);
            p.Set($"{device}.Running", false);
            return Task.CompletedTask;
        })
        .WithScenario("normal_operation", (p, _) => Task.CompletedTask)
        .WithScenario("temperature_step", (p, args) =>
        {
            var to = args?.GetValueOrDefault("to") is { } v && ValueComparer.TryDouble(v, out var d) ? d : 60.0;
            p.Set("TT-201.PV", to);
            return Task.CompletedTask;
        })
        .WithSymbol("P-101", p =>
            ValueComparer.AreEqual(p.Get("P-101.Fault"), true) ? new SymbolState("fault")
            : ValueComparer.AreEqual(p.Get("P-101.Running"), true) ? new SymbolState("running")
            : new SymbolState("stopped"));
}

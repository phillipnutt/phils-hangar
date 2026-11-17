using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PhilsReusabilitySuite.Core;

namespace PhilsReusabilitySuite
{
    /// <summary>
    /// A part's cargo: what's packed in its KSP inventory (ModuleInventoryPart) and in its Kerbal Inventory System
    /// containers (KIS's ModuleKISInventory). Each keeps what it holds in its own saved module, and adds what that's worth
    /// and weighs to the part's module cost and mass. Cargo comes home as it is: what was used up or taken out in flight is
    /// gone from the part, and from its price and mass (code scan R2; KIS cargo too, Phil 2026-11-16).
    /// </summary>
    static class Cargo
    {
        /// <summary>A part's cargo as it came home, in place of the cargo it launched with.</summary>
        public static void CarryHome(ProtoPartSnapshot ps, Node part)
        {
            Swap(part, name => ps.modules.Where(m => m.moduleName == name && m.moduleValues != null).Select(m => AsModule(m, name)));
        }

        /// <summary>Another node's cargo in place of this part's (Restore: the cargo the ship has now, not its blueprint's).</summary>
        public static void Carry(Node from, Node to)
        {
            Swap(to, name => from.Children("MODULE").Where(m => m.Get("name") == name).Select(m => m.Clone()));
        }

        /// <summary>Puts these cargo modules in place of the part's own (the same module, by name and order), and moves the
        /// part's module cost and mass by the difference in what they hold.</summary>
        static void Swap(Node part, Func<string, IEnumerable<Node>> modulesNamed)
        {
            double cost = 0, mass = 0;
            foreach (string name in Hardware.CargoModules)
            {
                List<Node> now = modulesNamed(name).ToList();
                int k = 0;
                for (int j = 0; j < part.Nodes.Count && k < now.Count; j++)
                {
                    if (part.Nodes[j].Name != "MODULE" || part.Nodes[j].Get("name") != name) continue;
                    Value(part.Nodes[j], out double wasCost, out double wasMass);
                    Value(now[k], out double nowCost, out double nowMass);
                    cost += nowCost - wasCost;
                    mass += nowMass - wasMass;
                    part.Nodes[j] = now[k++];
                }
            }
            if (Math.Abs(cost) > 1e-4) part.Set("modCost", ((float)(LaunchRecord.Num(part.Get("modCost")) + cost)).ToString("R", CultureInfo.InvariantCulture));
            if (Math.Abs(mass) > 1e-7) part.Set("modMass", ((float)(LaunchRecord.Num(part.Get("modMass")) + mass)).ToString("R", CultureInfo.InvariantCulture));
        }

        static Node AsModule(ProtoPartModuleSnapshot m, string name)
        {
            Node n = KspNodes.FromKsp(m.moduleValues);
            n.Name = "MODULE";
            if (n.Get("name") == null) n.Values.Insert(0, new KeyValuePair<string, string>("name", name));
            return n;
        }

        /// <summary>What a cargo module's contents add to the part holding it: its module cost and mass.</summary>
        public static void Value(Node module, out double cost, out double mass)
        {
            if (module?.Get("name") == "ModuleKISInventory") KisValue(module, out cost, out mass);
            else StockValue(module, out cost, out mass);
        }

        /// <summary>What a KSP inventory's stored parts add to the part holding it, as KSP works it out: each stored part's
        /// price with what it holds, and its mass with what it holds, times how many are stacked in its slot.</summary>
        static void StockValue(Node module, out double cost, out double mass)
        {
            cost = mass = 0;
            Node stored = module?.Child("STOREDPARTS");
            if (stored == null) return;
            foreach (Node slot in stored.Children("STOREDPART"))
            {
                Node p = slot.Child("PART");
                AvailablePart ap = p != null ? PartLoader.getPartInfoByName(p.Get("name") ?? slot.Get("partName")) : null;
                if (ap == null) continue;
                ShipConstruction.GetPartCostsAndMass(KspNodes.ToKsp(p, "PART"), ap, out float dry, out float fuel, out float dryMass, out float fuelMass);
                double held = 0;
                foreach (Node r in p.Children("RESOURCE"))
                {
                    PartResourceDefinition def = PartResourceLibrary.Instance?.GetDefinition(r.Get("name"));
                    if (def != null) held += LaunchRecord.Num(r.Get("amount")) * def.density;
                }
                double own = p.Get("mass") != null ? LaunchRecord.Num(p.Get("mass")) : dryMass;
                int count = Math.Max(1, (int)LaunchRecord.Num(slot.Get("quantity")));
                cost += (dry + fuel) * count;
                mass += (own + held) * count;
            }
        }

        /// <summary>
        /// What a KIS container's items add to the part holding it, as KIS works it out (its module cost and mass): each
        /// item's own price and mass as KIS saved them, what the item's tanks hold now, and what's packed inside it if it's a
        /// container itself, times how many are stacked in its slot. KIS saves all of that on each ITEM.
        /// </summary>
        static void KisValue(Node module, out double cost, out double mass)
        {
            cost = mass = 0;
            foreach (Node item in module.Children("ITEM"))
            {
                int count = (int)LaunchRecord.Num(item.Get("quantity"));
                if (count <= 0) continue;   // KIS won't load an item with none in its slot
                // KIS works out an item's resources from its part each time it loads it, so this does too.
                double resCost = 0, resMass = 0;
                Node p = item.Child("PART");
                if (p != null)
                    foreach (Node r in p.Children("RESOURCE"))
                    {
                        PartResourceDefinition def = PartResourceLibrary.Instance?.GetDefinition(r.Get("name"));
                        if (def == null) continue;
                        float amount = (float)LaunchRecord.Num(r.Get("amount"));
                        resCost += amount * def.unitCost;
                        resMass += amount * def.density;
                    }
                cost += (LaunchRecord.Num(item.Get("dryCost")) + resCost + LaunchRecord.Num(item.Get("contentCost"))) * count;
                mass += (LaunchRecord.Num(item.Get("dryMass")) + resMass + LaunchRecord.Num(item.Get("contentMass"))) * count;
            }
        }
    }
}

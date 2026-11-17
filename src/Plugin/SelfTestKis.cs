using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using PhilsReusabilitySuite.Core;
using UnityEngine;

namespace PhilsReusabilitySuite
{
    /// <summary>
    /// Cargo comes home as it is, Kerbal Inventory System cargo included (Phil, 2026-11-16), in game (test harness only).
    /// A probe ship stands on a storage unit, with a tank on top to drop. The unit has KSP's own inventory, and a KIS one
    /// when KIS is installed (KIS adds it). Two repair kits go in KSP's; four KIS guides and a full Oscar-B tank in KIS's.
    /// It's built, launched from the hangar, drops the tank, uses up a kit and three guides and empties the Oscar-B's fuel,
    /// and is recovered. The stored unit holds what's left and is priced with it; KIS prices it the same when it's opened
    /// in the VAB (so it's exactly the hangar ship); and Restore (the tank is missing) charges for the new tank only, not
    /// for cargo that's gone. Without KIS, the KSP inventory and Restore checks still run.
    /// </summary>
    class SelfTestKis : MonoBehaviour
    {
        public const string SaveName = "zz_PRSSelfTestKis";
        const string ShipName = "Cargo Test";
        const string Kit = "evaRepairKit";
        const string Unit = "CargoStorageUnit";
        const string Guide = "KIS.guide";
        const string Oscar = "miniFuelTank";
        static bool running;
        int pass, fail;

        public static void Begin()
        {
            if (running) return;
            running = true;
            var go = new GameObject("PRSSelfTestKis");
            DontDestroyOnLoad(go);
            go.AddComponent<SelfTestKis>();
        }

        void Check(bool ok, string what)
        {
            if (ok) { pass++; Debug.Log("[PRS-TEST] PASS " + what); }
            else { fail++; Debug.LogError("[PRS-TEST] FAIL " + what); }
        }

        static void Note(string s) { Debug.Log("[PRS-TEST] " + s); }
        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        void Start() { StartCoroutine(Run()); }

        IEnumerator Run()
        {
            bool designRule = Suite.Settings.RequireDesign;
            Suite.Settings.RequireDesign = false;
            IEnumerator inner = Body();
            while (true)
            {
                object cur;
                try { if (!inner.MoveNext()) break; cur = inner.Current; }
                catch (Exception ex) { Check(false, "the test threw " + ex); break; }
                yield return cur;
            }
            Note("done: " + pass + " passed, " + fail + " failed");
            Suite.Settings.RequireDesign = designRule;
            Debug.Log("[PRS-TEST-DONE]");
            Destroy(gameObject);
        }

        IEnumerator WaitFor(Func<bool> cond, float timeout, string what)
        {
            float t0 = Time.realtimeSinceStartup;
            while (!cond())
            {
                if (Time.realtimeSinceStartup - t0 > timeout) { Check(false, what + " (timed out)"); yield break; }
                yield return null;
            }
        }

        static object Member(object o, string name)
        {
            if (o == null) return null;
            Type t = o as Type ?? o.GetType();
            object target = o is Type ? null : o;
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            PropertyInfo p = t.GetProperty(name, all);
            if (p != null) return p.GetValue(target, null);
            FieldInfo f = t.GetField(name, all);
            return f?.GetValue(target);
        }

        // ---- Reading the cargo ----

        static Node UnitIn(Node craft) => craft?.Children("PART").FirstOrDefault(p => Hardware.PartName(p) == Unit);
        static Node TankIn(Node craft) => craft?.Children("PART").FirstOrDefault(p => Hardware.PartName(p) == "fuelTank");
        static double Price(Node part) => part != null ? KspCosts.Instance.Cost(part).Dry : -1;

        /// <summary>How many kits a part node's KSP inventory holds.</summary>
        static int KitsIn(Node part)
        {
            Node stored = part?.Children("MODULE").FirstOrDefault(m => m.Get("name") == "ModuleInventoryPart")?.Child("STOREDPARTS");
            if (stored == null) return 0;
            return stored.Children("STOREDPART").Where(s => (s.Get("partName") ?? s.Child("PART")?.Get("name")) == Kit)
                         .Sum(s => Math.Max(1, (int)LaunchRecord.Num(s.Get("quantity"))));
        }

        static Node KisModuleIn(Node part) => part?.Children("MODULE").FirstOrDefault(m => m.Get("name") == "ModuleKISInventory");
        static Node KisItemIn(Node part, string name) => KisModuleIn(part)?.Children("ITEM").FirstOrDefault(i => i.Get("partName") == name);
        static int CountOf(Node item) => (int)LaunchRecord.Num(item?.Get("quantity"));
        static double FuelOf(Node item) => LaunchRecord.Num(item?.Child("PART")?.Children("RESOURCE").FirstOrDefault(r => r.Get("name") == "LiquidFuel")?.Get("amount"));

        /// <summary>What a part node's KIS inventory holds, as the Suite prices it.</summary>
        static double KisValueIn(Node part)
        {
            Node m = KisModuleIn(part);
            if (m == null) return 0;
            Cargo.Value(m, out double cost, out _);
            return cost;
        }

        static PartModule KisOf(Part p) => p?.Modules.Cast<PartModule>().FirstOrDefault(m => m.moduleName == "ModuleKISInventory");
        static double KisCost(PartModule kis) => kis != null ? (double)Member(kis, "contentsCost") : -1;
        static double KisMass(PartModule kis) => kis != null ? (double)Member(kis, "contentsMass") : -1;
        static object KisItem(PartModule kis, string partName) =>
            kis == null ? null : ((IDictionary)Member(kis, "items")).Values.Cast<object>().FirstOrDefault(i => (Member(i, "availablePart") as AvailablePart)?.name == partName);
        static int KisCount(object item) => item != null ? (int)Member(item, "quantity") : 0;
        static double KisFuel(object item)
        {
            ConfigNode n = Member(item, "partNode") as ConfigNode;
            ConfigNode r = n?.GetNodes("RESOURCE").FirstOrDefault(x => x.GetValue("name") == "LiquidFuel");
            return r != null ? LaunchRecord.Num(r.GetValue("amount")) : -1;
        }

        IEnumerator Body()
        {
            // 1. A career game of its own, every technology researched, plenty of funds.
            GameParameters gp = GameParameters.GetDefaultParameters(Game.Modes.CAREER, GameParameters.Preset.Normal);
            Game g = GamePersistence.CreateNewGame(SaveName, Game.Modes.CAREER, gp, "Squad/Flags/default", GameScenes.SPACECENTER, EditorFacility.VAB);
            HighLogic.CurrentGame = g;
            HighLogic.SaveFolder = SaveName;
            g.startScene = GameScenes.SPACECENTER;
            g.Start();
            yield return WaitFor(() => HighLogic.LoadedScene == GameScenes.SPACECENTER && PRSScenario.Instance != null && Funding.Instance != null && ResearchAndDevelopment.Instance != null, 120, "the career game reached the Space Center");
            yield return new WaitForSeconds(4f);
            Funding.Instance.SetFunds(5000000, TransactionReasons.Cheating);
            foreach (ProtoRDNode rd in AssetBase.RnDTechTree.GetTreeNodes())
            {
                ProtoTechNode t = rd.tech;
                t.state = RDTech.State.Available;
                ResearchAndDevelopment.Instance.SetTechState(t.techID, t);
            }

            bool kis = AssemblyLoader.loadedAssemblies.Any(a => a.assembly.GetType("KIS.ModuleKISInventory", false) != null);
            Note("Cargo KIS is " + (kis ? "installed" : "not installed: only KSP's own inventory is checked"));
            if (kis && (PartLoader.getPartInfoByName(Guide) == null || PartLoader.getPartInfoByName(Oscar) == null)) { Check(false, "KIS's guide and the Oscar-B are installed"); yield break; }

            // 2. The ship, top to bottom: a tank to drop, a decoupler, a probe core, and the storage unit it stands on.
            var bits = new List<SelfTestScan.Bit>
            {
                new SelfTestScan.Bit { Part = "probeCoreOcto.v2" },                                         // 0
                new SelfTestScan.Bit { Part = "Decoupler.2", Parent = 0, Above = true, Decoupler = true },  // 1
                new SelfTestScan.Bit { Part = "fuelTank", Parent = 1, Above = true },                       // 2
                new SelfTestScan.Bit { Part = Unit, Parent = 0 },                                           // 3
            };
            ConfigNode file = SelfTestScan.ShipCraft(bits, ShipName);
            string dir = Path.Combine(KSPUtil.ApplicationRootPath, "saves", SaveName, "Ships", "VAB");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, ShipName + ".craft");
            file.Save(path);

            // 3. In the VAB: load it, and pack the storage unit.
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            EditorDriver.StartupBehaviour = EditorDriver.StartupBehaviours.START_CLEAN;
            EditorDriver.StartEditor(EditorFacility.VAB);
            yield return WaitFor(() => HighLogic.LoadedScene == GameScenes.EDITOR && EditorLogic.fetch != null && EditorAddon.Instance != null, 90, "the VAB opened");
            yield return new WaitForSeconds(3f);
            EditorLogic.LoadShipFromFile(path);
            yield return WaitFor(() => EditorLogic.fetch.ship != null && EditorLogic.fetch.ship.parts.Count == bits.Count, 30, "the ship loaded (" + bits.Count + " parts)");
            yield return new WaitForSeconds(2f);
            Part unitPart = EditorLogic.fetch.ship.parts.FirstOrDefault(p => p.partInfo.name == Unit);
            ModuleInventoryPart inv = unitPart?.FindModuleImplementing<ModuleInventoryPart>();
            bool stored = inv != null && inv.StoreCargoPartAtSlot(Kit, 0) && inv.UpdateStackAmountAtSlot(0, 2, null);
            PartModule kisInv = null;
            if (kis)
            {
                kisInv = KisOf(unitPart);
                Check(kisInv != null, "KIS gives the storage unit a KIS inventory");
                if (kisInv == null) yield break;
                MethodInfo add = kisInv.GetType().GetMethod("AddItem", new[] { typeof(Part), typeof(int), typeof(int) });
                add?.Invoke(kisInv, new object[] { PartLoader.getPartInfoByName(Guide).partPrefab, 4, -1 });
                add?.Invoke(kisInv, new object[] { PartLoader.getPartInfoByName(Oscar).partPrefab, 1, -1 });
            }
            GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
            yield return new WaitForSeconds(1f);
            Check(stored && inv.TotalAmountOfPartStored(Kit) == 2, "two repair kits in its KSP inventory (" + (inv != null ? inv.TotalAmountOfPartStored(Kit) : -1) + ")");
            Node editorUnit = UnitIn(EditorAddon.EditorCraft());
            if (kis)
            {
                object oscar = KisItem(kisInv, Oscar);
                Check(KisCount(KisItem(kisInv, Guide)) == 4 && KisCount(oscar) == 1 && KisFuel(oscar) > 1,
                      "four guides and a fuelled Oscar-B in its KIS inventory (" + KisCount(KisItem(kisInv, Guide)) + " guides, Oscar-B fuel " + F(KisFuel(oscar)) + ")");
                Check(CountOf(KisItemIn(editorUnit, Guide)) == 4 && KisItemIn(editorUnit, Oscar) != null, "the editor's craft saves them");
                // The Suite works out what KIS cargo is worth from the saved items; KIS works it out from its live ones.
                Node km = KisModuleIn(editorUnit);
                Cargo.Value(km, out double ourCost, out double ourMass);
                Check(km != null && Math.Abs(ourCost - KisCost(kisInv)) < 0.01 && Math.Abs(ourMass - KisMass(kisInv)) < 1e-6,
                      "the Suite prices the KIS cargo as KIS does: " + F(ourCost) + " (KIS " + F(KisCost(kisInv)) + "), " + F(ourMass * 1000) + " kg (KIS " + F(KisMass(kisInv) * 1000) + ")");
            }
            Note("Cargo storage unit in the VAB: price " + F(Price(editorUnit)) + ", KIS cargo " + F(KisCost(kisInv)));

            // 4. Build it (outsourced, so it's done at once).
            EditorAddon.Instance.OpenBuildPanel();
            BuildPanel panel = EditorAddon.Instance.Panel;
            Check(panel.Confirm(), "building it " + panel.Error);
            PRSScenario sc = PRSScenario.Instance;
            BuildJob job = sc.Jobs.LastOrDefault();
            if (job != null) sc.Outsource(job, out string oerr);
            HangarShip h = sc.Hangar.FirstOrDefault(x => x.Name == ShipName && x.Ready);
            Check(h != null && h.DisplayName == ShipName + " #1", "it's ready in the hangar (" + h?.DisplayName + ")");
            if (h == null) yield break;
            string shipId = h.Id;
            if (EditorAddon.Instance.Panel != null) EditorAddon.Instance.Panel.Showing = false;
            Note("Cargo stored storage unit before flying: price " + F(Price(UnitIn(h.Craft))) + ", modCost " + UnitIn(h.Craft).Get("modCost"));

            // 5. Launch it from the hangar, drop the tank, and use up some cargo.
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            HighLogic.LoadScene(GameScenes.SPACECENTER);
            yield return WaitFor(() => HighLogic.LoadedScene == GameScenes.SPACECENTER && PRSScenario.Instance != null, 90, "the Space Center opened");
            yield return new WaitForSeconds(3f);
            sc = PRSScenario.Instance;
            h = sc.HangarOf(shipId);
            Check(h != null && sc.LaunchReady(h, out string lerr), "launching it from the hangar");
            yield return WaitFor(() => HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.loaded && !FlightGlobals.ActiveVessel.packed, 120, "it reached the pad");
            yield return new WaitForSeconds(5f);
            Vessel v = FlightGlobals.ActiveVessel;
            ModuleDecouplerBase dec = v.parts.Select(p => p.FindModuleImplementing<ModuleDecouplerBase>()).FirstOrDefault(m => m != null);
            dec?.Decouple();
            yield return null;
            yield return null;
            v = FlightGlobals.ActiveVessel;
            foreach (Vessel other in FlightGlobals.Vessels.Where(x => x != v && x.parts.Any(p => p.partInfo.name == "fuelTank")).ToList()) other.Die();
            yield return new WaitForSeconds(2f);
            yield return WaitFor(() => v.srfSpeed < 0.05, 30, "the ship is standing still on the pad");
            yield return new WaitForSeconds(1f);
            Check(v.parts.Count == 3 && !v.parts.Any(p => p.partInfo.name == "fuelTank"), "the tank is gone (" + v.parts.Count + " parts left)");
            Part flyingUnit = v.parts.FirstOrDefault(p => p.partInfo.name == Unit);
            ModuleInventoryPart flyingInv = flyingUnit?.FindModuleImplementing<ModuleInventoryPart>();
            bool used = flyingInv != null && flyingInv.RemoveNPartsFromInventory(Kit, 1, false);
            Check(used && flyingInv.TotalAmountOfPartStored(Kit) == 1, "a kit used up in flight, one left (" + (flyingInv != null ? flyingInv.TotalAmountOfPartStored(Kit) : -1) + ")");
            double flyingKis = 0;
            if (kis)
            {
                PartModule flyingKisInv = KisOf(flyingUnit);
                object guides = KisItem(flyingKisInv, Guide), oscar = KisItem(flyingKisInv, Oscar);
                double before = KisCost(flyingKisInv);
                object took = guides?.GetType().GetMethod("StackRemove")?.Invoke(guides, new object[] { 3 });
                oscar?.GetType().GetMethod("UpdateResource")?.Invoke(oscar, new object[] { "LiquidFuel", 0.0, false });
                flyingKis = KisCost(flyingKisInv);
                Check(took is int n && n == 3 && KisCount(guides) == 1 && KisFuel(oscar) == 0,
                      "three guides used up and the Oscar-B's fuel gone (" + KisCount(guides) + " guide left, fuel " + F(KisFuel(oscar)) + ")");
                Note("Cargo KIS cargo in flight: " + F(before) + " before, " + F(flyingKis) + " after");
            }
            yield return new WaitForSeconds(1f);
            float flyingModMass = flyingUnit != null ? flyingUnit.GetModuleMass(flyingUnit.partInfo.partPrefab.mass) : -1;

            // 6. Recover it to the hangar.
            var altimeter = FindObjectOfType<KSP.UI.Screens.AltimeterSliderButtons>();
            typeof(KSP.UI.Screens.AltimeterSliderButtons).GetMethod("recoverVessel", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(altimeter, null);
            yield return new WaitForSeconds(1f);
            RecoverPrompt.Current?.TestChoose(RecoverChoice.Hangar);
            yield return WaitFor(() => HighLogic.LoadedScene == GameScenes.SPACECENTER && PRSScenario.Instance != null && Funding.Instance != null, 120, "recovery returned to the Space Center");
            yield return new WaitForSeconds(4f);
            sc = PRSScenario.Instance;
            h = sc.HangarOf(shipId);
            Check(h != null && h.PartCount == 3 && h.Missing == 1, "it's back in the hangar, 3 parts, missing the tank (" + h?.PartCount + " parts, " + h?.Missing + " missing)");
            if (h == null) yield break;

            // The stored unit holds what's left, and is priced and weighed with it.
            Node unitStored = UnitIn(h.Craft);
            double kitPrice = PartLoader.getPartInfoByName(Kit).cost, unitList = PartLoader.getPartInfoByName(Unit).cost;
            double expected = unitList + kitPrice + flyingKis;
            Note("Cargo stored storage unit after flying: price " + F(Price(unitStored)) + ", modCost " + unitStored.Get("modCost") + ", modMass " + unitStored.Get("modMass") + " (in flight " + F(flyingModMass) + ")");
            Check(KitsIn(unitStored) == 1, "the stored unit's KSP inventory holds the one kit left (" + KitsIn(unitStored) + ")");
            if (kis)
            {
                Check(CountOf(KisItemIn(unitStored, Guide)) == 1, "its KIS inventory holds the one guide left (" + CountOf(KisItemIn(unitStored, Guide)) + ")");
                Check(KisItemIn(unitStored, Oscar) != null && FuelOf(KisItemIn(unitStored, Oscar)) == 0, "and the Oscar-B, empty (fuel " + F(FuelOf(KisItemIn(unitStored, Oscar))) + ")");
                Check(Math.Abs(KisValueIn(unitStored) - flyingKis) < 0.01, "its KIS cargo is worth what it was in flight: " + F(KisValueIn(unitStored)) + " (flight " + F(flyingKis) + ")");
            }
            Check(Math.Abs(Price(unitStored) - expected) < 0.5, "it's priced with what's left: " + F(Price(unitStored)) + " (list " + F(unitList) + " + a kit " + F(kitPrice) + " + KIS cargo " + F(flyingKis) + ")");
            Check(Math.Abs(LaunchRecord.Num(unitStored.Get("modMass")) - flyingModMass) < 0.001, "and weighs what it held in flight: " + unitStored.Get("modMass") + " t of cargo (flight " + F(flyingModMass) + ")");

            // 7. Restore (the tank is missing): the unit keeps its cargo as it is, and the build charges for the new tank only.
            Node restored = sc.RestoreCraft(h, out int newParts);
            Node restoredUnit = UnitIn(restored);
            Check(restored != null && newParts == 1, "Restore puts back the tank (" + newParts + " new)");
            Check(KitsIn(restoredUnit) == 1 && (!kis || CountOf(KisItemIn(restoredUnit, Guide)) == 1), "Restore keeps the unit's cargo as it came home (" + KitsIn(restoredUnit) + " kit, " + CountOf(KisItemIn(restoredUnit, Guide)) + " guide)");
            Check(Math.Abs(Price(restoredUnit) - Price(unitStored)) < 0.5, "and prices the unit as stored: " + F(Price(restoredUnit)) + " (stored " + F(Price(unitStored)) + ")");
            Quote rq = restored != null ? sc.QuoteFor(restored, ShipName, null) : null;
            double tankNew = Price(TankIn(restored));
            Check(rq != null && Math.Abs(rq.NewPrice - tankNew) < 0.5, "Restore charges for the new tank only: new parts " + F(rq?.NewPrice ?? -1) + " (the tank " + F(tankNew) + ")");

            // 8. Opened from the hangar in the VAB: KIS loads the cargo as it came home and prices it the same, so it's
            // exactly the hangar ship.
            EditorAddon.OpenHangarShip(h);
            yield return WaitFor(() => HighLogic.LoadedScene == GameScenes.EDITOR && EditorLogic.fetch != null && EditorAddon.Instance != null && EditorLogic.fetch.ship != null && EditorLogic.fetch.ship.parts.Count == 3, 90, "Open in the VAB went to the VAB with the ship");
            yield return new WaitForSeconds(3f);
            sc = PRSScenario.Instance;
            h = sc.HangarOf(shipId);
            Part liveUnit = EditorLogic.fetch.ship.parts.FirstOrDefault(p => p.partInfo.name == Unit);
            ModuleInventoryPart liveInv = liveUnit?.FindModuleImplementing<ModuleInventoryPart>();
            Check(liveInv != null && liveInv.TotalAmountOfPartStored(Kit) == 1, "in the VAB its KSP inventory holds one kit (" + (liveInv != null ? liveInv.TotalAmountOfPartStored(Kit) : -1) + ")");
            if (kis)
            {
                PartModule liveKis = KisOf(liveUnit);
                object oscar = KisItem(liveKis, Oscar);
                Check(KisCount(KisItem(liveKis, Guide)) == 1 && KisCount(oscar) == 1 && KisFuel(oscar) == 0,
                      "and its KIS inventory one guide and the empty Oscar-B (" + KisCount(KisItem(liveKis, Guide)) + " guide, fuel " + F(KisFuel(oscar)) + ")");
                Check(Math.Abs(KisCost(liveKis) - flyingKis) < 0.01, "KIS prices that cargo as it was in flight: " + F(KisCost(liveKis)) + " (flight " + F(flyingKis) + ")");
            }
            Node opened = EditorAddon.EditorCraft();
            Check(Math.Abs(Price(UnitIn(opened)) - Price(UnitIn(h.Craft))) < 0.5, "the VAB prices the unit as stored: " + F(Price(UnitIn(opened))) + " (stored " + F(Price(UnitIn(h.Craft))) + ")");
            Check(sc.ExactShipOf(opened)?.Id == shipId, "the VAB sees it as exactly the hangar ship");
        }
    }
}

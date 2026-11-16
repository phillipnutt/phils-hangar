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
    /// The 2026-11-16 code scan's fixes, in game (test harness only). A probe ship of stock parts and a Phil's Iterative
    /// Parts battery, with a storage unit of repair kits and a strut across its decoupler, is built, launched from the
    /// hangar, drops its lower stage, uses up some kits, and is recovered and opened again:
    /// R3: recovery charges what the Recover prompt said (the strut left without its other end goes to the parts bin);
    /// R2: the kits used up in flight come off the stored part's price, so the reopened ship is still exactly the hangar ship;
    /// R1: moved from the VAB to the SPH, it's still the ship that was opened: the flown battery keeps its Mk, and Build
    /// keeps its name, number and flight log.
    /// </summary>
    class SelfTestScan : MonoBehaviour
    {
        public const string SaveName = "zz_PRSSelfTestScan";
        const string ShipName = "Scan Test";
        const string Kit = "evaRepairKit";
        const string Unit = "CargoStorageUnit";
        static bool running;
        int pass, fail;

        public static void Begin()
        {
            if (running) return;
            running = true;
            var go = new GameObject("PRSSelfTestScan");
            DontDestroyOnLoad(go);
            go.AddComponent<SelfTestScan>();
        }

        void Check(bool ok, string what)
        {
            if (ok) { pass++; Debug.Log("[PRS-TEST] PASS " + what); }
            else { fail++; Debug.LogError("[PRS-TEST] FAIL " + what); }
        }

        static void Note(string s) { Debug.Log("[PRS-TEST] " + s); }
        static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

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

        static void Shot(string name)
        {
            string dir = Path.Combine(KSPUtil.ApplicationRootPath, "KSPTestHarness", "out");
            Directory.CreateDirectory(dir);
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, "prs_" + name + ".png"));
        }

        static Type PipType(string name) => AssemblyLoader.loadedAssemblies.Select(a => a.assembly.GetType(name, false)).FirstOrDefault(x => x != null);

        static object Member(object o, string name)
        {
            Type t = o as Type ?? o.GetType();
            object target = o is Type ? null : o;
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            PropertyInfo p = t.GetProperty(name, all);
            if (p != null) return p.GetValue(target, null);
            FieldInfo f = t.GetField(name, all);
            return f?.GetValue(target);
        }

        /// <summary>A part node's own Mk, as saved.</summary>
        static string MkIn(Node part) => part?.Children("MODULE").FirstOrDefault(m => m.Get("name") == "ModulePipPart")?.Get("mk");

        /// <summary>A live part's Mk.</summary>
        static int LiveMk(Part p)
        {
            PartModule m = p?.Modules.Cast<PartModule>().FirstOrDefault(x => x.moduleName == "ModulePipPart");
            object v = m?.GetType().GetField("mk")?.GetValue(m);
            return v is int i ? i : -1;
        }

        /// <summary>How many kits a part node's inventory holds.</summary>
        static int KitsIn(Node part)
        {
            Node stored = part?.Children("MODULE").FirstOrDefault(m => m.Get("name") == "ModuleInventoryPart")?.Child("STOREDPARTS");
            if (stored == null) return 0;
            return stored.Children("STOREDPART").Where(s => (s.Get("partName") ?? s.Child("PART")?.Get("name")) == Kit)
                         .Sum(s => Math.Max(1, (int)LaunchRecord.Num(s.Get("quantity"))));
        }

        static Node UnitIn(Node craft) => craft?.Children("PART").FirstOrDefault(p => Hardware.PartName(p) == Unit);
        static Node PipIn(Node craft) => craft?.Children("PART").FirstOrDefault(p => MkIn(p) != null);
        static Part LivePip() => EditorLogic.fetch?.ship?.parts.FirstOrDefault(p => LiveMk(p) >= 0);

        // ---- The ship ----

        sealed class Bit { public string Part, Program; public int Parent = -1; public bool Radial, Above; public float X; public int StrutTo = -1; public bool Decoupler; }

        /// <summary>The ship as a craft file (PIP fills in its part's build). A strut is a surface part with its target's
        /// craft id in PARTDATA, as KSP saves one.</summary>
        static ConfigNode ShipCraft(List<Bit> bits)
        {
            var c = new ConfigNode("");
            c.AddValue("ship", ShipName);
            c.AddValue("version", "1.12.5");
            c.AddValue("description", "");
            c.AddValue("type", "VAB");
            c.AddValue("size", "3,6,3");
            var refs = bits.Select((b, i) => b.Part + "_" + (2001 + i).ToString(CultureInfo.InvariantCulture)).ToList();
            var y = new float[bits.Count];
            for (int i = 0; i < bits.Count; i++)
            {
                Bit b = bits[i];
                AvailablePart ap = PartLoader.getPartInfoByName(b.Part);
                if (b.Parent < 0) y[i] = 10;
                else if (b.Radial) y[i] = y[b.Parent];
                else if (b.Above)
                {
                    AttachNode pt = PartLoader.getPartInfoByName(bits[b.Parent].Part)?.partPrefab.FindAttachNode("top");
                    AttachNode cb = ap?.partPrefab.FindAttachNode("bottom");
                    y[i] = y[b.Parent] + (pt != null ? pt.position.y : 0.5f) - (cb != null ? cb.position.y : -0.5f);
                }
                else
                {
                    AttachNode pb = PartLoader.getPartInfoByName(bits[b.Parent].Part)?.partPrefab.FindAttachNode("bottom");
                    AttachNode ct = ap?.partPrefab.FindAttachNode("top");
                    y[i] = y[b.Parent] + (pb != null ? pb.position.y : -0.5f) - (ct != null ? ct.position.y : 0.5f);
                }
            }
            for (int i = 0; i < bits.Count; i++)
            {
                Bit b = bits[i];
                ConfigNode p = c.AddNode("PART");
                p.AddValue("part", refs[i]);
                p.AddValue("partName", b.StrutTo >= 0 ? "CompoundPart" : "Part");
                p.AddValue("persistentId", (2001 + i).ToString(CultureInfo.InvariantCulture));
                string pos = F(b.X) + "," + y[i].ToString("R", CultureInfo.InvariantCulture) + ",0";
                p.AddValue("pos", pos);
                p.AddValue("attPos", "0,0,0");
                p.AddValue("attPos0", pos);
                p.AddValue("rot", "0,0,0,1");
                p.AddValue("attRot", "0,0,0,1");
                p.AddValue("attRot0", "0,0,0,1");
                p.AddValue("mir", "1,1,1");
                p.AddValue("symMethod", "Radial");
                p.AddValue("istg", b.Decoupler ? "0" : "-1");
                p.AddValue("dstg", "0");
                p.AddValue("sidx", b.Decoupler ? "0" : "-1");
                p.AddValue("sqor", b.Decoupler ? "0" : "-1");
                p.AddValue("sepI", "0");
                p.AddValue("attm", b.Radial ? "1" : "0");
                p.AddValue("modCost", "0");
                p.AddValue("modMass", "0");
                p.AddValue("modSize", "0,0,0");
                for (int k = 0; k < bits.Count; k++) if (bits[k].Parent == i) p.AddValue("link", refs[k]);
                if (b.Parent >= 0)
                {
                    if (b.Radial) p.AddValue("srfN", "srfAttach," + refs[b.Parent]);
                    else p.AddValue("attN", (b.Above ? "bottom," : "top,") + refs[b.Parent]);
                }
                for (int k = 0; k < bits.Count; k++)
                    if (bits[k].Parent == i && !bits[k].Radial) p.AddValue("attN", (bits[k].Above ? "top," : "bottom,") + refs[k]);
                if (b.StrutTo >= 0)
                {
                    // To the side of its target, straight down (local axes are the world's: the strut isn't rotated).
                    var to = new Vector3(0.6f - b.X, y[b.StrutTo] - y[i], 0);
                    ConfigNode pd = p.AddNode("PARTDATA");
                    pd.AddValue("tgt", (2001 + b.StrutTo).ToString(CultureInfo.InvariantCulture));
                    pd.AddValue("tpersID", (2001 + b.StrutTo).ToString(CultureInfo.InvariantCulture));
                    pd.AddValue("pos", to.x.ToString("R", CultureInfo.InvariantCulture) + "," + to.y.ToString("R", CultureInfo.InvariantCulture) + ",0");
                    pd.AddValue("rot", "0,0,0,1");
                    Vector3 d = to.normalized;
                    pd.AddValue("dir", d.x.ToString("R", CultureInfo.InvariantCulture) + "," + d.y.ToString("R", CultureInfo.InvariantCulture) + ",0");
                }
                if (b.Program != null)
                {
                    ConfigNode m = p.AddNode("MODULE");
                    m.AddValue("name", "ModulePipPart");
                    m.AddValue("isEnabled", "True");
                    m.AddValue("programId", b.Program);
                }
            }
            return c;
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

            // 2. A PIP battery program at Mk 1.
            Type scenT = PipType("PIP.PipScenario"), typeT = PipType("PIP.PartType"), typesT = PipType("PIP.PartTypes"), factory = PipType("PIP.PartFactory");
            object scen = scenT != null ? Member(scenT, "Instance") : null;
            MethodInfo create = scenT?.GetMethod("Create", new[] { typeof(string), typeT, typeof(string[]), typeof(int[]), typeof(string).MakeByRefType() });
            MethodInfo ensure = factory?.GetMethod("Ensure", BindingFlags.Public | BindingFlags.Static);
            Check(scen != null && create != null && ensure != null && typesT != null, "Phil's Iterative Parts is installed");
            if (scen == null || create == null || ensure == null || typesT == null) yield break;
            object battery = Member(typesT, "Battery");
            string[] model = (string[])Member(battery, "DefaultModel");
            var args = new object[] { "Scancell", battery, model, new int[((Array)Member(battery, "Categories")).Length], null };
            object program = create.Invoke(scen, args);
            Check(program != null, "a PIP battery program " + args[4]);
            if (program == null) yield break;
            string programId = (string)Member(program, "Id");
            AvailablePart pipAp = ensure.Invoke(null, new[] { program }) as AvailablePart;
            string pipPart = (string)Member(program, "PartName");
            Check(pipAp != null && (int)Member(program, "Mk") == 1, "its part " + pipPart + " at Mk " + Member(program, "Mk"));
            Suite.Touch();

            // 3. The ship, top to bottom: a tank, a decoupler, a probe core, the battery and a storage unit (it stands on
            // that), with a strut from the unit up to the tank across the decoupler. Dropping the tank leaves the rest
            // standing on the pad.
            var bits = new List<Bit>
            {
                new Bit { Part = "probeCoreOcto.v2" },                                      // 0
                new Bit { Part = "Decoupler.2", Parent = 0, Above = true, Decoupler = true },  // 1
                new Bit { Part = "fuelTank", Parent = 1, Above = true },                    // 2
                new Bit { Part = pipPart, Program = programId, Parent = 0 },               // 3
                new Bit { Part = Unit, Parent = 3 },                                        // 4
                new Bit { Part = "strutConnector", Parent = 4, Radial = true, X = 1.2f, StrutTo = 2 },  // 5
            };
            foreach (Bit b in bits) if (PartLoader.getPartInfoByName(b.Part) == null) { Check(false, "part " + b.Part + " is installed"); yield break; }
            ConfigNode file = ShipCraft(bits);
            PipType("PIP.PipApi")?.GetMethod("ToCurrent", new[] { typeof(ConfigNode) })?.Invoke(null, new object[] { file });
            string dir = Path.Combine(KSPUtil.ApplicationRootPath, "saves", SaveName, "Ships", "VAB");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, ShipName + ".craft");
            file.Save(path);

            // 4. In the VAB: load it, and put six repair kits in the storage unit (four in one slot, two in the next).
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            EditorDriver.StartupBehaviour = EditorDriver.StartupBehaviours.START_CLEAN;
            EditorDriver.StartEditor(EditorFacility.VAB);
            yield return WaitFor(() => HighLogic.LoadedScene == GameScenes.EDITOR && EditorLogic.fetch != null && EditorAddon.Instance != null, 90, "the VAB opened");
            yield return new WaitForSeconds(3f);
            EditorLogic.LoadShipFromFile(path);
            yield return WaitFor(() => EditorLogic.fetch.ship != null && EditorLogic.fetch.ship.parts.Count == bits.Count, 30, "the ship loaded (" + bits.Count + " parts)");
            yield return new WaitForSeconds(2f);
            CompoundPart strut = EditorLogic.fetch.ship.parts.OfType<CompoundPart>().FirstOrDefault();
            Check(strut != null && strut.target != null && strut.target.partInfo.name == "fuelTank", "the strut is connected to the tank (" + strut?.target?.partInfo.name + ")");
            Part unitPart = EditorLogic.fetch.ship.parts.FirstOrDefault(p => p.partInfo.name == Unit);
            ModuleInventoryPart inv = unitPart?.FindModuleImplementing<ModuleInventoryPart>();
            bool stored = inv != null && inv.StoreCargoPartAtSlot(Kit, 0) && inv.UpdateStackAmountAtSlot(0, 4, null) && inv.StoreCargoPartAtSlot(Kit, 1) && inv.UpdateStackAmountAtSlot(1, 2, null);
            GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
            yield return new WaitForSeconds(1f);
            Check(stored && inv.TotalAmountOfPartStored(Kit) == 6, "six repair kits in the storage unit (" + (inv != null ? inv.TotalAmountOfPartStored(Kit) : -1) + ")");
            Check(KitsIn(UnitIn(EditorAddon.EditorCraft())) == 6, "the editor's craft saves them (" + KitsIn(UnitIn(EditorAddon.EditorCraft())) + ")");

            // 5. Build it (outsourced, so it's done at once).
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
            double unitBefore = KspCosts.Instance.Cost(UnitIn(h.Craft)).Dry;
            Note("Scan stored storage unit before flying: " + KitsIn(UnitIn(h.Craft)) + " kits, dry " + F(unitBefore) + ", modCost " + UnitIn(h.Craft).Get("modCost"));

            // 6. Launch it from the hangar, drop the lower stage (the strut stays on the storage unit, its other end
            // gone), and use up three kits.
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            HighLogic.LoadScene(GameScenes.SPACECENTER);
            yield return WaitFor(() => HighLogic.LoadedScene == GameScenes.SPACECENTER && PRSScenario.Instance != null, 90, "the Space Center opened");
            yield return new WaitForSeconds(3f);
            sc = PRSScenario.Instance;
            h = sc.HangarOf(shipId);
            // Some wear from earlier flights, so refurbishing parts into the bin costs something (wear × refurbish share).
            if (h != null) h.Wear = 0.4;
            Check(h != null && sc.LaunchReady(h, out string lerr), "launching it from the hangar at 40% wear");
            yield return WaitFor(() => HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.loaded && !FlightGlobals.ActiveVessel.packed, 120, "it reached the pad");
            yield return new WaitForSeconds(5f);
            Vessel v = FlightGlobals.ActiveVessel;
            ModuleDecouplerBase dec = v.parts.Select(p => p.FindModuleImplementing<ModuleDecouplerBase>()).FirstOrDefault(m => m != null);
            Check(dec != null, "the decoupler is on the pad");
            dec?.Decouple();
            yield return null;
            yield return null;
            v = FlightGlobals.ActiveVessel;
            // The dropped tank goes at once, so it neither lands on the ship nor is recovered with it.
            foreach (Vessel other in FlightGlobals.Vessels.Where(x => x != v && x.parts.Any(p => p.partInfo.name == "fuelTank")).ToList()) other.Die();
            yield return new WaitForSeconds(2f);
            yield return WaitFor(() => v.srfSpeed < 0.05, 30, "the ship is standing still on the pad");
            yield return new WaitForSeconds(1f);
            Check(v.parts.Count == 5 && v.parts.OfType<CompoundPart>().Count() == 1 && !v.parts.Any(p => p.partInfo.name == "fuelTank"),
                  "the ship left is the decoupler, probe, battery, storage unit and strut (" + v.parts.Count + " parts, " + v.situation + ")");
            ModuleInventoryPart flyingInv = v.parts.FirstOrDefault(p => p.partInfo.name == Unit)?.FindModuleImplementing<ModuleInventoryPart>();
            bool used = flyingInv != null && flyingInv.RemoveNPartsFromInventory(Kit, 3, false);
            Check(used && flyingInv.TotalAmountOfPartStored(Kit) == 3, "three kits used up in flight, three left (" + (flyingInv != null ? flyingInv.TotalAmountOfPartStored(Kit) : -1) + ")");
            yield return new WaitForSeconds(1f);

            // 7. Recover it to the hangar: recovery charges what the prompt said (R3).
            double fundsBefore = Funding.Instance.Funds;
            var altimeter = FindObjectOfType<KSP.UI.Screens.AltimeterSliderButtons>();
            typeof(KSP.UI.Screens.AltimeterSliderButtons).GetMethod("recoverVessel", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(altimeter, null);
            yield return new WaitForSeconds(1f);
            RecoverPrompt prompt = RecoverPrompt.Current;
            double promptBin = prompt != null ? (double)Member(prompt, "binPrice") : -1;
            double promptTransport = prompt != null ? (double)Member(prompt, "Transport") : -1;
            Note("Scan the Recover prompt: parts bin " + F(promptBin) + ", transport in all " + F(promptTransport) + " (" + (string)Member(prompt, "TransportText") + ")");
            Check(promptBin >= 0.5, "the prompt prices the strut left without its other end (" + F(promptBin) + ")");
            yield return new WaitForSeconds(0.5f);
            Shot("scan_recover_prompt");
            prompt?.TestChoose(RecoverChoice.Hangar);
            yield return WaitFor(() => HighLogic.LoadedScene == GameScenes.SPACECENTER && PRSScenario.Instance != null && Funding.Instance != null, 120, "recovery returned to the Space Center");
            yield return new WaitForSeconds(4f);
            double charged = fundsBefore - Funding.Instance.Funds;
            Check(Math.Abs(charged - promptTransport) < 1, "recovery charged what the prompt said: " + F(charged) + " (prompt " + F(promptTransport) + ")");
            sc = PRSScenario.Instance;
            h = sc.HangarOf(shipId);
            Check(h != null && h.PartCount == 4, "it's back in the hangar, 4 parts (" + h?.DisplayName + ", " + h?.PartCount + ")");
            if (h == null) yield break;
            Check(sc.Bin.Any(b => b.Key.StartsWith("strutConnector|", StringComparison.Ordinal)), "the strut is in the parts bin");

            // R2: three kits are gone from the stored storage unit, and from its price.
            Node unitStored = UnitIn(h.Craft);
            double kitPrice = PartLoader.getPartInfoByName(Kit).cost, unitList = PartLoader.getPartInfoByName(Unit).cost;
            double unitAfter = KspCosts.Instance.Cost(unitStored).Dry;
            Note("Scan stored storage unit after flying: " + KitsIn(unitStored) + " kits, dry " + F(unitAfter) + ", modCost " + unitStored.Get("modCost"));
            Check(KitsIn(unitStored) == 3, "the stored storage unit holds the three kits left (" + KitsIn(unitStored) + ")");
            Check(Math.Abs(unitAfter - (unitList + 3 * kitPrice)) < 0.5, "and is priced with three kits: " + F(unitAfter) + " (list " + F(unitList) + " + 3 × " + F(kitPrice) + ")");
            int flights = h.Ship.Flights, logLines = h.Ship.Log.Count;
            Note("Scan " + h.DisplayName + ": " + flights + " flight(s), log: " + string.Join(" | ", h.Ship.Log.ToArray()));

            // 8. The battery's program moves on to Mk 3 while the flown battery is Mk 1.
            object scenNow = Member(scenT, "Instance");
            object programNow = scenT.GetMethod("Find")?.Invoke(scenNow, new object[] { programId });
            programNow?.GetType().GetField("Launches")?.SetValue(programNow, 7);
            try { factory.GetMethod("Refresh", BindingFlags.Public | BindingFlags.Static, null, new[] { programNow.GetType() }, null)?.Invoke(null, new[] { programNow }); }
            catch (Exception ex) { Note("PIP's refresh: " + (ex.InnerException ?? ex).Message); }
            Suite.Touch();
            Check(programNow != null && (int)Member(programNow, "Mk") == 3 && MkIn(PipIn(h.Craft)) == "1", "the program is at Mk 3, the flown battery Mk " + MkIn(PipIn(h.Craft)));

            // 9. Opened from the hangar in the VAB: the battery stays Mk 1, and it's exactly the hangar ship (R2:
            // "Launch as it is" stays).
            EditorAddon.OpenHangarShip(h);
            yield return WaitFor(() => HighLogic.LoadedScene == GameScenes.EDITOR && EditorLogic.fetch != null && EditorAddon.Instance != null && EditorLogic.fetch.ship != null && EditorLogic.fetch.ship.parts.Count == 4, 90, "Open in the VAB went to the VAB with the ship");
            yield return new WaitForSeconds(3f);
            sc = PRSScenario.Instance;
            h = sc.HangarOf(shipId);
            Check(LiveMk(LivePip()) == 1, "in the VAB the flown battery is Mk " + LiveMk(LivePip()));
            Node opened = EditorAddon.EditorCraft();
            Note("Scan storage unit opened in the VAB: dry " + F(KspCosts.Instance.Cost(UnitIn(opened)).Dry) + " (stored " + F(KspCosts.Instance.Cost(UnitIn(h.Craft)).Dry) + ")");
            Check(sc.ExactShipOf(opened)?.Id == shipId, "the VAB sees it as exactly the hangar ship");
            EditorAddon.Instance.OpenBuildPanel();
            panel = EditorAddon.Instance.Panel;
            Check(panel.Title == ShipName + " #1", "the Build panel calls it " + panel.Title);
            panel.Showing = false;

            // 10. Moved to the SPH with the switch button (R1): still the ship that was opened.
            EditorLogic.fetch.SwitchEditor();
            yield return new WaitForSeconds(1f);
            yield return WaitFor(() => EditorDriver.editorFacility == EditorFacility.SPH && EditorDriver.fetch != null && !EditorDriver.fetch.restartingEditor
                                       && EditorLogic.fetch != null && EditorLogic.fetch.ship != null && EditorLogic.fetch.ship.parts.Count == 4, 90, "the ship moved to the SPH");
            yield return new WaitForSeconds(3f);
            int addons = FindObjectsOfType<EditorAddon>().Length;
            Check(addons == 1, "one suite editor add-on after the switch (" + addons + ")");
            Check(LiveMk(LivePip()) == 1, "in the SPH the flown battery is still Mk " + LiveMk(LivePip()));
            Check(EditorAddon.OpenedCraft == h.CraftId, "the suite still knows which hangar ship is open (" + (EditorAddon.OpenedCraft ?? "none") + ")");
            Check(EditorLogic.fetch.ship.parts.All(EditorAddon.IsHardware), "every part is still that ship's hardware");
            Check(sc.ExactShipOf(EditorAddon.EditorCraft())?.Id == shipId, "the SPH sees it as exactly the hangar ship");
            EditorAddon.Instance.OpenBuildPanel();
            panel = EditorAddon.Instance.Panel;
            yield return new WaitForSeconds(1f);
            Shot("scan_build_panel_in_sph");
            Check(panel.Title == ShipName + " #1" && panel.MainShip?.Id == shipId, "the Build panel calls it " + panel.Title);

            // 11. Build it there: it's still Scan Test #1, with its flight and log, and its battery stays Mk 1.
            Check(panel.Confirm(), "building it in the SPH " + panel.Error);
            job = sc.Jobs.LastOrDefault();
            if (job != null) sc.Outsource(job, out string oerr2);
            yield return new WaitForSeconds(1f);
            HangarShip after = sc.HangarOf(shipId);
            Check(after != null && after.Ready && after.DisplayName == ShipName + " #1", "finished, it's " + after?.DisplayName);
            Check(after != null && after.Ship.Flights == flights && after.Ship.Log.Count == logLines, "with its " + after?.Ship.Flights + " flight(s) and " + after?.Ship.Log.Count + " log line(s)");
            Check(!sc.Ships.Values.Any(s => s.Name == ShipName && s.Number != 1), "no other " + ShipName + " was made (" + string.Join(", ", sc.Ships.Values.Where(s => s.Name == ShipName).Select(s => s.DisplayName).ToArray()) + ")");
            Check(after != null && MkIn(PipIn(after.Craft)) == "1", "its battery is still Mk " + MkIn(PipIn(after?.Craft)));
            if (EditorAddon.Instance.Panel != null) EditorAddon.Instance.Panel.Showing = false;
        }
    }
}

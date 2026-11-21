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
    /// S33, in game (test harness only). Phil's Stayputnik with its memory raised (a Kerbalism setting that makes it 1,600,
    /// not 400) came back from the parts bin matching neither setup, so the Build panel built it new without saying so.
    /// Kerbalism isn't on the test install, so two parts stand in for it. A Phil's Iterative Parts fuel tank's price, like
    /// the Stayputnik's experiments, is worked out only once the part starts, so KSP's own copy of the launch has it
    /// cheaper: that's the bug's cause, shown in the run. A B9 Mk1 fuselage set to a fuel tank is a part whose price
    /// follows a setting. A probe on the fuselage on the tank is built, launched from the hangar, recovered and taken
    /// apart. The launch, the hangar and the bin keep both at the prices they were built for, so the same ship built again
    /// in the VAB takes every part from the bin; with the fuselage set back to plain, it's new, and the Build panel lists
    /// it first, by name.
    /// </summary>
    class SelfTestS33 : MonoBehaviour
    {
        public const string SaveName = "zz_PRSSelfTestS33";
        const string ShipName = "Setup Test";
        const string Fuselage = "B9.Body.Mk1.Fuselage.100m";   // KSP turns the cfg name's underscores into dots
        static bool running;
        int pass, fail;
        string pipTank;

        public static void Begin()
        {
            if (running) return;
            running = true;
            var go = new GameObject("PRSSelfTestS33");
            DontDestroyOnLoad(go);
            go.AddComponent<SelfTestS33>();
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
            if (o == null) return null;
            Type t = o as Type ?? o.GetType();
            object target = o is Type ? null : o;
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            PropertyInfo p = t.GetProperty(name, all);
            if (p != null) return p.GetValue(target, null);
            return t.GetField(name, all)?.GetValue(target);
        }

        static Node PartIn(Node craft, string name) => craft?.Children("PART").FirstOrDefault(p => Hardware.PartName(p) == name);
        static double Price(Node part) => part != null ? KspCosts.Instance.Cost(part).Dry : -1;
        static string SubtypeOf(Node part, string moduleId) => part?.Children("MODULE").FirstOrDefault(m => m.Get("name") == "ModuleB9PartSwitch" && m.Get("moduleID") == moduleId)?.Get("currentSubtype");
        static Part Live(string name) => EditorLogic.fetch?.ship?.parts.FirstOrDefault(p => p.partInfo.name == name);

        /// <summary>Switches one of a part's B9 settings as its part menu does.</summary>
        static string Switch(Part p, string moduleId, string subtype)
        {
            PartModule m = p?.Modules.Cast<PartModule>().FirstOrDefault(x => x.moduleName == "ModuleB9PartSwitch" && (string)Member(x, "moduleID") == moduleId);
            if (m == null) return "no " + moduleId;
            try { m.GetType().GetMethod("SwitchSubtype", new[] { typeof(string) }).Invoke(m, new object[] { subtype }); }
            catch (Exception ex) { return "switching threw " + (ex.InnerException ?? ex).Message; }
            GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
            return (string)Member(m, "CurrentSubtypeName");
        }

        /// <summary>The ship set up as built: the fuselage a fuel tank, the PIP tank long and holding liquid fuel.</summary>
        string SetUp()
        {
            return Switch(Live(Fuselage), "fuelSwitch", "LFO") + ", " + Switch(Live(pipTank), "pipSize", "125-L") + ", " + Switch(Live(pipTank), "fuelSwitch", "LiquidFuel");
        }

        IEnumerator LoadInVab(string path, string what)
        {
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            EditorDriver.StartupBehaviour = EditorDriver.StartupBehaviours.START_CLEAN;
            EditorDriver.StartEditor(EditorFacility.VAB);
            yield return WaitFor(() => HighLogic.LoadedScene == GameScenes.EDITOR && EditorLogic.fetch != null && EditorAddon.Instance != null, 90, "the VAB opened " + what);
            yield return new WaitForSeconds(3f);
            EditorLogic.LoadShipFromFile(path);
            yield return WaitFor(() => EditorLogic.fetch.ship != null && EditorLogic.fetch.ship.parts.Count == 3, 30, "the ship loaded " + what);
            yield return new WaitForSeconds(2f);
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
            if (PartLoader.getPartInfoByName(Fuselage) == null) { Check(false, "the B9 Mk1 fuselage is installed"); yield break; }

            // 2. A PIP fuel tank program.
            Type scenT = PipType("PIP.PipScenario"), typeT = PipType("PIP.PartType"), typesT = PipType("PIP.PartTypes"), factory = PipType("PIP.PartFactory");
            object scen = scenT != null ? Member(scenT, "Instance") : null;
            MethodInfo create = scenT?.GetMethod("Create", new[] { typeof(string), typeT, typeof(string[]), typeof(int[]), typeof(string).MakeByRefType() });
            MethodInfo ensure = factory?.GetMethod("Ensure", BindingFlags.Public | BindingFlags.Static);
            Check(scen != null && create != null && ensure != null && typesT != null, "Phil's Iterative Parts is installed");
            if (scen == null || create == null || ensure == null || typesT == null) yield break;
            object tankType = Member(typesT, "Tank");
            var args = new object[] { "L Silo", tankType, (string[])Member(tankType, "DefaultModel"), new int[((Array)Member(tankType, "Categories")).Length], null };
            object program = create.Invoke(scen, args);
            Check(program != null, "a PIP tank program " + args[4]);
            if (program == null) yield break;
            ensure.Invoke(null, new[] { program });
            pipTank = (string)Member(program, "PartName");
            Suite.Touch();

            // 3. Top to bottom: a probe core, the fuselage, and the tank it stands on.
            var bits = new List<SelfTestScan.Bit>
            {
                new SelfTestScan.Bit { Part = "probeCoreOcto.v2" },
                new SelfTestScan.Bit { Part = Fuselage, Parent = 0 },
                new SelfTestScan.Bit { Part = pipTank, Program = (string)Member(program, "Id"), Parent = 1 },
            };
            ConfigNode file = SelfTestScan.ShipCraft(bits, ShipName);
            PipType("PIP.PipApi")?.GetMethod("ToCurrent", new[] { typeof(ConfigNode) })?.Invoke(null, new object[] { file });
            string dir = Path.Combine(KSPUtil.ApplicationRootPath, "saves", SaveName, "Ships", "VAB");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, ShipName + ".craft");
            file.Save(path);

            // 4. In the VAB, set it up (both get dearer), and build it.
            yield return LoadInVab(path, "");
            double plain = Price(PartIn(EditorAddon.EditorCraft(), Fuselage));
            string set = SetUp();
            yield return new WaitForSeconds(1f);
            Node vab = EditorAddon.EditorCraft();
            double body = Price(PartIn(vab, Fuselage)), tank = Price(PartIn(vab, pipTank));
            Check(SubtypeOf(PartIn(vab, Fuselage), "fuelSwitch") == "LFO" && body > plain + 1, "set to a fuel tank, the fuselage costs more: " + F(body) + " (plain " + F(plain) + "; " + set + ")");
            Note("S33 PIP tank in the VAB: " + F(tank));
            EditorAddon.Instance.OpenBuildPanel();
            BuildPanel panel = EditorAddon.Instance.Panel;
            Check(panel.Confirm(), "building it " + panel.Error);
            PRSScenario sc = PRSScenario.Instance;
            BuildJob job = sc.Jobs.LastOrDefault();
            if (job != null) sc.Outsource(job, out string oerr);
            HangarShip h = sc.Hangar.FirstOrDefault(x => x.Name == ShipName && x.Ready);
            Check(h != null && Math.Abs(Price(PartIn(h.Craft, Fuselage)) - body) < 0.5 && Math.Abs(Price(PartIn(h.Craft, pipTank)) - tank) < 0.5,
                  "built, the hangar keeps the fuselage at " + F(Price(PartIn(h?.Craft, Fuselage))) + " and the tank at " + F(Price(PartIn(h?.Craft, pipTank))));
            if (h == null) yield break;
            string shipId = h.Id;

            // 5. Launched from the hangar: KSP's own copy has the tank cheaper (the cause); the launch record doesn't.
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            HighLogic.LoadScene(GameScenes.SPACECENTER);
            yield return WaitFor(() => HighLogic.LoadedScene == GameScenes.SPACECENTER && PRSScenario.Instance != null, 90, "the Space Center opened");
            yield return new WaitForSeconds(3f);
            sc = PRSScenario.Instance;
            h = sc.HangarOf(shipId);
            Check(h != null && sc.LaunchReady(h, out string lerr), "launching it from the hangar");
            yield return WaitFor(() => HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.loaded && !FlightGlobals.ActiveVessel.packed, 120, "it reached the pad");
            yield return new WaitForSeconds(5f);
            sc = PRSScenario.Instance;
            LaunchRecord rec = sc.Launches.LastOrDefault();
            Node kspCopy = ShipConstruction.ShipConfig != null ? KspNodes.FromKsp(ShipConstruction.ShipConfig) : null;
            Note("S33 tank: built " + F(tank) + ", KSP's own copy " + F(Price(PartIn(kspCopy, pipTank))) + ", the launch record " + F(Price(PartIn(rec?.Craft, pipTank)))
                 + "; fuselage: built " + F(body) + ", KSP's copy " + F(Price(PartIn(kspCopy, Fuselage))) + ", the record " + F(Price(PartIn(rec?.Craft, Fuselage))));
            Check(Price(PartIn(kspCopy, pipTank)) >= 0 && Price(PartIn(kspCopy, pipTank)) < tank - 0.5, "KSP's own copy of the launch has the tank cheaper (" + F(Price(PartIn(kspCopy, pipTank))) + "): what used to come home");
            Check(rec != null && Math.Abs(Price(PartIn(rec.Craft, pipTank)) - tank) < 0.5 && Math.Abs(Price(PartIn(rec.Craft, Fuselage)) - body) < 0.5,
                  "the launch is recorded at the prices it was built for: tank " + F(Price(PartIn(rec?.Craft, pipTank))) + ", fuselage " + F(Price(PartIn(rec?.Craft, Fuselage))));
            Vessel v = FlightGlobals.ActiveVessel;
            yield return WaitFor(() => v.srfSpeed < 0.05, 30, "the ship is standing still on the pad");

            // 6. Recovered to the hangar, then taken apart: the bin keeps each part as it was built.
            var altimeter = FindObjectOfType<KSP.UI.Screens.AltimeterSliderButtons>();
            typeof(KSP.UI.Screens.AltimeterSliderButtons).GetMethod("recoverVessel", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(altimeter, null);
            yield return new WaitForSeconds(1f);
            RecoverPrompt.Current?.TestChoose(RecoverChoice.Hangar);
            yield return WaitFor(() => HighLogic.LoadedScene == GameScenes.SPACECENTER && PRSScenario.Instance != null && Funding.Instance != null, 120, "recovery returned to the Space Center");
            yield return new WaitForSeconds(4f);
            sc = PRSScenario.Instance;
            h = sc.HangarOf(shipId);
            Check(h != null && Math.Abs(Price(PartIn(h.Craft, pipTank)) - tank) < 0.5 && Math.Abs(Price(PartIn(h.Craft, Fuselage)) - body) < 0.5,
                  "back in the hangar: tank " + F(Price(PartIn(h?.Craft, pipTank))) + ", fuselage " + F(Price(PartIn(h?.Craft, Fuselage))));
            if (h == null) yield break;
            Check(sc.TakeApart(h, out string aerr), "taking it apart " + aerr);
            BinKind bodyKind = sc.Bin.FirstOrDefault(b => b.Key.StartsWith(Fuselage + "|", StringComparison.Ordinal));
            BinKind tankKind = sc.Bin.FirstOrDefault(b => b.Key.StartsWith(pipTank + "|", StringComparison.Ordinal));
            Check(bodyKind != null && Math.Abs(Price(bodyKind.Sample) - body) < 0.5 && SubtypeOf(bodyKind.Sample, "fuelSwitch") == "LFO",
                  "the parts bin keeps the fuselage as it was built: a fuel tank at " + F(Price(bodyKind?.Sample)) + " (" + bodyKind?.Key + ")");
            Check(tankKind != null && Math.Abs(Price(tankKind.Sample) - tank) < 0.5, "and the tank at " + F(Price(tankKind?.Sample)) + " (" + tankKind?.Key + ")");

            // 7. The same ship again in the VAB: every part comes from the bin, nothing new.
            yield return LoadInVab(path, "again");
            SetUp();
            yield return new WaitForSeconds(1f);
            EditorAddon.Instance.OpenBuildPanel();
            panel = EditorAddon.Instance.Panel;
            Quote q = panel.Quote;
            Check(q != null && q.BinParts == 3 && q.NewPrice < 0.5, "built again as it was, every part comes from the parts bin: " + q?.BinParts + " from the bin, new parts " + F(q?.NewPrice ?? -1));
            yield return new WaitForSeconds(0.5f);
            Shot("s33_from_bin");

            // 8. The fuselage set back to plain is another part: built new, listed first and by name.
            panel.Showing = false;
            Switch(Live(Fuselage), "fuelSwitch", "Structural");
            yield return new WaitForSeconds(1f);
            EditorAddon.Instance.OpenBuildPanel();
            panel = EditorAddon.Instance.Panel;
            q = panel.Quote;
            QuoteLine first = q?.Lines.FirstOrDefault();
            List<string> named = first != null ? BuildPanel.Items(first) : new List<string>();
            string title = PartLoader.getPartInfoByName(Fuselage).title;
            Check(q != null && q.BinParts == 2 && Math.Abs(q.NewPrice - plain) < 0.5, "as a plain fuselage it isn't the bin's: built new for " + F(q?.NewPrice ?? -1) + ", the probe and tank from the bin");
            Check(first != null && first.Kind == OwnerKind.New && named.Contains(title), "the Build panel lists the new part first, by name: " + string.Join(", ", named.ToArray()));
            yield return new WaitForSeconds(0.5f);
            Shot("s33_new_part_named");
            panel.Showing = false;
        }
    }
}

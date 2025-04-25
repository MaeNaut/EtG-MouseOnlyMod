using BepInEx;
using Alexandria;
using Alexandria.ItemAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace MouseOnlyMod
{
    [BepInDependency(Alexandria.Alexandria.GUID)] // this mod depends on the Alexandria API: https://enter-the-gungeon.thunderstore.io/package/Alexandria/Alexandria/
    [BepInDependency(ETGModMainBehaviour.GUID)]
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "Maestronaut.etg.MouseOnlyMod";
        public const string NAME = "MouseOnlyMod";
        public const string VERSION = "1.0.0";
        public const string TEXT_COLOR = "#00FFFF";

        public static bool MouseOnlyEnabled = false;

        public void Start()
        {
            ETGModMainBehaviour.WaitForGameManagerStart(GMStart);
        }

        public void GMStart(GameManager g)
        {
            Log($"{NAME} v{VERSION} started successfully.", TEXT_COLOR);

            GameObject controllerObj = new GameObject("MouseOnlyController");
            controllerObj.AddComponent<MouseOnlyController>();
            GameObject.DontDestroyOnLoad(controllerObj);

            // Allow InputPatch.cs to run its Postfix method every frame.
            var harmony = new HarmonyLib.Harmony(GUID);
            harmony.PatchAll();
        }

        public static void Log(string text, string color="#FFFFFF")
        {
            ETGModConsole.Log($"<color={color}>{text}</color>");
        }
    }
}

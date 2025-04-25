using BepInEx;
using Alexandria;
using Alexandria.ItemAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Dungeonator;

namespace MouseOnlyMod
{
    public class MouseOnlyController : MonoBehaviour
    {
        // EtG starts with Mouse-Only Mod enabled.
        private bool mouseOnlyEnabled = true;

        void Start()
        {
            Plugin.MouseOnlyEnabled = mouseOnlyEnabled;
        }

        void Update()
        {
            // Toggle Mouse-Only Mod with M key.
            if (Input.GetKeyDown(KeyCode.M))
            {
                mouseOnlyEnabled = !mouseOnlyEnabled;
                Plugin.MouseOnlyEnabled = mouseOnlyEnabled;
                ETGModConsole.Log($"Mouse-Only Mod: {(mouseOnlyEnabled ? "ENABLED" : "DISABLED")}");
            }
        }
    }
}
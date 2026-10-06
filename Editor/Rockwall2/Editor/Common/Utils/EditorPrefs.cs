using Newtonsoft.Json;
using Rockwall2.Editor.Mapper;
using Rockwall2.Editor.Mapper.Toolbar;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Rockwall2.Editor.Common.Utils;
public static class EditorPrefs
{
    static string PrefsSaveFile = $"{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}/Rockwall/editor_prefs.json";

    public static bool EnableSFX = true;

    struct Prefs
    {
        public bool FlipSelectShift;
        public bool FlipScrollShift;
        public (int slot, string? id)[] Hotbar;
        public bool EnableSFX;
        public bool AutoSaveBeforeCompile;
    }

    public static void LoadEditorPrefs()
    {
        if (!File.Exists(PrefsSaveFile))
        {
            SaveEditorPrefs();
            return;
        }

        var prefs = JsonConvert.DeserializeObject<Prefs>(File.ReadAllText(PrefsSaveFile));

        Toolbelt.FlipSelectShift = prefs.FlipSelectShift;
        Toolbelt.FlipScrollShift = prefs.FlipScrollShift;
        EnableSFX = prefs.EnableSFX;
        Toolbelt.AutoSaveBeforeCompile = prefs.AutoSaveBeforeCompile;

        for (int i = 0; i < prefs.Hotbar.Length; i++)
        {
            Hotbar.AssignSlot(prefs.Hotbar[i].slot, prefs.Hotbar[i].id);
        }
    }
    public static void SaveEditorPrefs()
    {
        var prefs = new Prefs
        {
            FlipSelectShift = Toolbelt.FlipSelectShift,
            FlipScrollShift = Toolbelt.FlipScrollShift,
            EnableSFX = EnableSFX,
            AutoSaveBeforeCompile = Toolbelt.AutoSaveBeforeCompile,
        };
        prefs.Hotbar = new (int slot, string? id)[9];

        for(int i = 0; i < 9; i++)
        {
            prefs.Hotbar[i] = (i+1, Hotbar.GetSlot(i+1)?.Id);
        }

        File.WriteAllText(PrefsSaveFile, JsonConvert.SerializeObject(prefs));
    }
}

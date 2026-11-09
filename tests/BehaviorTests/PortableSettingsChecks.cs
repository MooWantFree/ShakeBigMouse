using System.IO;
using System.Windows;
using ShakeToFindCursor;

internal static partial class Program
{
    private static void VerifyPortableSettings()
    {
        var folder = Directory.CreateTempSubdirectory("ShakeBigMouse-settings-");
        string portable = Path.Combine(folder.FullName, "settings.json");
        string user = Path.Combine(folder.FullName, "Roaming", "ShakeToFindCursor", "settings.json");
        AppSettings Load() => AppSettings.Load(portable, user);
        try
        {
            var fresh = Load();
            if (!fresh.PortableMode || File.Exists(portable) || File.Exists(user))
                throw new Exception("A fresh installation should default to portable mode.");
            fresh.Save(fresh, portable, user);
            if (!File.Exists(portable) || File.Exists(user)) throw new Exception("Wrong first-save location.");
            File.Delete(portable);

            Directory.CreateDirectory(Path.GetDirectoryName(user)!);
            const string legacy = """{"Sensitivity":10,"Language":"en","ExcludedProcesses":["game"]}""";
            File.WriteAllText(user, legacy);
            var saved = Load();
            if (saved.PortableMode || saved.MinimumShakeDistance != 450 || saved.ExcludedProcesses.Single() != "game")
                throw new Exception("The existing user configuration was not loaded.");

            var window = new SettingsWindow(saved, draft => { draft.Save(saved, portable, user); saved = draft; });
            window.TogglePortableMode.IsChecked = true;
            window.BtnReset.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (window.TogglePortableMode.IsChecked != true || !window.BtnApply.IsEnabled || File.Exists(portable))
                throw new Exception("Reset or toggling changed the storage location before Apply.");
            window.Close();
            if (File.Exists(portable) || File.ReadAllText(user) != legacy) throw new Exception("Closing saved a draft.");
            window = new SettingsWindow(saved, draft => { draft.Save(saved, portable, user); saved = draft; });
            window.TogglePortableMode.IsChecked = true;
            window.SliderMinimumShakeDistance.Value = 703;
            window.BtnApply.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            window.Close();
            saved = Load();
            if (!saved.PortableMode || saved.MinimumShakeDistance != 703 || saved.ExcludedProcesses.Single() != "game" ||
                File.ReadAllText(user) != legacy)
                throw new Exception("Enabling portable mode lost settings or replaced the user file.");
            saved.MinimumShakeDistance = 789;
            saved.Save(saved, portable, user);
            if (Load().MinimumShakeDistance != 789 || File.ReadAllText(user) != legacy)
                throw new Exception("Subsequent saves went to the wrong file.");

            var local = saved.Clone();
            local.PortableMode = false;
            using (File.Open(portable, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { local.Save(saved, portable, user); throw new Exception("A locked portable file allowed the switch."); }
                catch (IOException) { }
            }
            if (!Load().PortableMode || File.ReadAllText(user) != legacy)
                throw new Exception("Failed migration did not preserve both configurations.");
            local.Save(saved, portable, user);
            var reloaded = Load();
            if (File.Exists(portable) || reloaded.PortableMode || reloaded.MinimumShakeDistance != 789 ||
                reloaded.ExcludedProcesses.Single() != "game")
                throw new Exception("Switching back did not preserve the portable settings in AppData.");
        }
        finally { folder.Delete(recursive: true); }
    }
}

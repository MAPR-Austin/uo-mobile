// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;

namespace ClassicUO.Configuration
{
    internal static class ProfileManager
    {
        public static GlobalProfile GlobalProfile { get; private set; }
        public static Profile CurrentProfile { get; private set; }
        public static string ProfilePath { get; private set; }

        private static string _rootPath;
        private static string RootPath
        {
            get
            {
                if (string.IsNullOrEmpty(_rootPath))
                {
                    if (string.IsNullOrWhiteSpace(Settings.GlobalSettings.ProfilesPath))
                    {
                        _rootPath = Path.Combine(CUOEnviroment.ExecutablePath, "Data", "Profiles");
                    }
                    else
                    {
                        _rootPath = Settings.GlobalSettings.ProfilesPath;
                    }
                }

                return _rootPath;
            }
        }

        public static void Load(string servername, string username, string charactername)
        {
            GlobalProfile = ConfigurationResolver.Load<GlobalProfile>(Path.Combine(RootPath, "globalprofile.json"), ProfileJsonContext.DefaultToUse.GlobalProfile) ?? new GlobalProfile();

            CarryOverRenamedServer(username, servername);

            string path = FileSystemHelper.CreateFolderIfNotExists(RootPath, username, servername, charactername);
            string fileToLoad = Path.Combine(path, "profile.json");

            ProfilePath = path;
            CurrentProfile = ConfigurationResolver.Load<Profile>(fileToLoad, ProfileJsonContext.DefaultToUse.Profile) ?? NewFromDefault();

            CurrentProfile.Username = username;
            CurrentProfile.ServerName = servername;
            CurrentProfile.CharacterName = charactername;

            ValidateFields(CurrentProfile);
        }

        /// <summary>UO Mobile: shards renamed in the server list (old name, new name).</summary>
        private static readonly (string Old, string New)[] RenamedServers = { ("UO Mobile", "Graveyard Battles") };

        /// <summary>
        /// UO Mobile: a character's settings (button layouts, macros, windows) live under the shard's name, so when the
        /// shard is renamed they are carried over the first time the new name is used: each character's folder (and any
        /// file beside them) that the new name doesn't have yet moves across; what already exists there is kept.
        /// </summary>
        private static void CarryOverRenamedServer(string username, string servername)
        {
            foreach ((string oldName, string newName) in RenamedServers)
            {
                if (!string.Equals(servername, newName, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    string from = Path.Combine(RootPath, username, oldName), to = Path.Combine(RootPath, username, newName);

                    if (!Directory.Exists(from))
                    {
                        continue;
                    }

                    if (!Directory.Exists(to))
                    {
                        Directory.Move(from, to);
                        Log.Info($"[UOMobile] settings carried over from '{oldName}' to '{newName}'");

                        continue;
                    }

                    foreach (string dir in Directory.GetDirectories(from))
                    {
                        string target = Path.Combine(to, Path.GetFileName(dir));

                        if (!Directory.Exists(target))
                        {
                            Directory.Move(dir, target);
                        }
                    }

                    foreach (string file in Directory.GetFiles(from))
                    {
                        string target = Path.Combine(to, Path.GetFileName(file));

                        if (!File.Exists(target))
                        {
                            File.Move(file, target);
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Warn($"[UOMobile] settings not carried over to '{newName}': {e.Message}");
                }
            }
        }

        public static void SetProfileAsDefault(Profile profile)
        {
            Save(profile, RootPath, "default.json");
        }

        public static Profile NewFromDefault()
        {
            return ConfigurationResolver.Load<Profile>(Path.Combine(RootPath, "default.json"), ProfileJsonContext.DefaultToUse.Profile) ?? new Profile();
        }

        private static void ValidateFields(Profile profile)
        {
            if (profile == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(profile.ServerName))
            {
                throw new InvalidDataException();
            }

            if (string.IsNullOrEmpty(profile.Username))
            {
                throw new InvalidDataException();
            }

            if (string.IsNullOrEmpty(profile.CharacterName))
            {
                throw new InvalidDataException();
            }

            if (profile.WindowClientBounds.X < 600)
            {
                profile.WindowClientBounds = new Point(600, profile.WindowClientBounds.Y);
            }

            if (profile.WindowClientBounds.Y < 480)
            {
                profile.WindowClientBounds = new Point(profile.WindowClientBounds.X, 480);
            }
        }

        public static void UnLoadProfile()
        {
            GlobalProfile = null;
            CurrentProfile = null;
        }

        internal static void Save(Profile profile, string path, string filename = "profile.json")
        {
            ConfigurationResolver.Save(profile, Path.Combine(path, filename), ProfileJsonContext.DefaultToUse.Profile);
            if (GlobalProfile != null)
            {
                ConfigurationResolver.Save(GlobalProfile, Path.Combine(RootPath, "globalprofile.json"), ProfileJsonContext.DefaultToUse.GlobalProfile);
            }
        }
    }
}

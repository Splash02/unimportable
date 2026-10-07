using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arcade.UI;
using Arcade.UI.SongSelect;
using BepInEx.Bootstrap;
using HarmonyLib;
using Rhythm;
using UnityEngine;

namespace Unimportable;

internal static class SongImport
{
    private static readonly System.Reflection.FieldInfo Current = AccessTools.Field(typeof(ArcadeBGMManager), "currentItem");
    private static readonly System.Reflection.FieldInfo Pending = AccessTools.Field(typeof(ArcadeBGMManager), "itemToPlay");
    private static readonly System.Reflection.FieldInfo CurrentSong = AccessTools.Field(typeof(ArcadeBGMManager), "<CurrentSong>k__BackingField");

    internal static void Validate(ArchiveImport import)
    {
        var beatnet = Chainloader.PluginInfos.ContainsKey("splash02.beatnet");
        var slots = BeatmapIndex.defaultIndex.Difficulties;
        foreach (var folder in import.Folders)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in ArchiveImport.GetCharts(folder))
            {
                var original = File.ReadAllText(path);
                var text = original.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", Environment.NewLine);
                var chart = BeatmapParser.ParseBeatmap(text, BeatmapParserEngine.SectionTypes.Everything);
                try
                {
                    if (chart == null || string.IsNullOrWhiteSpace(chart.metadata.title)
                        || chart.hitObjects.Count == 0 || chart.timingPoints.Count == 0)
                    {
                        throw new InvalidDataException("invalid beatmap: " + Path.GetFileName(path));
                    }

                    var slot = ResolveSlot(path, chart.metadata.version, slots);
                    if (slot == null || !BeatmapIndex.defaultIndex.DifficultyIsSelectable(slot))
                    {
                        throw new InvalidDataException("unknown beatmap difficulty: " + Path.GetFileName(path));
                    }

                    if (!used.Add(slot))
                    {
                        throw new InvalidDataException("duplicate difficulty: " + slot.ToLowerInvariant());
                    }

                    if (text != original)
                    {
                        File.WriteAllText(path, text);
                    }

                    if (!beatnet)
                    {
                        var target = Path.Combine(folder, slot + ".txt");
                        if (!path.Equals(target, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Move(path, target);
                        }
                    }
                }
                finally
                {
                    if (chart != null)
                    {
                        UnityEngine.Object.Destroy(chart);
                    }
                }
            }

            if (!beatnet)
            {
                var charts = new HashSet<string>(ArchiveImport.GetCharts(folder), StringComparer.OrdinalIgnoreCase);
                foreach (var path in Directory.EnumerateFiles(folder).Where(path => Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!charts.Contains(path))
                    {
                        File.Move(path, path + ".asset");
                    }
                }
            }
        }
    }

    private static string? ResolveSlot(string path, string? version, string[] slots)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var start = name.LastIndexOf('[');
        if (start >= 0 && name.EndsWith("]", StringComparison.Ordinal))
        {
            var label = name.Substring(start + 1, name.Length - start - 2).Trim();
            if (label.Length > 0)
            {
                return FindSlot(label, slots);
            }
        }

        return slots.FirstOrDefault(slot => name.IndexOf(slot, StringComparison.OrdinalIgnoreCase) >= 0)
            ?? FindSlot(version?.Trim(), slots);
    }

    private static string? FindSlot(string? label, string[] slots)
    {
        if (string.IsNullOrEmpty(label))
        {
            return null;
        }

        if (label!.Equals("Expert", StringComparison.OrdinalIgnoreCase))
        {
            label = "Hard";
        }

        return slots.FirstOrDefault(slot => slot.Equals(label, StringComparison.OrdinalIgnoreCase))
            ?? slots.FirstOrDefault(slot => slot == "Star");
    }

    internal static void Reload()
    {
        if (Chainloader.PluginInfos.TryGetValue("splash02.beatnet", out var beatnet))
        {
            var loader = beatnet.Instance.GetType().Assembly.GetType("BEATNET.CustomSongLoader");
            var reload = loader == null ? null : AccessTools.Method(loader, "Reload");
            if (reload != null)
            {
                reload.Invoke(null, null);
                return;
            }
        }

        var database = ArcadeSongDatabase.Instance;
        if (database == null)
        {
            throw new InvalidOperationException("arcade song database is unavailable");
        }

        var previous = database.SongDatabase.Values.Select(item => item.Beatmap).Distinct().ToArray();
        var manager = ArcadeBGMManager.Instance;
        var current = manager != null ? Current.GetValue(manager) as ArcadeSongDatabase.BeatmapItem : null;
        var pending = manager != null ? Pending.GetValue(manager) as ArcadeSongDatabase.BeatmapItem : null;
        database.LoadDatabase();
        var retained = new HashSet<Beatmap>(database.SongDatabase.Values.Select(item => item.Beatmap));
        if (manager != null)
        {
            if (current != null)
            {
                var replacement = database.GetBeatmapItemByPath(current.Path) ?? current;
                Current.SetValue(manager, replacement);
                CurrentSong.SetValue(null, replacement);
                retained.Add(replacement.Beatmap);
            }

            if (pending != null)
            {
                var replacement = database.GetBeatmapItemByPath(pending.Path) ?? pending;
                Pending.SetValue(manager, replacement);
                retained.Add(replacement.Beatmap);
            }
        }

        database.RefreshSongList();
        foreach (var chart in previous)
        {
            if (!retained.Contains(chart))
            {
                UnityEngine.Object.Destroy(chart);
            }
        }
    }

    internal static void Verify(ArchiveImport import)
    {
        var database = ArcadeSongDatabase.Instance;
        if (database == null)
        {
            throw new InvalidOperationException("arcade song database is unavailable");
        }

        foreach (var folder in import.InstalledFolders)
        {
            var count = database.SongDatabase.Values.Count(item => item.CustomSong
                && Path.GetFullPath(item.Song.CustomPath).Equals(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase));
            if (count != ArchiveImport.GetCharts(folder).Length)
            {
                throw new InvalidDataException("game could not load all imported beatmaps");
            }
        }
    }

    internal static void Select(ArchiveImport import)
    {
        var database = ArcadeSongDatabase.Instance;
        var list = ArcadeSongList.Instance;
        if (database == null || list == null)
        {
            throw new InvalidOperationException("arcade song list is unavailable");
        }

        var folder = import.InstalledFolders.First();
        var song = database.SongDatabase.Values.Where(item => item.CustomSong
                && Path.GetFullPath(item.Song.CustomPath).Equals(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.BeatmapInfo.difficulty == ArcadeSongDatabase.SelectedDifficulty)
            .ThenBy(item => Array.IndexOf(database.BeatmapIndex.Difficulties, item.BeatmapInfo.difficulty))
            .FirstOrDefault();
        if (song == null)
        {
            throw new InvalidOperationException("imported song is unavailable");
        }

        database.SetDifficulty(song.BeatmapInfo.difficulty);
        database.SetCategory(song.Song.Category);
        var index = database.IndexOfSong(song.Song);
        if (index < 0)
        {
            throw new InvalidOperationException("imported song is missing from the song list");
        }

        list.SetSelectedSongIndex(index);
    }

    [HarmonyPatch(typeof(ArcadeSongDatabase), nameof(ArcadeSongDatabase.LoadDatabase))]
    private static class CustomSongsPatch
    {
        private static void Prefix(ArcadeSongDatabase __instance, ref bool ____loadCustomSongs, BeatmapIndex.Category ___customCategory)
        {
            if (Directory.Exists(Path.Combine(Application.persistentDataPath, "CustomSongs")))
            {
                ____loadCustomSongs = true;
                if (!__instance.SelectableCategories.Contains(___customCategory))
                {
                    __instance.SelectableCategories.Add(___customCategory);
                }
            }
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Arcade.UI.MenuStates;
using Arcade.UI.SongSelect;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Unimportable;

[BepInPlugin(Id, "unimportable", Version)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Id = "splash02.unimportable";
    public const string Version = "1.0.0";

    private readonly ConcurrentQueue<string[]> drops = new();
    private readonly ConcurrentQueue<string> errors = new();
    private readonly Queue<(string Path, bool Delete)> pending = new();
    private readonly CancellationTokenSource cancellation = new();
    private ConfigEntry<bool> deleteSource = null!;
    private FileDrop? fileDrop;
    private Harmony? patches;
    private Task<ArchiveImport>? preparation;
    private Task<string?>? finishing;
    private (string Path, bool Delete) active;
    private string songs = string.Empty;
    private bool hookFailed;

    private void Awake()
    {
        Dependencies.Initialize();
        deleteSource = Config.Bind("Import", "DeleteSourceArchive", true, "Delete the dropped source archive after a successful import");
        songs = Path.Combine(Application.persistentDataPath, "CustomSongs");
        patches = new Harmony(Id);
        patches.PatchAll(typeof(Plugin).Assembly);
        if (Application.platform == RuntimePlatform.WindowsPlayer)
        {
            fileDrop = new FileDrop(drops, errors);
        }
        else
        {
            Logger.LogWarning("file drop requires windows");
        }

        Logger.LogInfo("unimportable loaded");
    }

    private static bool CanImport => ArcadeMenuStateMachine.Instance != null
        && ArcadeMenuStateMachine.Instance.isActiveAndEnabled
        && ArcadeMenuStateMachine.Instance.CurrentState?.StateName == EArcadeMenuStates.SongSelect
        && ArcadeSongDatabase.Instance != null && ArcadeSongList.Instance != null
        && ArcadeSongList.Instance.gameObject.activeInHierarchy;

    private void Update()
    {
        var ready = CanImport;
        if (!hookFailed)
        {
            try
            {
                fileDrop?.Update(ready);
            }
            catch (Exception error)
            {
                hookFailed = true;
                Logger.LogError("cannot enable file drop: " + error.Message);
            }
        }

        while (errors.TryDequeue(out var error))
        {
            Logger.LogWarning("cannot read dropped files: " + error);
        }

        while (drops.TryDequeue(out var files))
        {
            if (!ready)
            {
                continue;
            }

            foreach (var path in files)
            {
                pending.Enqueue((path, deleteSource.Value));
            }
        }

        if (finishing != null)
        {
            if (!finishing.IsCompleted)
            {
                return;
            }

            try
            {
                var warning = finishing.GetAwaiter().GetResult();
                if (warning != null)
                {
                    Logger.LogWarning(warning);
                }
            }
            catch (Exception failure)
            {
                Logger.LogWarning("cannot clean up import: " + failure.Message);
            }

            finishing = null;
        }

        if (preparation != null && preparation.IsCompleted)
        {
            if (!ready)
            {
                return;
            }

            CompleteImport();
        }

        if (ready && preparation == null && finishing == null && pending.Count > 0)
        {
            active = pending.Dequeue();
            var source = active.Path;
            var token = cancellation.Token;
            Logger.LogInfo("importing " + Path.GetFileName(source));
            preparation = Task.Run(() => ArchiveImport.Prepare(source, songs, token), token);
        }
    }

    private void CompleteImport()
    {
        ArchiveImport? import = null;
        try
        {
            import = preparation!.GetAwaiter().GetResult();
            SongImport.Validate(import);
            import.Commit(songs);
            SongImport.Reload();
            SongImport.Verify(import);
            SongImport.Select(import);
            Logger.LogInfo("imported " + import.Folders.Length + " songs");
            var completed = import;
            var removeSource = active.Delete;
            finishing = Task.Run(() => FinishImport(completed, removeSource));
        }
        catch (Exception error)
        {
            Logger.LogWarning("cannot import " + Path.GetFileName(active.Path) + ": " + error.Message);
            if (import != null)
            {
                var restored = false;
                try
                {
                    var committed = import.InstalledFolders.Any();
                    import.Rollback();
                    restored = true;
                    if (committed)
                    {
                        SongImport.Reload();
                    }
                }
                catch (Exception rollbackError)
                {
                    Logger.LogError("cannot restore import: " + rollbackError.Message);
                }
                finally
                {
                    if (restored)
                    {
                        try
                        {
                            import.Dispose();
                        }
                        catch (Exception cleanupError)
                        {
                            Logger.LogWarning("cannot clean up import: " + cleanupError.Message);
                        }
                    }
                }
            }
        }
        finally
        {
            preparation = null;
        }
    }

    private static string? FinishImport(ArchiveImport import, bool removeSource)
    {
        using (import)
        {
            if (removeSource)
            {
                try
                {
                    import.DeleteSource();
                }
                catch (Exception error)
                {
                    return "source archive kept: " + error.Message;
                }
            }
        }

        return null;
    }

    private void OnDestroy()
    {
        fileDrop?.Dispose();
        cancellation.Cancel();
        if (preparation != null)
        {
            preparation.ContinueWith(task =>
            {
                if (task.Status == TaskStatus.RanToCompletion)
                {
                    try
                    {
                        task.Result.Dispose();
                    }
                    catch (Exception error)
                    {
                        Logger.LogWarning("cannot clean up import: " + error.Message);
                    }
                }
                else if (task.IsFaulted)
                {
                    _ = task.Exception;
                }
            }, TaskScheduler.Default);
        }

        if (finishing != null)
        {
            finishing.ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    _ = task.Exception;
                }
            }, TaskScheduler.Default);
        }

        patches?.UnpatchSelf();
    }
}

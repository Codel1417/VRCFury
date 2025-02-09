using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace VF.Updater {
    public class PackageActions {
        private static bool alreadyRan = false;
        private List<(string, string)> addPackages = new List<(string, string)>();
        private List<string> removePackages = new List<string>();
        private List<string> deleteDirectories = new List<string>();
        private List<Marker> createMarkers = new List<Marker>();
        private List<Marker> removeMarkers = new List<Marker>();
        private bool sceneCloseNeeded = false;
        private Action<string> DebugLog;

        public PackageActions(Action<string> debugLog) {
            DebugLog = debugLog;
        }

        public void AddPackage(string name, string path) {
            addPackages.Add((name,path));
        }

        public void RemovePackage(string name) {
            removePackages.Add(name);
        }

        public void RemoveDirectory(string path) {
            deleteDirectories.Add(path);
        }
        
        public void CreateMarker(Marker marker) {
            createMarkers.Add(marker);
        }
        
        public void RemoveMarker(Marker marker) {
            removeMarkers.Add(marker);
        }

        public void SceneCloseNeeded() {
            sceneCloseNeeded = true;
        }

        public bool NeedsRun() {
            return addPackages.Count > 0
                   || removePackages.Count > 0
                   || deleteDirectories.Count > 0
                   || createMarkers.Count > 0
                   || removeMarkers.Count > 0;
        }

        public async Task Run() {
            if (!NeedsRun()) return;

            // safety in case the updater ran twice somehow
            if (alreadyRan) return;
            alreadyRan = true;
            
            await AsyncUtils.Progress($"Performing package actions ...");

            if (sceneCloseNeeded) {
                await SceneCloser.CloseScenes();
            }
            
            await AsyncUtils.InMainThread(EditorApplication.LockReloadAssemblies);
            try {
                await RunInner();
            } finally {
                await AsyncUtils.InMainThread(EditorApplication.UnlockReloadAssemblies);
            }
            
            await AsyncUtils.Progress("Scripts are reloading ...");
            await TriggerRecompile();
            await Task.Delay(1000);
            await TriggerRecompile();
            await Task.Delay(5000);
            await TriggerRecompile();
            for (var i = 0; i < 6; i++) {
                await Task.Delay(10000);
                await TriggerRecompile();
            }
        }

        private async Task TriggerRecompile() {
            DebugLog("Triggering asset import and script recompilation ...");
            await AsyncUtils.InMainThread(() => AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport));
            await AsyncUtils.InMainThread(CompilationPipeline.RequestScriptCompilation);
        }

        private async Task RunInner() {
            // Always remove com.unity.multiplayer-hlapi before doing any package work, because otherwise
            // unity sometimes throws "Copying assembly from Temp/com.unity.multiplayer-hlapi.Runtime.dll
            // to Library/ScriptAssemblies/com.unity.multiplayer-hlapi.Runtime.dll failed and fails to
            // recompile assemblies -_-.
            // Luckily, nobody uses multiplayer-hlapi in a vrchat project anyways.
            var list = await ListInstalledPacakges();
            if (list.Any(p => p.name == "com.unity.multiplayer-hlapi")) {
                await AsyncUtils.Progress($"Removing com.unity.multiplayer-hlapi ...");
                await PackageRequest(() => Client.Remove("com.unity.multiplayer-hlapi"));
            }

            foreach (var dir in deleteDirectories) {
                if (Directory.Exists(dir) && dir.StartsWith("Assets/")) await AsyncUtils.InMainThread(() => AssetDatabase.DeleteAsset(dir));
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            foreach (var marker in removeMarkers) {
                marker.Clear();
            }

            foreach (var name in removePackages) {
                await AsyncUtils.Progress($"Removing package {name} ...");
                DebugLog($"Removing package {name}");
                await PackageRequest(() => Client.Remove(name));
                var savedTgzPath = $"Packages/{name}.tgz";
                if (File.Exists(savedTgzPath)) {
                    DebugLog($"Deleting {savedTgzPath}");
                    File.Delete(savedTgzPath);
                }
            }

            foreach (var (name,path) in addPackages) {
                await AsyncUtils.Progress($"Importing package {name} ...");
                var savedTgzPath = $"Packages/{name}.tgz";
                if (File.Exists(savedTgzPath)) {
                    DebugLog($"Deleting {savedTgzPath}");
                    File.Delete(savedTgzPath);
                }
                if (Directory.Exists($"Packages/{name}")) {
                    DebugLog($"Deleting Packages/{name}");
                    Directory.Delete($"Packages/{name}", true);
                }
                File.Copy(path, savedTgzPath);
                DebugLog($"Adding package file:{name}.tgz");
                await PackageRequest(() => Client.Add($"file:{name}.tgz"));
            }

            //await EnsureVrcfuryEmbedded();

            foreach (var marker in createMarkers) {
                marker.Create();
            }
        }
        
        // Vrcfury packages are all "local" (not embedded), because it makes them read-only which is nice.
        // However, the creator companion can only see embedded packages, so we do this to com.vrcfury.vrcfury only.
        public async Task EnsureVrcfuryEmbedded() {
            foreach (var local in await ListInstalledPacakges()) {
                if (local.name == "com.vrcfury.vrcfury" && local.source == PackageSource.LocalTarball) {
                    DebugLog($"Embedding package {local.name}");
                    await PackageRequest(() => Client.Embed(local.name));
                }
            }
        }

        private PackageCollection _cachedList = null;
        public async Task<PackageCollection> ListInstalledPacakges() {
            if (_cachedList != null) {
                return _cachedList;
            }
            DebugLog("(list packages start)");
            _cachedList = await PackageRequest(() => Client.List(true, false));
            DebugLog("(list packages end)");
            return _cachedList;
        }
        
        private static async Task<T> PackageRequest<T>(Func<Request<T>> requestProvider) {
            var request = await AsyncUtils.InMainThread(requestProvider);
            await PackageRequest(request);
            return request.Result;
        }
        private static async Task PackageRequest(Func<Request> requestProvider) {
            var request = await AsyncUtils.InMainThread(requestProvider);
            await PackageRequest(request);
        }
        private static Task PackageRequest(Request request) {
            var promise = new TaskCompletionSource<object>();
            void Check() {
                if (!request.IsCompleted) {
                    AsyncUtils.ScheduleNextTick(Check);
                    return;
                }
                if (request.Status == StatusCode.Failure) {
                    promise.SetException(new Exception(request.Error.message));
                    return;
                }
                promise.SetResult(null);
            }
            AsyncUtils.ScheduleNextTick(Check);
            return promise.Task;
        }
    }
}

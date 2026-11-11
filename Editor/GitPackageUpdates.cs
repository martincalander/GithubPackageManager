using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;

namespace MartinCalander.GithubPackageManager.Editor
{
#if UNITY_6000_5_OR_NEWER
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    internal static class GitPackageUpdates
    {
        internal const string MenuPath = "Window/Package Management/Update Git Packages";
        private const string Title = "Git packages";
        private const double TimeoutSeconds = 600;
        private static PackageManagerSubmoduleActions.OperationResources resources;
        private static AddAndRemoveRequest request;
        private static Dependency[] pending;
        private static double deadline;
        private static int progressId = -1;
        internal static bool IsBusy => request != null || resources != null;
        internal static string LastSummary { get; private set; } = string.Empty;

        internal readonly struct Dependency
        {
            internal readonly string Name, Reference, Commit;

            internal Dependency(string name, string reference, string commit)
            {
                Name = name;
                Reference = reference;
                Commit = commit;
            }
        }

        internal readonly struct Outcome
        {
            internal readonly string Summary;
            internal readonly int Changed;
            internal readonly bool Failed;

            internal Outcome(string summary, int changed, bool failed)
            {
                Summary = summary;
                Changed = changed;
                Failed = failed;
            }
        }

        [MenuItem(MenuPath)]
        private static void UpdateFromMenu() => Start();

        // Only direct manifest dependencies that Unity currently resolves from Git are refreshed.
        internal static Dependency[] FindDependencies(IEnumerable<PackageInfo> packages)
        {
            var found = new Dictionary<string, Dependency>(StringComparer.Ordinal);
            foreach (PackageInfo package in packages ?? Enumerable.Empty<PackageInfo>())
            {
                if (package == null || package.source != PackageSource.Git || !package.isDirectDependency ||
                    string.IsNullOrEmpty(package.name) || found.ContainsKey(package.name))
                    continue;
                string prefix = package.name + "@";
                string id = package.packageId;
                if (id == null || id.Length <= prefix.Length || !id.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                string reference = id.Substring(prefix.Length);
                if (reference.Any(char.IsControl) || reference.Trim().Length != reference.Length)
                    continue;
                found.Add(package.name, new Dependency(package.name, reference, package.git?.hash ?? string.Empty));
            }
            return found.Values.OrderBy(dependency => dependency.Name, StringComparer.Ordinal).ToArray();
        }

        internal static Outcome Describe(IReadOnlyList<Dependency> before, IEnumerable<PackageInfo> after)
        {
            string Short(string commit) => string.IsNullOrEmpty(commit) ? "an unknown commit" :
                commit.Length > 12 ? commit.Substring(0, 12) : commit;
            var resolved = new Dictionary<string, PackageInfo>(StringComparer.Ordinal);
            foreach (PackageInfo package in after ?? Enumerable.Empty<PackageInfo>())
                if (package != null && !string.IsNullOrEmpty(package.name))
                    resolved[package.name] = package;
            var moved = new List<string>();
            var problems = new List<string>();
            foreach (Dependency dependency in before)
            {
                if (!resolved.TryGetValue(dependency.Name, out PackageInfo package) || package.source != PackageSource.Git)
                    problems.Add(dependency.Name + " is no longer resolved from Git.");
                else if (package.errors != null && package.errors.Length != 0)
                    problems.Add(dependency.Name + ": " + string.Join(" ", package.errors.Select(error => error.message)));
                else if (!string.Equals(package.git?.hash ?? string.Empty, dependency.Commit, StringComparison.Ordinal))
                    moved.Add(dependency.Name + " from " + Short(dependency.Commit) + " to " + Short(package.git?.hash));
            }
            string count = before.Count + (before.Count == 1 ? " Git package" : " Git packages");
            string summary = moved.Count == 0
                ? "Checked " + count + ". All are already at the latest commit."
                : "Checked " + count + ". Updated " + moved.Count + ":\n" + string.Join("\n", moved);
            if (problems.Count != 0)
                summary += "\nProblems:\n" + string.Join("\n", problems);
            return new Outcome(summary, moved.Count, problems.Count != 0);
        }

        internal static string BlockingReason(bool busy, bool playing, bool working) =>
            busy ? "Wait for the current package operation to finish." :
            playing ? "Exit Play mode before updating Git packages." :
            working ? "Wait for Unity to finish importing and compiling, then choose Update Git Packages again." : null;

        private static void Start()
        {
            string reason = BlockingReason(IsBusy || PackageManagerSubmoduleActions.IsBusy,
                EditorApplication.isPlayingOrWillChangePlaymode, EditorApplication.isCompiling || EditorApplication.isUpdating);
            if (reason != null)
            {
                EditorUtility.DisplayDialog(Title, reason, "OK");
                return;
            }
            Dependency[] dependencies = FindDependencies(PackageInfo.GetAllRegisteredPackages());
            if (dependencies.Length == 0)
            {
                EditorUtility.DisplayDialog(Title, "This project has no Git dependencies in Packages/manifest.json.", "OK");
                return;
            }
            try
            {
                LastSummary = string.Empty;
                pending = dependencies;
                resources = new PackageManagerSubmoduleActions.OperationResources(SetReloadLocked, SetRefreshLocked, SetCallbacks);
                resources.Acquire();
                progressId = Progress.Start("Updating Git packages", "Fetching " + dependencies.Length +
                    (dependencies.Length == 1 ? " Git package..." : " Git packages..."), Progress.Options.Indefinite);
                // Adding the unchanged manifest reference makes Unity fetch that reference again,
                // with or without a lockfile. Other dependencies and their locked versions stay as they are.
                request = Client.AddAndRemove(dependencies.Select(dependency => dependency.Reference).ToArray());
                deadline = EditorApplication.timeSinceStartup + TimeoutSeconds;
            }
            catch (Exception exception)
            {
                Finish(exception.GetBaseException().Message);
            }
        }

        private static void SetReloadLocked(bool locked)
        {
            if (locked)
                EditorApplication.LockReloadAssemblies();
            else
                EditorApplication.UnlockReloadAssemblies();
        }

        private static void SetRefreshLocked(bool locked)
        {
            if (locked)
                AssetDatabase.DisallowAutoRefresh();
            else
                AssetDatabase.AllowAutoRefresh();
        }

        private static void SetCallbacks(bool subscribe)
        {
            EditorApplication.update -= Poll;
            EditorApplication.wantsToQuit -= CanQuit;
            if (subscribe)
            {
                EditorApplication.update += Poll;
                EditorApplication.wantsToQuit += CanQuit;
            }
        }

        private static bool CanQuit()
        {
            if (!IsBusy)
                return true;
            EditorUtility.DisplayDialog(Title, "Wait for the Git package update to finish before closing Unity.", "OK");
            return false;
        }

        private static void Poll()
        {
            try
            {
                if (request == null)
                    return;
                if (!request.IsCompleted)
                {
                    if (EditorApplication.timeSinceStartup >= deadline)
                        Finish("Unity did not finish updating Git packages within ten minutes. Check Package Manager for errors.");
                    return;
                }
                if (request.Status == StatusCode.Failure)
                    Finish("Unity could not update Git packages: " + request.Error?.message);
                else
                    Finish(null, request.Result);
            }
            catch (Exception exception)
            {
                Finish(exception.GetBaseException().Message);
            }
        }

        private static void Finish(string error, IEnumerable<PackageInfo> result = null)
        {
            Dependency[] before = pending;
            bool started = request != null;
            request = null;
            pending = null;
            try
            {
                Outcome outcome = error != null || before == null
                    ? new Outcome(error ?? "The Git package update did not start.", 0, true)
                    : Describe(before, result);
                LastSummary = outcome.Summary;
                if (progressId >= 0)
                {
                    try { Progress.Finish(progressId, outcome.Failed ? Progress.Status.Failed : Progress.Status.Succeeded); }
                    catch { }
                }
                progressId = -1;
                if (started)
                    PackageManagerPackageStatus.Refresh();
                if (outcome.Failed)
                {
                    Debug.LogError("Update Git Packages: " + outcome.Summary);
                    EditorUtility.DisplayDialog(Title, outcome.Summary, "OK");
                }
                else if (outcome.Changed != 0)
                    Debug.Log("Update Git Packages: " + outcome.Summary);
                else
                    EditorUtility.DisplayDialog(Title, outcome.Summary, "OK");
            }
            finally
            {
                PackageManagerSubmoduleActions.OperationResources owned = resources;
                resources = null;
                try { owned?.Dispose(); }
                catch (Exception exception) { Debug.LogError("Update Git Packages could not release Editor locks: " + exception.Message); }
                if (started)
                {
                    // The request reports the fetched commits, but the Editor keeps the previous
                    // package folders until a resolve applies them. Resolve once the locks are released.
                    try { Client.Resolve(); }
                    catch (Exception exception) { Debug.LogError("Update Git Packages could not apply the fetched packages: " + exception.Message); }
                }
            }
        }
    }
}

using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MartinCalander.GithubPackageManager.Editor.Tests
{
    [Category("PackageSourceFeatures")]
    public sealed class GitPackageUpdatesTests
    {
        private const string First = "1111111111111111111111111111111111111111";
        private const string Second = "2222222222222222222222222222222222222222";

        private static PackageInfo Package(string name, string reference, PackageSource source = PackageSource.Git,
            bool direct = true, string commit = First, string error = null) => JsonUtility.FromJson<PackageInfo>(
            "{\"m_Name\":\"" + name + "\",\"m_PackageId\":\"" + name + "@" + reference + "\",\"m_Source\":" + (int)source +
            ",\"m_IsDirectDependency\":" + (direct ? "true" : "false") + ",\"m_Git\":{\"m_Hash\":\"" + commit + "\"}" +
            (error == null ? string.Empty : ",\"m_Errors\":[{\"m_Message\":\"" + error + "\"}]") + "}");

        [Test]
        public void OnlyDirectGitDependenciesAreSelectedWithTheirManifestReference()
        {
            PackageInfo wrongId = Package("com.example.wrongid", "https://example.com/wrong.git");
            typeof(PackageInfo).GetField("m_PackageId", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(wrongId, "com.example.other@https://example.com/wrong.git");
            var packages = new[]
            {
                Package("com.example.zeta", "https://example.com/zeta.git?path=/Package#release"),
                Package("com.example.alpha", "git@github.com:example/alpha.git", commit: Second),
                Package("com.example.registry", "1.2.3", PackageSource.Registry),
                Package("com.example.embedded", "file:/project/Packages/com.example.embedded", PackageSource.Embedded),
                Package("com.example.indirect", "https://example.com/indirect.git", direct: false),
                Package("com.example.control", "https://example.com/control.git\\n--upload-pack=x"),
                Package("com.example.padded", " https://example.com/padded.git"),
                Package("com.example.empty", string.Empty),
                wrongId,
                null
            };

            GitPackageUpdates.Dependency[] found = GitPackageUpdates.FindDependencies(packages);

            Assert.That(found.Select(dependency => dependency.Name), Is.EqualTo(new[] { "com.example.alpha", "com.example.zeta" }));
            Assert.That(found[0].Reference, Is.EqualTo("git@github.com:example/alpha.git"));
            Assert.That(found[0].Commit, Is.EqualTo(Second));
            Assert.That(found[1].Reference, Is.EqualTo("https://example.com/zeta.git?path=/Package#release"));
            Assert.That(GitPackageUpdates.FindDependencies(null), Is.Empty);
        }

        [Test]
        public void SummaryReportsMovedCommitsAndUnchangedPackages()
        {
            var before = GitPackageUpdates.FindDependencies(new[]
            {
                Package("com.example.moved", "https://example.com/moved.git"),
                Package("com.example.same", "https://example.com/same.git")
            });

            GitPackageUpdates.Outcome unchanged = GitPackageUpdates.Describe(before, new[]
            {
                Package("com.example.moved", "https://example.com/moved.git"),
                Package("com.example.same", "https://example.com/same.git")
            });
            GitPackageUpdates.Outcome moved = GitPackageUpdates.Describe(before, new[]
            {
                Package("com.example.moved", "https://example.com/moved.git", commit: Second),
                Package("com.example.same", "https://example.com/same.git")
            });

            Assert.That(unchanged.Failed, Is.False);
            Assert.That(unchanged.Changed, Is.Zero);
            Assert.That(unchanged.Summary, Is.EqualTo("Checked 2 Git packages. All are already at the latest commit."));
            Assert.That(moved.Failed, Is.False);
            Assert.That(moved.Changed, Is.EqualTo(1));
            Assert.That(moved.Summary, Is.EqualTo(
                "Checked 2 Git packages. Updated 1:\ncom.example.moved from 111111111111 to 222222222222"));
        }

        [Test]
        public void SummaryFailsWhenAPackageIsMissingReplacedOrReportsErrors()
        {
            var before = GitPackageUpdates.FindDependencies(new[]
            {
                Package("com.example.errors", "https://example.com/errors.git"),
                Package("com.example.missing", "https://example.com/missing.git"),
                Package("com.example.replaced", "https://example.com/replaced.git")
            });

            GitPackageUpdates.Outcome outcome = GitPackageUpdates.Describe(before, new[]
            {
                Package("com.example.errors", "https://example.com/errors.git", commit: Second, error: "Clone failed."),
                Package("com.example.replaced", "file:/project/Packages/com.example.replaced", PackageSource.Embedded)
            });

            Assert.That(outcome.Failed, Is.True);
            Assert.That(outcome.Changed, Is.Zero);
            Assert.That(outcome.Summary, Does.Contain("com.example.errors: Clone failed."));
            Assert.That(outcome.Summary, Does.Contain("com.example.missing is no longer resolved from Git."));
            Assert.That(outcome.Summary, Does.Contain("com.example.replaced is no longer resolved from Git."));
            Assert.That(GitPackageUpdates.Describe(before, null).Failed, Is.True);
        }

        [Test]
        public void UpdateWaitsForAnIdleEditor()
        {
            Assert.That(GitPackageUpdates.BlockingReason(false, false, false), Is.Null);
            Assert.That(GitPackageUpdates.BlockingReason(true, true, true), Does.Contain("current package operation"));
            Assert.That(GitPackageUpdates.BlockingReason(false, true, true), Does.Contain("Play mode"));
            Assert.That(GitPackageUpdates.BlockingReason(false, false, true), Does.Contain("importing and compiling"));
        }

        [Test]
        public void CommandIsRegisteredBesideUnityPackageManagement()
        {
            MethodInfo command = typeof(GitPackageUpdates).GetMethod("UpdateFromMenu", BindingFlags.Static | BindingFlags.NonPublic);

            Assert.That(command, Is.Not.Null);
            Assert.That(command.GetCustomAttribute<MenuItem>().menuItem, Is.EqualTo("Window/Package Management/Update Git Packages"));
            Assert.That(GitPackageUpdates.IsBusy, Is.False);
        }
    }
}

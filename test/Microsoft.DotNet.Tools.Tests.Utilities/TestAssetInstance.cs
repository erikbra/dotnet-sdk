﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Reflection;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.Tools.Common;
using DotnetCommand = Microsoft.DotNet.Tools.Test.Utilities.DotnetCommand;

namespace Microsoft.DotNet.TestFramework
{
    public class TestAssetInstance
    {
        public DirectoryInfo MigrationBackupRoot { get; }

        public DirectoryInfo Root { get; }

        public TestAssetInfo TestAssetInfo { get; }

        private bool _filesCopied = false;

        private bool _restored = false;

        public TestAssetInstance(TestAssetInfo testAssetInfo, DirectoryInfo root)
        {
            if (testAssetInfo == null)
            {
                throw new ArgumentException(nameof(testAssetInfo));
            }

            if (root == null)
            {
                throw new ArgumentException(nameof(root));
            }

            TestAssetInfo = testAssetInfo;

            Root = root;

            MigrationBackupRoot = new DirectoryInfo(Path.Combine(root.Parent.FullName, "backup"));

            if (Root.Exists)
            {
                try
                {
                    Root.Delete(recursive: true);
                }
                catch (IOException ex)
                {
                    throw new InvalidOperationException("Unable to delete directory: " + Root.FullName, ex);
                }
            }

            Root.Create();

            if (MigrationBackupRoot.Exists)
            {
                MigrationBackupRoot.Delete(recursive: true);
            }
        }

        public TestAssetInstance WithSourceFiles()
        {
            if (!_filesCopied)
            {
                CopySourceFiles();

                _filesCopied = true;
            }

            return this;
        }

        public TestAssetInstance WithRestoreFiles()
        {
            if (!_restored)
            {
                WithSourceFiles();

                RestoreAllProjects();

                _restored = true;
            }

            return this;
        }

        public TestAssetInstance WithNuGetConfig(string nugetCache, string externalRestoreSources = null)
        {
            var thisAssembly = typeof(TestAssetInstance).GetTypeInfo().Assembly;
            var newNuGetConfig = Root.GetFile("NuGet.Config");
            externalRestoreSources = externalRestoreSources ?? string.Empty;

            var content = @"<configuration>
              <packageSources>
                <add key=""dotnet-core"" value=""https://dotnetfeed.blob.core.windows.net/dotnet-core/index.json"" />
                <add key=""test-packages"" value=""$fullpath$"" />
                $externalRestoreSources$
              </packageSources>
            </configuration>";
            content = content
                .Replace("$fullpath$", nugetCache)
                .Replace("$externalRestoreSources$", externalRestoreSources);

            using (var newNuGetConfigStream =
                new FileStream(newNuGetConfig.FullName, FileMode.Create, FileAccess.Write))
            {
                var contentBytes = new UTF8Encoding(true).GetBytes(content);
                newNuGetConfigStream.Write(contentBytes, 0, contentBytes.Length);
            }

            return this;
        }

        public TestAssetInstance WithEmptyGlobalJson()
        {
            var file = Root.Parent.GetFile("global.json");

            File.WriteAllText(file.FullName, @"{}");

            return this;
        }

        public TestAssetInstance WithProjectChanges(Action<XDocument> xmlAction) => WithProjectChanges((path, project) => xmlAction(project));

        public TestAssetInstance WithProjectChanges(Action<string, XDocument> xmlAction)
        {
            var projectFileInfos = Root.GetFiles("*.*proj", SearchOption.AllDirectories);

            foreach (var projectFileInfo in projectFileInfos)
            {
                var projectFile = projectFileInfo.FullName;
                var project = XDocument.Load(projectFile);

                xmlAction(projectFile, project);

                using (var file = File.CreateText(projectFile))
                {
                    project.Save(file);
                }
            }

            return this;
        }

        private static string RebasePath(string path, string oldBaseDirectory, string newBaseDirectory)
        {
            path = Path.IsPathRooted(path) ? PathUtility.GetRelativePath(PathUtility.EnsureTrailingSlash(oldBaseDirectory), path) : path;
            return Path.Combine(newBaseDirectory, path);
        }

        private void CopySourceFiles()
        {
            var filesToCopy = TestAssetInfo.GetSourceFiles();
            foreach (var file in filesToCopy)
            {
                var newPath = RebasePath(file.FullName, TestAssetInfo.Root.FullName, Root.FullName);

                var newFile = new FileInfo(newPath);

                PathUtility.EnsureDirectoryExists(newFile.Directory.FullName);

                CopyFileAdjustingPaths(file, newFile);
            }
        }

        private void CopyFileAdjustingPaths(FileInfo source, FileInfo destination)
        {
            if (string.Equals(source.Name, "nuget.config", StringComparison.OrdinalIgnoreCase))
            {
                CopyNugetConfigAdjustingPath(source, destination);
            }
            else
            {
                source.CopyTo(destination.FullName);
            }
        }

        private void CopyNugetConfigAdjustingPath(FileInfo source, FileInfo destination)
        {
            var doc = XDocument.Load(source.FullName, LoadOptions.PreserveWhitespace);
            foreach (var packageSource in doc.Root.Element("packageSources").Elements("add").Attributes("value"))
            {
                if (!Path.IsPathRooted(packageSource.Value))
                {
                    string fullPathAtSource = Path.GetFullPath(Path.Combine(source.Directory.FullName, packageSource.Value));
                    if (!PathUtility.IsChildOfDirectory(TestAssetInfo.Root.FullName, fullPathAtSource))
                    {
                        packageSource.Value = fullPathAtSource;
                    }
                }

                using var file = new FileStream(destination.FullName, FileMode.CreateNew, FileAccess.ReadWrite);
                doc.Save(file, SaveOptions.None);
            }
        }

        private IEnumerable<FileInfo> GetProjectFiles() => Root.GetFiles(TestAssetInfo.ProjectFilePattern, SearchOption.AllDirectories);

        private void Restore(FileInfo projectFile)
        {
            var restoreArgs = new string[] { "restore", projectFile.FullName };

            var commandResult = new DotnetCommand()
                .Execute(ArgumentEscaper.EscapeAndConcatenateArgArrayForProcessStart(restoreArgs));

            commandResult.Should().Pass();
        }

        private void RestoreAllProjects()
        {
            Console.WriteLine($"TestAsset Restore '{TestAssetInfo.AssetName}'");

            foreach (var projFile in GetProjectFiles())
            {
                Restore(projFile);
            }
        }
    }
}

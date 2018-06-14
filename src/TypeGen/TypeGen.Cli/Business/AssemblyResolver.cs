﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NuGet.Configuration;
using TypeGen.Cli.Extensions;
using TypeGen.Core.Storage;
using TypeGen.Core.Business;

namespace TypeGen.Cli.Business
{
    internal class AssemblyResolver
    {
        private readonly FileSystem _fileSystem;
        private readonly string _projectFolder;

        private IEnumerable<string> _directories;
        public IEnumerable<string> Directories
        {
            get => _directories;
            set => _directories = value?.Select(d => Path.IsPathRooted(d) ? d : Path.Combine(_projectFolder, d));
        }

        private readonly IEnumerable<string> _nugetPackagesFolders;

        public AssemblyResolver(FileSystem fileSystem, string projectFolder)
        {
            _fileSystem = fileSystem;
            _projectFolder = projectFolder.ToAbsolutePath();

            NuGetPathContext nugetPathContext = NuGetPathContext.Create(_projectFolder);
            _nugetPackagesFolders = nugetPathContext.FallbackPackageFolders;

            if (!string.IsNullOrWhiteSpace(nugetPathContext.UserPackageFolder))
            {
                _nugetPackagesFolders = _nugetPackagesFolders.Concat(new[] { nugetPathContext.UserPackageFolder });
            }
        }

        public void Register()
        {
            AppDomain.CurrentDomain.AssemblyResolve += AssemblyResolve;
        }

        public void Unregister()
        {
            AppDomain.CurrentDomain.AssemblyResolve -= AssemblyResolve;
        }

        private Assembly AssemblyResolve(object sender, ResolveEventArgs args)
        {
            // step 1 - search by assembly name (nuget global + nuget fallback + user-defined)

            // nuget
            Assembly assembly = FindByAssemblyName(_nugetPackagesFolders, args.Name);
            if (assembly != null) return assembly;

            // user-defined
            assembly = FindByAssemblyName(Directories, args.Name);
            if (assembly != null) return assembly;

            // step 2 - search recursively (nuget global + nuget fallback + user-defined)

            string assemblyFileName = GetAssemblyFileName(args.Name);
            string assemblyVersion = GetAssemblyVersion(args.Name);

            // nuget
            assembly = FindRecursive(_nugetPackagesFolders, assemblyFileName, assemblyVersion);
            if (assembly != null) return assembly;

            // user-defined
            assembly = FindRecursive(Directories, assemblyFileName, assemblyVersion);
            if (assembly != null) return assembly;

            // exception if assembly not found

            throw new AssemblyResolutionException($"Could not resolve assembly: {args.Name} in any of the searched directories: {string.Join("; ", Directories)}");
        }

        private Assembly FindByAssemblyName(IEnumerable<string> directories, string assemblyFullName)
        {
            return directories.Select(directory => FindByAssemblyName(directory, assemblyFullName)).FirstOrDefault(assembly => assembly != null);
        }

        private Assembly FindByAssemblyName(string directory, string assemblyFullName)
        {
            string assemblyShortName = GetAssemblyShortName(assemblyFullName);
            string assemblyFileName = GetAssemblyFileName(assemblyFullName);
            string assemblyVersion = GetAssemblyVersion(assemblyFullName);

            string packageFolder = GetPackageFolder(directory, assemblyShortName);
            return packageFolder == null ? null : FindRecursive(packageFolder, assemblyFileName, assemblyVersion);
        }

        private string GetPackageFolder(string root, string assemblyFolder)
        {
            string packageFolder = Path.Combine(root, assemblyFolder);
            if (_fileSystem.DirectoryExists(packageFolder)) return packageFolder;

            string[] assemblyFolderParts = assemblyFolder.Split('.');
            if (assemblyFolderParts.Length > 1)
            {
                string truncatedAssemblyFolder = string.Join('.', assemblyFolderParts.Take(assemblyFolderParts.Length - 1));
                return GetPackageFolder(root, truncatedAssemblyFolder);
            }

            return null;
        }

        private Assembly FindRecursive(IEnumerable<string> directories, string assemblyFileName, string assemblyVersion)
        {
            return directories.Select(directory => FindRecursive(directory, assemblyFileName, assemblyVersion)).FirstOrDefault(assembly => assembly != null);
        }

        private Assembly FindRecursive(string directory, string assemblyFileName, string assemblyVersion)
        {
            string[] foundPaths = _fileSystem.GetFilesRecursive(directory, assemblyFileName);
            return foundPaths.Any() ? ResolveFromPaths(foundPaths, assemblyVersion) : null;
        }

        private Assembly ResolveFromPaths(IEnumerable<string> paths, string assemblyVersion)
        {
            foreach (string path in paths)
            {
                try
                {
                    Assembly assembly = Assembly.LoadFrom(path);
                    if (assembly.GetName().Version.ToString() == assemblyVersion) return assembly;
                }
                catch (BadImageFormatException)
                {
                }
            }

            return null;
        }

        private string GetAssemblyShortName(string assemblyFullName)
        {
            return assemblyFullName.Split(',')[0];
        }

        private string GetAssemblyFileName(string assemblyFullName)
        {
            return GetAssemblyShortName(assemblyFullName) + ".dll";
        }

        private string GetAssemblyVersion(string assemblyFullName)
        {
            Match match = Regex.Match(assemblyFullName, "Version=(.+?),");
            return match.Success ? match.Groups[1].Value : throw new CliException($"Could not determine assembly version of assembly: ${assemblyFullName}");
        }
    }
}

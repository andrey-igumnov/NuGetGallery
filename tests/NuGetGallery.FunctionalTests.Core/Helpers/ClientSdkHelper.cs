﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet;
using Xunit;
using Xunit.Abstractions;

namespace NuGetGallery.FunctionalTests
{
    public class ClientSdkHelper
        : HelperBase
    {
        public ClientSdkHelper(ITestOutputHelper testOutputHelper)
            : base(testOutputHelper)
        {
        }

        private static string SourceUrl
        {
            get
            {
                return UrlHelper.BaseUrl + "api/v2";
            }
        }

        /// <summary>
        /// Clears the local machine cache.
        /// </summary>
        public static void ClearMachineCache()
        {
            MachineCache.Default.Clear();
        }

        /// <summary>
        /// Checks if the given package is present in the source.
        /// </summary>
        public bool CheckIfPackageExistsInSource(string packageId, string sourceUrl)
        {
            WriteLine("Checking if package {0} exists in source {1}... ", packageId, sourceUrl);
            var packageRepository = PackageRepositoryFactory.Default.CreateRepository(sourceUrl);
            IPackage package = packageRepository.FindPackage(packageId);
            var packageExistsInSource = (package != null);
            if (packageExistsInSource)
            {
                WriteLine("Found!");
            }
            else
            {
                WriteLine("NOT Found!");
            }

            return packageExistsInSource;
        }

        /// <summary>
        /// Checks if the given package version is present in the source.
        /// </summary>
        public bool CheckIfPackageVersionExistsInSource(string packageId, string version, string sourceUrl)
        {
            var repo = PackageRepositoryFactory.Default.CreateRepository(sourceUrl);
            SemanticVersion semVersion = SemanticVersion.Parse(version);

            return VerifyWithRetry(
                $"Verifying that package {packageId} {version} exists on source {sourceUrl}",
                () =>
                {
                    var package = repo.FindPackage(packageId, semVersion);

                    return package != null;
                });
        }

        private bool VerifyWithRetry(string actionPhrase, Func<bool> action)
        {
            bool success = false;
            const int intervalSec = 30;
            const int maxAttempts = 30;

            try
            {
                WriteLine($"{actionPhrase} ({maxAttempts} attempts, interval {intervalSec} seconds).");

                for (var i = 0; i < maxAttempts && !success; i++)
                {
                    if (i != 0)
                    {
                        WriteLine($"[verification attempt {i}]: Waiting {intervalSec} seconds before next check...");
                        Thread.Sleep(intervalSec * 1000);
                    }

                    WriteLine($"[verification attempt {i}]: Executing... ");
                    success = action();
                    WriteLine(success ? "Successful!" : "NOT successful!");
                }
            }
            catch (Exception ex)
            {
                WriteLine($"{actionPhrase} threw an exception.{Environment.NewLine}{ex}");
            }

            return success;
        }

        /// <summary>
        /// Creates a package with the specified Id and Version and uploads it and checks if the upload has succeeded.
        /// Throws if the upload fails or cannot be verified in the source.
        /// </summary>
        public async Task UploadNewPackageAndVerify(string packageId, string version = "1.0.0", string minClientVersion = null, string title = null, string tags = null, string description = null, string licenseUrl = null, string dependencies = null)
        {
            await UploadNewPackage(packageId, version, minClientVersion, title, tags, description, licenseUrl, dependencies);

            VerifyPackageExistsInSource(packageId, version);
        }

        public async Task UploadNewPackage(string packageId, string version = "1.0.0", string minClientVersion = null,
            string title = null, string tags = null, string description = null, string licenseUrl = null,
            string dependencies = null, string apiKey = null)
        {
            if (string.IsNullOrEmpty(packageId))
            {
                packageId = DateTime.Now.Ticks.ToString();
            }

            WriteLine("Uploading new package '{0}', version '{1}'", packageId, version);

            var packageCreationHelper = new PackageCreationHelper(TestOutputHelper);
            var packageFullPath = await packageCreationHelper.CreatePackage(packageId, version, minClientVersion, title, tags, description, licenseUrl, dependencies);

            await UploadExistingPackage(packageFullPath);

            // Delete package from local disk once it gets uploaded
            CleanCreatedPackage(packageFullPath);
        }

        public async Task UploadExistingPackage(string packageFullPath, string apiKey = null)
        {
            var commandlineHelper = new CommandlineHelper(TestOutputHelper);
            var processResult = await commandlineHelper.UploadPackageAsync(packageFullPath, UrlHelper.V2FeedPushSourceUrl, apiKey);

            Assert.True(processResult.ExitCode == 0,
                "The package upload via Nuget.exe did not succeed properly. Check the logs to see the process error and output stream.  Exit Code: " +
                processResult.ExitCode + ". Error message: \"" + processResult.StandardError + "\"");
        }

        /// <summary>
        /// Unlists a package with the specified Id and Version and checks if the unlist has succeeded.
        /// Throws if the unlist fails or cannot be verified in the source.
        /// </summary>
        public async Task UnlistPackageAndVerify(string packageId, string version = "1.0.0")
        {
            await UnlistPackage(packageId, version);

            VerifyPackageExistsInSource(packageId, version);
        }

        public async Task UnlistPackage(string packageId, string version = "1.0.0", string apiKey = null)
        {
            if (string.IsNullOrEmpty(packageId))
            {
                throw new ArgumentException($"{nameof(packageId)} cannot be null or empty!");
            }

            WriteLine("Unlisting package '{0}', version '{1}'", packageId, version);

            var commandlineHelper = new CommandlineHelper(TestOutputHelper);
            var processResult = await commandlineHelper.DeletePackageAsync(packageId, version, UrlHelper.V2FeedPushSourceUrl, apiKey);

            Assert.True(processResult.ExitCode == 0,
                "The package unlist via Nuget.exe did not succeed properly. Check the logs to see the process error and output stream.  Exit Code: " +
                processResult.ExitCode + ". Error message: \"" + processResult.StandardError + "\"");
        }

        /// <summary>
        /// Throws if the specified package cannot be found in the source.
        /// </summary>
        /// <param name="packageId">Id of the package.</param>
        /// <param name="version">Version of the package.</param>
        public void VerifyPackageExistsInSource(string packageId, string version = "1.0.0")
        {
            var packageExistsInSource = CheckIfPackageVersionExistsInSource(packageId, version, UrlHelper.V2FeedRootUrl);
            Assert.True(packageExistsInSource,
                $"Package {packageId} with version {version} is not found on the site {UrlHelper.V2FeedRootUrl}.");
        }

        /// <summary>
        /// Returns the latest stable version string for the given package.
        /// </summary>
        public static string GetLatestStableVersion(string packageId)
        {
            var repo = PackageRepositoryFactory.Default.CreateRepository(SourceUrl);
            var packages = repo.FindPackagesById(packageId).ToList();
            packages = packages.Where(item => item.IsListed()).ToList();
            packages = packages.Where(item => item.IsReleaseVersion()).ToList();
            var version = packages.Max(item => item.Version);
            return version.ToString();
        }

        public void VerifyVersionCount(string packageId, int expectedVersionCount, bool allowPreRelease = true)
        {
            var repo = PackageRepositoryFactory.Default.CreateRepository(SourceUrl);

            // To verify the count of package versions, the FindPackagesById() V2 OData endpoint is used. When the
            // gallery handles this request, it delegates to the search service. Since the search service can lag being
            // the gallery database (due to the time it takes for packages to make it through the V3 pipeline and into
            // an active Lucene index), we retry the request for a while.
            Assert.True(VerifyWithRetry(
                $"Verifying count of {packageId} versions is {expectedVersionCount}",
                () =>
                {
                    var packages = repo.FindPackagesById(packageId).ToList();
                    if (!allowPreRelease)
                    {
                        packages = packages.Where(item => item.IsReleaseVersion()).ToList();
                    }
                    var actualVersionCount = packages.Count;

                    var versionsDisplay = string.Join(", ", packages.Select(p => p.Version));
                    WriteLine($"{actualVersionCount} versions of {packageId} found: {versionsDisplay}");
                    return actualVersionCount == expectedVersionCount;
                }));
        }

        /// <summary>
        /// Returns the download count of the given package as a formatted string as it would appear in the gallery UI.
        /// </summary>
        public static string GetFormattedDownLoadStatistics(string packageId)
        {
            var formattedCount = GetDownLoadStatistics(packageId).ToString("N1", CultureInfo.InvariantCulture);
            if (formattedCount.EndsWith(".0"))
                formattedCount = formattedCount.Remove(formattedCount.Length - 2);
            return formattedCount;
        }

        /// <summary>
        /// Returns the download count of the given package.
        /// </summary>
        public static int GetDownLoadStatistics(string packageId)
        {
            var repo = PackageRepositoryFactory.Default.CreateRepository(SourceUrl);
            var package = repo.FindPackage(packageId);
            return package.DownloadCount;
        }

        /// <summary>
        /// Returns the download count of the specific version of the package.
        /// </summary>
        public static int GetDownLoadStatisticsForPackageVersion(string packageId, string packageVersion)
        {
            var repo = PackageRepositoryFactory.Default.CreateRepository(SourceUrl);
            var package = repo.FindPackage(packageId, new SemanticVersion(packageVersion));
            return package.DownloadCount;
        }

        /// <summary>
        /// Searchs the gallery to get the packages matching the specific search term and returns their count.
        /// </summary>
        public static int GetPackageCountForSearchTerm(string searchQuery)
        {
            var repo = PackageRepositoryFactory.Default.CreateRepository(SourceUrl);

            var packages = repo.Search(searchQuery, false).ToList();

            return packages.Count;
        }

        /// <summary>
        /// Given the path to the nupkg file, returns the corresponding package ID.
        /// </summary>
        public string GetPackageIdFromNupkgFile(string filePath)
        {
            try
            {
                ZipPackage pack = new ZipPackage(filePath);
                return pack.Id;
            }
            catch (Exception e)
            {
                WriteLine(" Exception thrown while trying to create zippackage for :{0}. Message {1}", filePath, e.Message);
                return null;
            }
        }

        /// <summary>
        /// Given the path to the nupkg file, returns the corresponding package ID.
        /// </summary>
        public bool IsPackageVersionUnListed(string packageId, string version)
        {
            IPackageRepository repo = PackageRepositoryFactory.Default.CreateRepository(SourceUrl);
            IPackage package = repo.FindPackage(packageId, new SemanticVersion(version), true, true);
            if (package != null)
                return !package.Listed;
            else
                return false;
        }

        /// <summary>
        /// Clears the local package folder.
        /// </summary>
        public void ClearLocalPackageFolder(string packageId, string version = "1.0.0")
        {
            string expectedDownloadedNupkgFileName = packageId + "." + version;
            string pathToNupkgFolder = Path.Combine(Environment.CurrentDirectory, expectedDownloadedNupkgFileName);
            WriteLine("Path to the downloaded Nupkg file for clearing local package folder is: " + pathToNupkgFolder);
            if (Directory.Exists(pathToNupkgFolder))
                Directory.Delete(expectedDownloadedNupkgFileName, true);
        }

        /// <summary>
        /// Given a package checks if it is installed properly in the current dir.
        /// </summary>
        public bool CheckIfPackageInstalled(string packageId)
        {
            string packageVersion = GetLatestStableVersion(packageId);
            return CheckIfPackageVersionInstalled(packageId, packageVersion);
        }

        /// <summary>
        /// Given a package checks if it that version of the package is installed.
        /// </summary>
        public bool CheckIfPackageVersionInstalled(string packageId, string packageVersion)
        {
            //string packageVersion = ClientSDKHelper.GetLatestStableVersion(packageId);
            string expectedDownloadedNupkgFileName = packageId + "." + packageVersion;
            //check if the nupkg file exists on the expected path post install
            string expectedNupkgFilePath = Path.Combine(Environment.CurrentDirectory, expectedDownloadedNupkgFileName, expectedDownloadedNupkgFileName + ".nupkg");
            WriteLine("The expected Nupkg file path after package install is: " + expectedNupkgFilePath);
            if ((!File.Exists(expectedNupkgFilePath)))
            {
                WriteLine(" Package file {0} not present after download", expectedDownloadedNupkgFileName);
                return false;
            }
            string downloadedPackageId = GetPackageIdFromNupkgFile(expectedNupkgFilePath);
            //Check that the downloaded Nupkg file is not corrupt and it indeed corresponds to the package which we were trying to download.
            if (!(downloadedPackageId.Equals(packageId)))
            {
                WriteLine("Unable to unzip the package downloaded via Nuget Core. Check log for details");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Downloads a package to local folder and see if the download is successful. Used to individual tests which extend the download scenarios.
        /// </summary>
        public void DownloadPackageAndVerify(string packageId, string version = "1.0.0")
        {
            ClearMachineCache();
            ClearLocalPackageFolder(packageId, version);

            var packageRepository = PackageRepositoryFactory.Default.CreateRepository(UrlHelper.V2FeedRootUrl);
            var packageManager = new PackageManager(packageRepository, Environment.CurrentDirectory);

            packageManager.InstallPackage(packageId, new SemanticVersion(version));

            Assert.True(CheckIfPackageVersionInstalled(packageId, version),
                "Package install failed. Either the file is not present on disk or it is corrupted. Check logs for details");
        }

        public void CleanCreatedPackage(string packageFullPath)
        {
            if (!string.IsNullOrEmpty(packageFullPath) && File.Exists(packageFullPath))
            {
                File.Delete(packageFullPath);
                Directory.Delete(Path.GetFullPath(Path.GetDirectoryName(packageFullPath)), true);
            }
        }
    }
}
// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Graph;
using Microsoft.Build.Logging;
using NuGet.Commands;
using NuGet.Commands.Restore.Utility;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.ProjectModel;

namespace NuGet.Build.Tasks.Console
{
    internal sealed class MSBuildStaticGraphRestore : IDisposable
    {
        /// <summary>
        /// Represents the name of the environment variable that user can set to specify MSBuild binary logger parameters.
        /// </summary>
        public const string BinaryLoggerParameterEnvironmentVariable = "RESTORE_TASK_BINLOG_PARAMETERS";

        private static readonly Lazy<IMachineWideSettings> MachineWideSettingsLazy = new Lazy<IMachineWideSettings>(() => new XPlatMachineWideSetting());

        /// <summary>
        /// Represents the small list of targets that must be executed in order for various restore input items to be accurate.
        /// </summary>
        private static readonly string[] TargetsToBuild =
        {
            "_CollectRestoreInputs"
        };

        private readonly IEnvironmentVariableReader _environment;

        private readonly Lazy<ConsoleLoggingQueue> _loggingQueueLazy;

        private readonly Lazy<MSBuildLogger> _msBuildLoggerLazy;

        private readonly SettingsLoadingContext _settingsLoadContext = new SettingsLoadingContext();

        public MSBuildStaticGraphRestore(IEnvironmentVariableReader environment = null)
        {
            _environment = environment ?? EnvironmentVariableWrapper.Instance;
            _loggingQueueLazy = new Lazy<ConsoleLoggingQueue>(() => new ConsoleLoggingQueue(LoggerVerbosity.Normal));
            _msBuildLoggerLazy = new Lazy<MSBuildLogger>(() => new MSBuildLogger(LoggingQueue.TaskLoggingHelper));
        }

        /// <summary>
        /// Gets a <see cref="ConsoleLoggingQueue" /> object to be used for logging.
        /// </summary>
        private ConsoleLoggingQueue LoggingQueue => _loggingQueueLazy.Value;

        /// <summary>
        /// Gets a <see cref="MSBuildLogger" /> object to be used for logging.
        /// </summary>
        private MSBuildLogger MSBuildLogger => _msBuildLoggerLazy.Value;

        public void Dispose()
        {
            if (_loggingQueueLazy.IsValueCreated)
            {
                // Disposing the logging queue will wait for the queue to be drained
                _loggingQueueLazy.Value.Dispose();
            }

            _settingsLoadContext.Dispose();
        }

        /// <summary>
        /// Restores the specified projects.
        /// </summary>
        /// <param name="entryProjectFilePath">The main project to restore.  This can be a project for a Visual Studio© Solution File.</param>
        /// <param name="globalProperties">The global properties to use when evaluation MSBuild projects.</param>
        /// <param name="options">The set of options to use when restoring.  These options come from the main MSBuild process and control how restore functions.</param>
        /// <returns><code>true</code> if the restore succeeded, otherwise <code>false</code>.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public async Task<bool> RestoreAsync(string entryProjectFilePath, IDictionary<string, string> globalProperties, IReadOnlyDictionary<string, string> options)
        {
            bool interactive = IsOptionTrue(nameof(RestoreTaskEx.Interactive), options);

            string binaryLoggerParameters = GetBinaryLoggerParameters(_environment, options);

            var dependencyGraphSpec = GetDependencyGraphSpec(entryProjectFilePath, globalProperties, interactive, binaryLoggerParameters);

            // If the dependency graph spec is null, something went wrong evaluating the projects, so return false
            if (dependencyGraphSpec == null)
            {
                return false;
            }

            static bool HasProjectToRestore(DependencyGraphSpec dgSpec, bool restorePackagesConfig)
            {
                if (dgSpec.Restore.Count > 0)
                {
                    return true;
                }

#if NETFRAMEWORK
                if (restorePackagesConfig)
                {
                    for (int i = 0; i < dgSpec.Projects.Count; i++)
                    {
                        PackageSpec project = dgSpec.Projects[i];
                        if (project.RestoreMetadata?.ProjectStyle == ProjectStyle.PackagesConfig)
                        {
                            return true;
                        }
                    }
                }
#endif

                return false;
            }

            bool restorePackagesConfig = IsOptionTrue(nameof(RestoreTaskEx.RestorePackagesConfig), options);
            if (string.Equals(Path.GetExtension(entryProjectFilePath), ".sln", StringComparison.OrdinalIgnoreCase)
                    && !HasProjectToRestore(dependencyGraphSpec, restorePackagesConfig))
            {
                MSBuildLogger.LogInformation(string.Format(CultureInfo.CurrentCulture, Strings.Log_NoProjectsForRestore));
                return true;
            }

            try
            {
                // todo: need to return Restore task output properties, like in NuGet.targets
                // https://github.com/NuGet/Home/issues/13828
                List<RestoreSummary> restoreSummaries = await BuildTasksUtility.RestoreAsync(
                    dependencyGraphSpec: dependencyGraphSpec,
                    interactive,
                    recursive: IsOptionTrue(nameof(RestoreTaskEx.Recursive), options),
                    noCache: IsOptionTrue(nameof(RestoreTaskEx.NoCache), options) || IsOptionTrue(nameof(RestoreTaskEx.NoHttpCache), options),
                    ignoreFailedSources: IsOptionTrue(nameof(RestoreTaskEx.IgnoreFailedSources), options),
                    disableParallel: IsOptionTrue(nameof(RestoreTaskEx.DisableParallel), options),
                    force: IsOptionTrue(nameof(RestoreTaskEx.Force), options),
                    forceEvaluate: IsOptionTrue(nameof(RestoreTaskEx.ForceEvaluate), options),
                    hideWarningsAndErrors: IsOptionTrue(nameof(RestoreTaskEx.HideWarningsAndErrors), options),
                    restorePC: restorePackagesConfig,
                    cleanupAssetsForUnsupportedProjects: IsOptionTrue(nameof(RestoreTaskEx.CleanupAssetsForUnsupportedProjects), options),
                    log: MSBuildLogger,
                cancellationToken: CancellationToken.None);
                bool result = restoreSummaries.All(rs => rs.Success);

                LogFilesToEmbedInBinlog(dependencyGraphSpec, options);

                return result;
            }
            catch (Exception e)
            {
                LogErrorFromException(e);

                return false;
            }
        }

        /// <summary>
        /// Generates a dependency graph spec for the given properties.
        /// </summary>
        /// <param name="entryProjectFilePath">The main project to generate that graph for.  This can be a project for a Visual Studio© Solution File.</param>
        /// <param name="globalProperties">The global properties to use when evaluation MSBuild projects.</param>
        /// <param name="options">The set of options to use to generate the graph, including the restore graph output path.</param>
        /// <returns><code>true</code> if the dependency graph spec was generated and written, otherwise <code>false</code>.</returns>
        public bool WriteDependencyGraphSpec(string entryProjectFilePath, IDictionary<string, string> globalProperties, IReadOnlyDictionary<string, string> options)
        {
            bool interactive = IsOptionTrue(nameof(RestoreTaskEx.Interactive), options);

            string binaryLoggerParameters = GetBinaryLoggerParameters(_environment, options);

            var dependencyGraphSpec = GetDependencyGraphSpec(entryProjectFilePath, globalProperties, interactive, binaryLoggerParameters);

            try
            {
                if (dependencyGraphSpec == null)
                {
                    LoggingQueue.TaskLoggingHelper.LogError(Strings.Error_DgSpecGenerationFailed);
                    return false;
                }

                if (options.TryGetValue("RestoreGraphOutputPath", out var path))
                {
                    dependencyGraphSpec.Save(path);
                    return true;
                }
                else
                {
                    LoggingQueue.TaskLoggingHelper.LogError(Strings.Error_MissingRestoreGraphOutputPath);
                }
            }
            catch (Exception e)
            {
                LogErrorFromException(e);
            }
            return false;
        }

        /// <summary>
        /// Gets parameters for the MSBuild binary logger.
        /// </summary>
        /// <param name="environment">An <see cref="IEnvironmentVariableReader" /> to use when reading environment variables.</param>
        /// <param name="options">The <see cref="IReadOnlyCollection{TKey, TValue}" /> containing user supplied options.</param>
        /// <returns>A <see cref="string" /> containing the parameters for the MSBuild binary logger if specified, otherwise <see langword="null" />.</returns>
        internal static string GetBinaryLoggerParameters(IEnvironmentVariableReader environment, IReadOnlyDictionary<string, string> options)
        {
            string binaryLoggerParameters = environment.GetEnvironmentVariable(BinaryLoggerParameterEnvironmentVariable);

            if (!string.IsNullOrEmpty(binaryLoggerParameters))
            {
                return binaryLoggerParameters;
            }

            // Return null if the binary logger is not enabled
            if (!IsOptionTrue(nameof(RestoreTaskEx.EnableBinaryLogger), options))
            {
                return null;
            }

            if (options.TryGetValue(nameof(RestoreTaskEx.BinaryLoggerParameters), out binaryLoggerParameters) && !string.IsNullOrWhiteSpace(binaryLoggerParameters))
            {
                // User supplied the parameters
                return binaryLoggerParameters;
            }

            // Default parameters
            return binaryLoggerParameters = "LogFile=nuget.binlog";
        }

        /// <summary>
        /// Determines of the specified option is <code>true</code>.
        /// </summary>
        /// <param name="name">The name of the option.</param>
        /// <param name="options">A <see cref="Dictionary{String,String}" />containing options.</param>
        /// <returns><code>true</code> if the specified option is true, otherwise <code>false</code>.</returns>
        internal static bool IsOptionTrue(string name, IReadOnlyDictionary<string, string> options)
        {
            return options.TryGetValue(name, out string value) && StringComparer.OrdinalIgnoreCase.Equals(value, bool.TrueString);
        }

        /// <summary>
        /// Gets the list of project graph entry points.  If the entry project is a solution, this method returns all of the projects it contains.
        /// </summary>
        /// <param name="entryProjectPath">The full path to the main project or solution file.</param>
        /// <param name="globalProperties">An <see cref="IDictionary{String,String}" /> representing the global properties for the project.</param>
        /// <returns></returns>
        private List<ProjectGraphEntryPoint> GetProjectGraphEntryPoints(string entryProjectPath, IDictionary<string, string> globalProperties)
        {
            // If the project's extension is .sln, parse it as a Visual Studio solution and return the projects it contains
            if (string.Equals(Path.GetExtension(entryProjectPath), ".sln", StringComparison.OrdinalIgnoreCase))
            {
                var solutionFile = SolutionFile.Parse(entryProjectPath);

                IEnumerable<ProjectInSolution> projectsKnownToMSBuild = solutionFile.ProjectsInOrder.Where(i => i.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat);
                IEnumerable<ProjectInSolution> projectsNotKnownToMSBuild = solutionFile.ProjectsInOrder.Except(projectsKnownToMSBuild);

                if (projectsNotKnownToMSBuild.Any())
                {
                    IList<string> projects = projectsNotKnownToMSBuild.Select(project => project.ProjectName).ToList();

                    MSBuildLogger.LogInformation(string.Format(CultureInfo.CurrentCulture,
                        Strings.Log_ProjectsInSolutionNotKnowntoMSBuild,
                        projects.Count, string.Join(",", projects)));
                }

                return projectsKnownToMSBuild.Select(i => new ProjectGraphEntryPoint(i.AbsolutePath, globalProperties)).ToList();
            }

            // Return just the main project in a list if its not a solution file
            return new List<ProjectGraphEntryPoint>
            {
                new ProjectGraphEntryPoint(entryProjectPath, globalProperties),
            };
        }

        /// <summary>
        /// Gets a <see cref="DependencyGraphSpec" /> for the specified project.
        /// </summary>
        /// <param name="entryProjectPath">The full path to a project or Visual Studio Solution File.</param>
        /// <param name="globalProperties">An <see cref="IDictionary{String,String}" /> containing the global properties to use when evaluation MSBuild projects.</param>
        /// <param name="interactive"><see langword="true" /> if the build is allowed to interact with the user, otherwise <see langword="false" />.</param>
        /// <returns>A <see cref="DependencyGraphSpec" /> for the specified project if they could be loaded, otherwise <code>null</code>.</returns>
        private DependencyGraphSpec GetDependencyGraphSpec(string entryProjectPath, IDictionary<string, string> globalProperties, bool interactive, string binaryLoggerParameters)
        {
            try
            {
                MSBuildLogger.LogMinimal(Strings.DeterminingProjectsToRestore);

                var entryProjects = GetProjectGraphEntryPoints(entryProjectPath, globalProperties);

                // Load the projects via MSBuild and create an array of them since Parallel.ForEach is optimized for arrays
                var projects = LoadProjects(entryProjects, interactive, binaryLoggerParameters)?.ToArray();

                // If no projects were loaded, return an empty DependencyGraphSpec
                if (projects == null || projects.Length == 0)
                {
                    return new DependencyGraphSpec();
                }

                var sw = Stopwatch.StartNew();

                var dependencyGraphSpec = new DependencyGraphSpec(isReadOnly: true);

                // Unique names created by the MSBuild restore target are project paths, these
                // can be different on case-insensitive file systems for the same project file.
                // To workaround this unique names should be compared based on the OS.
                var uniqueNameComparer = PathUtility.GetStringComparerBasedOnOS();
                var projectPathLookup = new ConcurrentDictionary<string, string>(uniqueNameComparer);

                try
                {
                    // Get the PackageSpecs in parallel because creating each one is relatively expensive so parallelism speeds things up
                    Parallel.ForEach(projects, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, project =>
                    {
                        var settings = RestoreSettingsUtils.ReadSettings(
                            project.Value.OuterBuild.GetProperty("RestoreSolutionDirectory"),
                            project.Value.OuterBuild.GetProperty("RestoreRootConfigDirectory") ?? project.Value.Directory,
                            UriUtility.GetAbsolutePath(project.Value.Directory, project.Value.OuterBuild.GetProperty("RestoreConfigFile")),
                            MachineWideSettingsLazy,
                            _settingsLoadContext);

                        var packageSpec = PackageSpecFactory.GetPackageSpec(project.Value, settings);

                        if (packageSpec != null)
                        {
                            // Keep track of all project path casings
                            var uniqueName = packageSpec.RestoreMetadata.ProjectUniqueName;
                            if (uniqueName != null && !projectPathLookup.ContainsKey(uniqueName))
                            {
                                projectPathLookup.TryAdd(uniqueName, uniqueName);
                            }

                            var projectPath = packageSpec.RestoreMetadata.ProjectPath;
                            if (projectPath != null && !projectPathLookup.ContainsKey(projectPath))
                            {
                                projectPathLookup.TryAdd(projectPath, projectPath);
                            }

                            lock (dependencyGraphSpec)
                            {
                                dependencyGraphSpec.AddProject(packageSpec);
                            }
                        }
                    });
                }
                catch (Exception e)
                {
                    LogErrorFromException(e);

                    return null;
                }

                // Fix project reference casings to match the original project on case insensitive file systems.
                MSBuildRestoreUtility.NormalizePathCasings(projectPathLookup, dependencyGraphSpec);

                // Remove references to projects that could not be read by restore.
                MSBuildRestoreUtility.RemoveMissingProjects(dependencyGraphSpec);

                // Add all entry projects if they support restore.  In most cases this is just a single project but if the entry
                // project is a solution, then all projects in the solution are added (if they support restore)
                foreach (var entryPoint in entryProjects)
                {
                    PackageSpec project = dependencyGraphSpec.GetProjectSpec(entryPoint.ProjectFile);

                    if (project != null && BuildTasksUtility.DoesProjectSupportRestore(project))
                    {
                        dependencyGraphSpec.AddRestore(entryPoint.ProjectFile);
                    }
                }

                sw.Stop();

                MSBuildLogger.LogDebug(string.Format(CultureInfo.CurrentCulture, Strings.CreatedDependencyGraphSpec, sw.ElapsedMilliseconds));

                return dependencyGraphSpec;
            }
            catch (Exception e)
            {
                LogErrorFromException(e);
            }

            return null;
        }

        /// <summary>
        /// Recursively loads and evaluates MSBuild projects.
        /// </summary>
        /// <param name="entryProjects">An <see cref="IEnumerable{ProjectGraphEntryPoint}" /> containing the entry projects to load.</param>
        /// <param name="interactive"><see langword="true" /> if the build is allowed to interact with the user, otherwise <see langword="false" />.</param>
        /// <param name="binaryLoggerParameters">Optional parameters to use for the MSBuild binary log.</param>
        /// <returns>An <see cref="ICollection{ProjectWithInnerNodes}" /> object containing projects and their inner nodes if they are targeting multiple frameworks.</returns>
        private ConcurrentDictionary<string, RestoreProjectAdapter> LoadProjects(IEnumerable<ProjectGraphEntryPoint> entryProjects, bool interactive, string binaryLoggerParameters)
        {
            try
            {
                var loggers = new List<Microsoft.Build.Framework.ILogger>
                {
                    LoggingQueue
                };

                bool logTaskInputs = false;

                // Attach the binary logger if parameters were specified
                if (!string.IsNullOrWhiteSpace(binaryLoggerParameters))
                {
                    loggers.Add(new BinaryLogger
                    {
                        Parameters = Uri.UnescapeDataString(binaryLoggerParameters)
                    });

                    // Log task inputs when the binary logger is attached
                    logTaskInputs = true;
                }

                var projects = new ConcurrentDictionary<string, RestoreProjectAdapter>(PathUtility.GetStringComparerBasedOnOS());

                using var projectCollection = new ProjectCollection(
                    globalProperties: null,
                    // Attach a logger for evaluation only if the Debug option is set
                    loggers: loggers,
                    remoteLoggers: null,
                    toolsetDefinitionLocations: ToolsetDefinitionLocations.Default,
                    // Having more than 1 node spins up multiple msbuild.exe instances to run builds in parallel
                    // However, these targets complete so quickly that the added overhead makes it take longer
                    maxNodeCount: 1,
                    onlyLogCriticalEvents: false,
                    // Loading projects as readonly makes parsing a little faster since comments and whitespace can be ignored
                    loadProjectsReadOnly: true);

                Stopwatch sw = Stopwatch.StartNew();

                EvaluationContext evaluationContext = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared);

                // Create a ProjectGraph object and pass a factory method which creates a ProjectInstance
                ProjectGraph projectGraph = new ProjectGraph(entryProjects, projectCollection, (path, properties, collection) =>
                {
                    var projectOptions = new ProjectOptions
                    {
                        EvaluationContext = evaluationContext,
                        GlobalProperties = properties,
                        Interactive = interactive,
                        // Ignore bad imports to maximize the chances of being able to load the project and restore
                        LoadSettings = ProjectLoadSettings.IgnoreEmptyImports | ProjectLoadSettings.IgnoreInvalidImports | ProjectLoadSettings.IgnoreMissingImports | ProjectLoadSettings.DoNotEvaluateElementsWithFalseCondition,
                        ProjectCollection = collection
                    };

                    return ProjectInstance.FromFile(path, projectOptions);
                });

                int buildCount = 0;
                int failedBuildSubmissionCount = 0;

                var buildParameters = new BuildParameters(projectCollection)
                {
                    // Use the same loggers as the project collection
                    Loggers = projectCollection.Loggers,
                    LogTaskInputs = logTaskInputs
                };

                try
                {
                    // BeginBuild starts a queue which accepts build requests and applies the build parameters to all of them
                    BuildManager.DefaultBuildManager.BeginBuild(buildParameters);

                    // Loop through each project and run the targets.  There is no need for this to run in parallel since there is only
                    // one node in the process to run builds.
                    foreach (ProjectGraphNode projectGraphItem in projectGraph.ProjectNodes)
                    {
                        ProjectInstance projectInstance = projectGraphItem.ProjectInstance;

                        if (!projectInstance.Targets.ContainsKey("_IsProjectRestoreSupported") || projectInstance.GlobalProperties == null || projectInstance.GlobalProperties.TryGetValue("TargetFramework", out string targetFramework) && string.IsNullOrWhiteSpace(targetFramework))
                        {
                            // In rare cases, users can set an empty TargetFramework value in a project-to-project reference.  Static Graph will respect that
                            // but NuGet does not need to do anything with that instance of the project since the actual project is still loaded correctly
                            // with its actual TargetFramework.
                            var message = MSBuildRestoreUtility.GetMessageForUnsupportedProject(projectInstance.FullPath);
                            MSBuildLogger.Log(message);
                            continue;
                        }

                        // If the project supports restore, queue up a build of the targets needed for restore
                        BuildSubmission buildSubmission = BuildManager.DefaultBuildManager.PendBuildRequest(
                            new BuildRequestData(
                                projectInstance,
                                TargetsToBuild,
                                hostServices: null,
                                // Suppresses an error that a target does not exist because it may or may not contain the targets that we're running
                                BuildRequestDataFlags.SkipNonexistentTargets));

                        buildSubmission.ExecuteAsync((submission) =>
                        {
                            BuildResult result = submission.BuildResult;
                            if (result.OverallResult == BuildResultCode.Failure)
                            {
                                failedBuildSubmissionCount++;
                            }

                            buildCount++;

                            projects.AddOrUpdate(
                                projectInstance.FullPath,
                                key =>
                                {
                                    var adapter = new RestoreProjectAdapter(projectInstance.FullPath);
                                    adapter.AddTargetFramework(targetFramework, new TargetFrameworkAdapter(projectInstance));
                                    return adapter;
                                },
                                (_, item) =>
                                {
                                    item.AddTargetFramework(targetFramework, new TargetFrameworkAdapter(projectInstance));
                                    return item;
                                });
                        }, context: null);
                    }
                }
                finally
                {
                    // EndBuild blocks until all builds are complete
                    BuildManager.DefaultBuildManager.EndBuild();
                }

                sw.Stop();

                MSBuildLogger.LogInformation(string.Format(CultureInfo.CurrentCulture, Strings.ProjectEvaluationSummary, projectGraph.ProjectNodes.Count, sw.ElapsedMilliseconds, buildCount, failedBuildSubmissionCount));

                if (failedBuildSubmissionCount != 0)
                {
                    // Return null if any builds failed, they will have logged errors
                    return null;
                }

                // Just return the projects not the whole dictionary as it was just used to group the projects together

                return projects;
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception e)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                LogErrorFromException(e);

                return null;
            }
        }

        /// <summary>
        /// Logs an error from the specified exception.
        /// </summary>
        /// <param name="exception">The <see cref="Exception" /> with details to be logged.</param>
        private void LogErrorFromException(Exception exception)
        {
            switch (exception)
            {
                case AggregateException aggregateException:
                    foreach (Exception innerException in aggregateException.Flatten().InnerExceptions)
                    {
                        LogErrorFromException(innerException);
                    }
                    break;

                case InvalidProjectFileException invalidProjectFileException:
                    // Special case the InvalidProjectFileException since it has extra information about what project file couldn't be loaded
                    LoggingQueue.TaskLoggingHelper.LogError(
                        invalidProjectFileException.ErrorSubcategory,
                        invalidProjectFileException.ErrorCode,
                        invalidProjectFileException.HelpKeyword,
                        invalidProjectFileException.ProjectFile,
                        invalidProjectFileException.LineNumber,
                        invalidProjectFileException.ColumnNumber,
                        invalidProjectFileException.EndLineNumber,
                        invalidProjectFileException.EndColumnNumber,
                        invalidProjectFileException.Message);
                    break;

                default:
                    LoggingQueue.TaskLoggingHelper.LogErrorFromException(
                        exception,
                        showStackTrace: true);
                    break;
            }
        }

        /// <summary>
        /// Logs the list of files to embed in the MSBuild binary log.
        /// </summary>
        /// <param name="dependencyGraphSpec"></param>
        private void LogFilesToEmbedInBinlog(DependencyGraphSpec dependencyGraphSpec, IReadOnlyDictionary<string, string> options)
        {
            // Determines what the user wants embedded in the binary log where 0 or false disables embedding anything, 2 embeds everything, and 1 or true embeds just the assets file, g.props, and g.targets.
            options.TryGetValue(nameof(RestoreTaskEx.EmbedFilesInBinlog), out string embedFilesInBinlog);

            int embedInBinlogSelection = BuildTasksUtility.GetFilesToEmbedInBinlogValue(embedFilesInBinlog);

            if (embedInBinlogSelection == 0)
            {
                return;
            }

            IReadOnlyList<PackageSpec> projects = dependencyGraphSpec.Projects;

            foreach (PackageSpec project in projects)
            {
                if (project.RestoreMetadata.ProjectStyle == ProjectStyle.PackageReference)
                {
                    LoggingQueue.Enqueue(new ConsoleOutLogEmbedInBinlog(Path.Combine(project.RestoreMetadata.OutputPath, LockFileFormat.AssetsFileName)));
                    LoggingQueue.Enqueue(new ConsoleOutLogEmbedInBinlog(BuildAssetsUtils.GetMSBuildFilePathForPackageReferenceStyleProject(project, BuildAssetsUtils.PropsExtension)));
                    LoggingQueue.Enqueue(new ConsoleOutLogEmbedInBinlog(BuildAssetsUtils.GetMSBuildFilePathForPackageReferenceStyleProject(project, BuildAssetsUtils.TargetsExtension)));

                    // Only include the dgspec if the user wants everything embedded in the binlog.
                    if (embedInBinlogSelection == 2)
                    {
                        LoggingQueue.Enqueue(new ConsoleOutLogEmbedInBinlog(Path.Combine(project.RestoreMetadata.OutputPath, DependencyGraphSpec.GetDGSpecFileName(Path.GetFileName(project.RestoreMetadata.ProjectPath)))));
                    }
                }
                else if (project.RestoreMetadata.ProjectStyle == ProjectStyle.PackagesConfig)
                {
                    string packagesConfigPath = BuildTasksUtility.GetPackagesConfigFilePath(project.RestoreMetadata.ProjectPath);

                    if (packagesConfigPath != null)
                    {
                        LoggingQueue.Enqueue(new ConsoleOutLogEmbedInBinlog(packagesConfigPath));
                    }
                }
            }
        }
    }
}

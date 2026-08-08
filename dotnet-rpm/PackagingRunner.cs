using Microsoft.Build.Locator;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConsoleLogger = Microsoft.Build.Logging.ConsoleLogger;
using IMSBuildLogger = Microsoft.Build.Framework.ILogger;
using LoggerVerbosity = Microsoft.Build.Framework.LoggerVerbosity;
using MSBuild = Microsoft.Build.Evaluation;

namespace Dotnet.Packaging
{
    public class PackagingRunner
    {
        private readonly string outputName;
        private readonly string msbuildTarget;
        private readonly string commandName;

        public PackagingRunner(string outputName, string msbuildTarget, string commandName)
        {
            var instance = MSBuildLocator.RegisterDefaults();

            // Workaround for https://github.com/microsoft/MSBuildLocator/issues/86
            AssemblyLoadContext.Default.Resolving += (assemblyLoadContext, assemblyName) =>
            {
                var path = Path.Combine(instance.MSBuildPath, assemblyName.Name + ".dll");
                if (File.Exists(path))
                {
                    return assemblyLoadContext.LoadFromAssemblyPath(path);
                }

                return null;
            };

            this.outputName = outputName;
            this.msbuildTarget = msbuildTarget;
            this.commandName = commandName;
        }

        public int Run(string[] args)
        {
            var rootCommand = new RootCommand();

            var runtimeOption = new Option<string>("--runtime", "-r")
            {
                Description = $"Target runtime of the {outputName}. The target runtime has to be specified in the project file.",
                Arity = ArgumentArity.ExactlyOne,
            };

            var frameworkOption = new Option<string>("--framework", "-f")
            {
                Description = $"Target framework of the {outputName}. The target framework has to be specified in the project file.",
                Arity = ArgumentArity.ExactlyOne,
            };

            var configurationOption = new Option<string>("--configuration", "-c")
            {
                Description = $"Target configuration of the {outputName}. The default for most projects is 'Debug'.",
                Arity = ArgumentArity.ExactlyOne,
            };

            var outputOption = new Option<string>("--output", "-o")
            {
                Description = "The output directory to place built packages in. The default is the output directory of your project.",
                Arity = ArgumentArity.ExactlyOne,
            };

            var versionSuffixOption = new Option<string>("--version-suffix")
            {
                Description = "Defines the value for the $(VersionSuffix) property in the project.",
                Arity = ArgumentArity.ExactlyOne,
            };

            var noRestoreOption = new Option<bool>("--no-restore")
            {
                Description = "Do not restore the project before building.",
            };

            var verboseOption = new Option<bool>("--verbose", "-v")
            {
                Description = "Enable verbose output.",
            };

            var projectArgument = new Argument<string>("project")
            {
                Description = "The project file to operate on. If a file is not specified, the command will search the current directory for one.",
                Arity = ArgumentArity.ZeroOrOne,
            };

            rootCommand.Options.Add(runtimeOption);
            rootCommand.Options.Add(frameworkOption);
            rootCommand.Options.Add(configurationOption);
            rootCommand.Options.Add(outputOption);
            rootCommand.Options.Add(versionSuffixOption);
            rootCommand.Options.Add(noRestoreOption);
            rootCommand.Options.Add(verboseOption);
            rootCommand.Arguments.Add(projectArgument);

            var installCommand = new Command("install");
            installCommand.SetAction(parseResult =>
            {
                Console.WriteLine($"dotnet {this.commandName} ({ThisAssembly.AssemblyInformationalVersion})");

                // Create/update the Directory.Build.props file in the directory of the version.json file to add the Packaging.Targets package.
                string directoryBuildPropsPath = Path.Combine(Environment.CurrentDirectory, "Directory.Build.props");
                MSBuild.Project propsFile;
                if (File.Exists(directoryBuildPropsPath))
                {
                    propsFile = new MSBuild.Project(directoryBuildPropsPath);
                }
                else
                {
                    propsFile = new MSBuild.Project();
                }

                const string PackageReferenceItemType = "PackageReference";
                const string PackageId = "Packaging.Targets";
                if (!propsFile.GetItemsByEvaluatedInclude(PackageId).Any(i => i.ItemType == PackageReferenceItemType && i.EvaluatedInclude == PackageId))
                {
                    // Using the -* suffix will allow us to match both released and prereleased versions, in the absence
                    // of https://github.com/AArnott/Nerdbank.GitVersioning/issues/409
                    string packageVersion = $"{new Version(ThisAssembly.AssemblyFileVersion).ToString(3)}-*";
                    propsFile.AddItem(
                        PackageReferenceItemType,
                        PackageId,
                        new Dictionary<string, string>
                        {
                        { "Version", packageVersion },
                        { "PrivateAssets", "all" },
                        });

                    propsFile.Save(directoryBuildPropsPath);
                }

                Console.WriteLine($"Successfully installed dotnet {this.commandName}. Now run 'dotnet {this.commandName}' to package your");
                Console.WriteLine($"application as a {this.outputName}");

                return 0;
            });
            rootCommand.Subcommands.Add(installCommand);

            rootCommand.SetAction(parseResult =>
            {
                string runtime = parseResult.GetValue(runtimeOption);
                string framework = parseResult.GetValue(frameworkOption);
                string configuration = parseResult.GetValue(configurationOption);
                string output = parseResult.GetValue(outputOption);
                string versionSuffix = parseResult.GetValue(versionSuffixOption);
                bool noRestore = parseResult.GetValue(noRestoreOption);
                bool verbose = parseResult.GetValue(verboseOption);
                string project = parseResult.GetValue(projectArgument);

                Console.WriteLine($"dotnet {this.commandName} ({ThisAssembly.AssemblyInformationalVersion})");

                if (verbose)
                {
                    Console.WriteLine($"{nameof(runtime)}: {runtime}");
                    Console.WriteLine($"{nameof(framework)}: {framework}");
                    Console.WriteLine($"{nameof(configuration)}: {configuration}");
                    Console.WriteLine($"{nameof(output)}: {output}");
                    Console.WriteLine($"{nameof(versionSuffix)}: {versionSuffix}");
                    Console.WriteLine($"{nameof(noRestore)}: {noRestore}");
                    Console.WriteLine($"{nameof(verbose)}: {verbose}");
                }

                if (!TryGetProjectFilePath(project, out string projectFilePath))
                {
                    return -1;
                }

                if (verbose)
                {
                    Console.WriteLine($"User specified project '{project}', using '{projectFilePath}'.");
                }

                if (!noRestore)
                {
                    if (!this.IsPackagingTargetsInstalled(verbose, projectFilePath, framework))
                    {
                        return -1;
                    }
                }

                StringBuilder msbuildArguments = new StringBuilder();
                msbuildArguments.Append($"msbuild /t:{msbuildTarget} ");

                if (!string.IsNullOrWhiteSpace(runtime))
                {
                    msbuildArguments.Append($"/p:RuntimeIdentifier={runtime} ");
                }

                if (!string.IsNullOrWhiteSpace(framework))
                {
                    msbuildArguments.Append($"/p:TargetFramework={framework} ");
                }

                if (!string.IsNullOrWhiteSpace(configuration))
                {
                    msbuildArguments.Append($"/p:Configuration={configuration} ");
                }

                if (!string.IsNullOrWhiteSpace(versionSuffix))
                {
                    msbuildArguments.Append($"/p:VersionSuffix={versionSuffix} ");
                }

                if (!string.IsNullOrWhiteSpace(output))
                {
                    msbuildArguments.Append($"/p:PackageDir={output} ");
                }

                msbuildArguments.Append($"{projectFilePath} ");

                return RunDotnet(msbuildArguments);
            });

            return rootCommand.Parse(args).Invoke();
        }

        public int RunDotnet(StringBuilder msbuildArguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = msbuildArguments.ToString()
            };

            var process = new Process
            {
                StartInfo = psi,

            };

            process.Start();
            process.WaitForExit();

            return process.ExitCode;
        }

        public bool IsPackagingTargetsInstalled(bool verbose, string projectFilePath, string framework)
        {
            var loggers = new IMSBuildLogger[] { new ConsoleLogger(verbose ? LoggerVerbosity.Detailed : LoggerVerbosity.Quiet) };
            var project = new MSBuild.Project(projectFilePath);

            if (!string.IsNullOrWhiteSpace(framework))
            {
                project.SetProperty("TargetFramework", framework);
            }

            if (!project.Build("Restore", loggers))
            {
                Console.Error.WriteLine($"Failed to restore '{Path.GetFileName(projectFilePath)}'. Please run dotnet restore, and try again.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(project.GetPropertyValue("TargetFramework")))
            {
                Console.Error.WriteLine($"The project '{Path.GetFileName(projectFilePath)}' does not specify a default target framework. Please specify the -f {{framework}} option, and try again.");
                return false;
            }

            var projectAssetsPath = project.GetPropertyValue("ProjectAssetsFile");

            if (string.IsNullOrWhiteSpace(projectAssetsPath))
            {
                Console.Error.WriteLine($"Failed to read the ProjectAssetsFile property for '{Path.GetFileName(projectFilePath)}'. Please run dotnet restore, and try again.");
                return false;
            }

            // NuGet has a LockFileUtilities.GetLockFile API which provides direct access to this file format,
            // but loading NuGet in the same process as MSBuild creates dependency conflicts.
            var lockFileContent = File.ReadAllBytes(projectAssetsPath);

            LockFile lockFile = JsonSerializer.Deserialize<LockFile>(lockFileContent);

            if (!lockFile.Libraries.Any(l => l.Key.StartsWith("Packaging.Targets/")))
            {
                Console.Error.WriteLine($"The project '{Path.GetFileName(projectFilePath)}' doesn't have a PackageReference to Packaging.Targets.");
                Console.Error.WriteLine($"Please run 'dotnet {this.commandName} install', and try again.");
                return false;
            }

            return true;
        }

        private bool TryGetProjectFilePath(string project, out string projectFilePath)
        {
            if (!string.IsNullOrWhiteSpace(project))
            {
                projectFilePath = project;

                if (!File.Exists(project))
                {
                    Console.Error.WriteLine($"Could not find the project file '{project}'.");
                    return false;
                }

                return true;
            }

            projectFilePath = Directory.GetFiles(Environment.CurrentDirectory, "*.*proj").SingleOrDefault();

            if (projectFilePath == null)
            {
                Console.Error.WriteLine($"Failed to find a .*proj file in '{Environment.CurrentDirectory}'. dotnet {this.commandName} only works if");
                Console.Error.WriteLine($"you have exactly one .*proj file in your directory. For advanced scenarios, please use 'dotnet msbuild /t:{this.msbuildTarget}'");
                return false;
            }

            return true;
        }

        class LockFile
        {
            [JsonPropertyName("libraries")]
            public Dictionary<string, object> Libraries { get; set; }
        }
    }
}
using System;
using System.Linq;
using Nuke.Common;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.ReportGenerator;
using Nuke.Common.Tools.Xunit;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using Nuke.Common.CI;
using Nuke.Components;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.ReportGenerator.ReportGeneratorTasks;
using static Nuke.Common.Tools.Xunit.XunitTasks;
using static Serilog.Log;
using Serilog;

[UnsetVisualStudioEnvironmentVariables]
[DotNetVerbosityMapping]
class Build : NukeBuild
{
    /* Support plugins are available for:
       - JetBrains ReSharper        https://nuke.build/resharper
       - JetBrains Rider            https://nuke.build/rider
       - Microsoft VisualStudio     https://nuke.build/visualstudio
       - Microsoft VSCode           https://nuke.build/vscode
    */

    public static int Main() => Execute<Build>(x => x.Installers);

    [Parameter("The solution configuration to build. Default is 'Debug' (local) or 'CI' (server).")]
    readonly Configuration Configuration = Configuration.Debug;

    [Parameter("Use this parameter if you encounter build problems in any way, " +
        "to generate a .binlog file which holds some useful information.")]
    readonly bool? GenerateBinLog;
 
    [Solution(GenerateProjects = false)]
    readonly Solution Solution = null!;
    


    [Required]
    [GitRepository]
    readonly GitRepository GitRepository = null!;

    AbsolutePath ArtifactsDirectory => RootDirectory / "Artifacts";

    AbsolutePath AttachmentsDirectory => ArtifactsDirectory / "Attachments";

    AbsolutePath BuildLogsDirectory => AttachmentsDirectory / "build_logs";

    AbsolutePath CoverageDirectory => AttachmentsDirectory / "Coverage";

    AbsolutePath TestResultsDirectory => AttachmentsDirectory / "TestResults";

    string SemVer = null!;

    Target Clean => _ => _
        .Executes(() =>
        {
            ArtifactsDirectory.CreateOrCleanDirectory();
            TestResultsDirectory.CreateOrCleanDirectory();
        });

    Target None => _ => _
        .Executes(() =>
        {

        });

    
    Target Restore => _ => _
        .DependsOn(Clean)
        .Executes(() =>
        {

            DotNetToolRestore();
			
			DotNet("wix extension add -g WixToolset.UI.wixext/6.0.2");
            
            //DotNet("paket restore");
            
            DotNetRestore(s => s
                .SetProjectFile(Solution)
                .EnableNoCache()
                .SetConfigFile(RootDirectory / "nuget.config")
                );
        });
   
    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            ReportSummary(s => s
                .WhenNotNull(SemVer, (summary, semVer) => summary
                    .AddPair("Version", semVer)));

            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .When(_ => GenerateBinLog == true, c => c
                    .SetBinaryLog(BuildLogsDirectory / $"ClinicManager.build.binlog")
                )
                .EnableNoLogo());
        });
        
        
    Target Tests => _ => _
        .DependsOn(UnitTests)
        .DependsOn(E2ETests);

    Target CodeCoverage => _ => _
        .DependsOn(Tests)
        .Executes(() =>
        {
        
            ReportGenerator(s => s
               .SetProcessToolPath(NuGetToolPathResolver.GetPackageExecutable("ReportGenerator", "ReportGenerator.dll",
                    framework: "net10.0"))
                .SetTargetDirectory(TestResultsDirectory / "coverage_reports")
                .AddReports(CoverageDirectory / "**/*.cobertura.xml")
                .AddReportTypes(
                    ReportTypes.lcov,ReportTypes.MHtml,
                    ReportTypes.HtmlInline_AzurePipelines_Dark)
                .AddFileFilters("-*.g.cs")
                .AddFileFilters("-*.nuget*")
                 .SetAssemblyFilters("+*ClinicMgr*;+*ClinicManager*"));

		   string link = TestResultsDirectory / "coverage_reports" / "index.html";
            Information($"Code coverage report: \x1b]8;;file://{link.Replace('\\', '/')}\x1b\\{link}\x1b]8;;\x1b\\");
      });

    

    Target UnitTests => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            var testProjectNames = new[] { "ClinicManager.Win.Tests", "ClinicManager.Core.Tests" };
            
            var unitTestProjects = testProjectNames.Select(x => Solution.GetAllProjects(x).First());
           
		var testCombinations =
                from project in unitTestProjects
                let frameworks = project.GetTargetFrameworks()
                from framework in frameworks
                select new { project, framework };

            
            DotNetRun(s => s
                .SetConfiguration(Configuration.Debug)
                .SetProcessEnvironmentVariable("DOTNET_CLI_UI_LANGUAGE", "en-US")
                .EnableNoBuild()
                .CombineWith(
                    testCombinations,
                    (settings, v) => settings
                        .SetProjectFile(v.project)
                        .SetFramework(v.framework)
                        .SetProcessAdditionalArguments(
                            "--",
							"--coverage",
							"--coverage-output-format cobertura",
							$"--coverage-output {CoverageDirectory / $"{v.project.Name}_{v.framework}.cobertura.xml"}",
                            $"--results-directory {TestResultsDirectory}"
                         )
                    )
                );
            
        });

    Target E2ETests => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            var testProjectNames = new[] { "ClinicManager.E2E.Tests" };

            var unitTestProjects = testProjectNames.Select(x => Solution.GetAllProjects(x).First());
         
            var testCombinations =
                from project in unitTestProjects
                let frameworks = project.GetTargetFrameworks()
                from framework in frameworks
                select new { project, framework };

               // E2ETestProjects.ForEach(x=>Information(x.Name));

            DotNetRun(s => s
                .SetConfiguration(Configuration.Debug)
                .SetProcessEnvironmentVariable("DOTNET_CLI_UI_LANGUAGE", "en-US")
                .EnableNoBuild()
                .CombineWith(
                    testCombinations,
                    (settings, v) => settings
                        .SetProjectFile(v.project)
                        .SetFramework(v.framework)
                        .SetProperty("RunWorkingDirectory", ArtifactsDirectory / "bin" / "ClinicManager.Win" / Configuration )
						.SetProcessAdditionalArguments(
                            "--",
							"--coverage",
							"--coverage-output-format cobertura",
							$"--coverage-output {CoverageDirectory / $"{v.project.Name}_{v.framework}.cobertura.xml"}",
                            $"--results-directory {TestResultsDirectory}"
                         )
                    )
                );
        });

		Target PublishDesktop => _ => _
            .DependsOn(Restore)
            .Executes(() =>
            {
                var winProjectName = "ClinicManager.Win";
            
                var winProject = Solution.GetAllProjects(winProjectName).First();
                DotNetPublish(s => s
                    .SetProject(winProject)
                    .SetConfiguration(Configuration)
                    .SetPublishProfile("FolderProfile") // use your publish profile
                    .SetOutput(OutputDirectory / "publish"));
			
            
        });
		
    Target Installers => _ => _
        .DependsOn(PublishDesktop)
        .Executes(() =>
        {
            var setupProjectName = "Installer";
            
            var setupProject = Solution.GetAllProjects(setupProjectName).First();
         
            DotNetBuild(s => s
                .SetProjectFile(setupProject)
                .SetConfiguration(Configuration)
                .When(_ => GenerateBinLog == true, c => c
                    .SetBinaryLog(BuildLogsDirectory / $"ClinicManagerSetup.build.binlog")
                )
				.SetProperty("NukeBuild", "True")
                .EnableNoLogo());
        });

		Target Full => _ => _
        .DependsOn(Compile)
		.DependsOn(Installers)
	    .DependsOn(Tests)
        .Executes(() =>
        {
            
            
        });
		
    static bool IsDocumentation(string x) =>
        x.StartsWith("docs") ||
        x.StartsWith("CONTRIBUTING.md") ||
        x.StartsWith("cSpell.json") ||
        x.StartsWith("LICENSE") ||
        x.StartsWith("package.json") ||
        x.StartsWith("package-lock.json") ||
        x.StartsWith("README.md");
}

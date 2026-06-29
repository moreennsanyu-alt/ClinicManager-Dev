using System;
using System.Linq;
using Fallout.Common;
using Fallout.Common.CI.GitHubActions;
using Fallout.Common.Execution;
using Fallout.Common.Git;
using Fallout.Common.IO;
using Fallout.Common.ProjectModel;
using Fallout.Common.Tooling;
using Fallout.Common.Tools.DotNet;
using Fallout.Common.Tools.GitVersion;
using Fallout.Common.Tools.GitHub;
using Fallout.Common.Tools.ReportGenerator;
using Fallout.Common.Tools.Xunit;
using Fallout.Common.Utilities;
using Fallout.Common.Utilities.Collections;
using Fallout.Common.CI;
using Fallout.Components;
using static Fallout.Common.Tools.DotNet.DotNetTasks;
using static Fallout.Common.Tools.ReportGenerator.ReportGeneratorTasks;
using static Fallout.Common.Tools.Xunit.XunitTasks;
using static Serilog.Log;
using Serilog;
using static ThisAssembly.Constants.CMConstants;

[UnsetVisualStudioEnvironmentVariables]
[DotNetVerbosityMapping]
class Build : FalloutBuild
{
    /* Support plugins are available for:
       - JetBrains ReSharper        https://nuke.build/resharper
       - JetBrains Rider            https://nuke.build/rider
       - Microsoft VisualStudio     https://nuke.build/visualstudio
       - Microsoft VSCode           https://nuke.build/vscode
    */

    public static int Main() => Execute<Build>(x => x.PublishInstallerRelease);

    [Parameter("The solution configuration to build. Default is 'Debug' (local) or 'CI' (server).")]
    readonly Configuration Configuration = Configuration.Debug;

    [Parameter("Use this parameter if you encounter build problems in any way, " +
        "to generate a .binlog file which holds some useful information.")]
    readonly bool? GenerateBinLog;

    [Parameter("GitHub authentication token for publishing releases")]
    readonly string GitHubToken = null!;
 
    [Solution(GenerateProjects = false)]
    readonly Solution Solution = null!;
    
    AbsolutePath MsiFile => ArtifactsDirectory / "MyApplication.msi";

    [Required]
    [GitRepository]
    readonly GitRepository GitRepository = null!;

    AbsolutePath ArtifactsDirectory => RootDirectory / "Artifacts";

    AbsolutePath AttachmentsDirectory => ArtifactsDirectory / "Attachments";

    AbsolutePath BuildLogsDirectory => AttachmentsDirectory / "build_logs";

    AbsolutePath CoverageDirectory => AttachmentsDirectory / "Coverage";

    AbsolutePath TestResultsDirectory => AttachmentsDirectory / "TestResults";


    [Required]
    [GitVersion(Framework = "net10.0", NoCache = true, NoFetch = true)]
    readonly GitVersion GitVersion;
	
    string SemVer = null!;

	Target CalculateVersion => _ => _
        .Executes(() =>
        {
            SemVer = GitVersion.SemVer;

           // Information("SemVer = {semver}", SemVer);
        });

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
			
			DotNet("tool install --global wix --version 6.0.2");
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
                .SetPublishProfile("FolderProfile"));
		
        
        });
	
    Target Installers => _ => _
        .DependsOn(PublishDesktop)
		.DependsOn(CalculateVersion)
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
			.SetProperty("FalloutBuild", "True")
                .EnableNoLogo());
        });

    Target PublishInstallerRelease => _ => _
        .DependsOn(Installers)
        .OnlyWhenDynamic(() => GitRepository.IsOnMainBranch())
        .Executes(() =>
        {
            var msiPath = ArtifactsDirectory / "Publish" / "Installers" / "CMSetup.msi";
            
            if (!msiPath.FileExists())
            {
                Warning($"MSI file not found at {msiPath}");
                return;
            }

            // Validate GitHub token
            if (string.IsNullOrEmpty(GitHubToken))
            {
                Error("GitHub token not provided. Set GitHubToken parameter or GITHUB_TOKEN environment variable.");
                return;
            }

            // Get version from git tag or use commit SHA
            var version = SemVer;
            
            Information($"Publishing MSI installer to GitHub releases for version {version}...");
            
            // Create authenticated GitHub client
            var gitHubClient = new Octokit.GitHubClient(new Octokit.ProductHeaderValue("ClinicManager-Build"))
            {
                Credentials = new Octokit.Credentials(GitHubToken)
            };
            
            var owner = GitRepository.GetGitHubOwner();
            var repo = GitRepository.GetGitHubName();
            
            var newRelease = new Octokit.NewRelease(version)
            {
                Name = $"ClinicManager {version}",
                Body = $"ClinicManager MSI Installer Release\n\nVersion: {version}\nBuild Date: {DateTime.UtcNow:yyyy-MM-dd}",
                Draft = false,
                Prerelease = false
            };

            try
            {
                var release = gitHubClient.Repository.Release.Create(owner, repo, newRelease).Result;
                
                Information($"Created release: {release.Name}");

                // Upload MSI as asset
                using var fileStream = System.IO.File.OpenRead(msiPath);
                var assetUpload = new Octokit.ReleaseAssetUpload()
                {
                    FileName = msiPath.Name,
                    ContentType = "application/x-msi",
                    RawData = fileStream
                };

                var uploadedAsset = gitHubClient.Repository.Release.UploadAsset(release, assetUpload).Result;
                
                Information($"MSI installer published successfully!");
                Information($"Release URL: {release.HtmlUrl}");
                Information($"Download URL: {uploadedAsset.BrowserDownloadUrl}");
            }
            catch (Exception ex)
            {
                Error($"Failed to publish release: {ex.Message}");
                throw;
            }
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

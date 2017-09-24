using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Xml;
using Terraria.ModLoader.Properties;
using static Terraria.ModLoader.Setup.Program;


using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.Utilities;
using dnSpy.Decompiler.MSBuild;



namespace Terraria.ModLoader.Setup
{
	public class DecompileTask : Task
	{
		private readonly string _outputDir;
		private readonly bool _serverOnly;
		public DecompileTask(ITaskInterface taskInterface, string outputDir, bool serverOnly = false) : base(taskInterface)
		{
			_outputDir = Path.Combine(baseDir, outputDir);
			_serverOnly = serverOnly;
		}

		public override bool ConfigurationDialog()
		{
			if (File.Exists(TerrariaPath) && File.Exists(TerrariaServerPath))
				return true;

			return (bool)taskInterface.Invoke(new Func<bool>(SelectTerrariaDialog));
		}

		public override bool StartupWarning()
		{
			return MessageBox.Show(
					"Decompilation may take a long time (1-3 hours) and consume a lot of RAM (2GB will not be enough)",
					"Ready to Decompile", MessageBoxButtons.OKCancel, MessageBoxIcon.Information)
				== DialogResult.OK;
		}

		public override void Run()
		{
			//TODO: TerrariaServer & Terraria decompilation separate
			taskInterface.SetStatus("Setting up everything");

			var filesToDecompile = new List<string> { TerrariaServerPath };
			if (!_serverOnly) filesToDecompile.Add(TerrariaPath);

			ProjectCreatorOptions options = new ProjectOptionsCreator(taskInterface, filesToDecompile, _outputDir)
			{
				NumThreads = Settings.Default.SingleDecompileThread ? 1 : 0,
				Merge = true
			}.Run();


			taskInterface.SetStatus("Deleting Old Sources");
			if (Directory.Exists(options.Directory)) Directory.Delete(options.Directory, true);


			taskInterface.SetStatus("Setting projects up");
			var decompileContext = new DecompileContext(options.CancellationToken, new NoLogger());
			var projects = new List<Project>();

			using (SatelliteAssemblyFinder satelliteAssemblyFinder = new SatelliteAssemblyFinder())
			{
				int i = 0;
				foreach (var projectModuleOptions in options.ProjectModules)
				{
					Project project = new Project(projectModuleOptions, "Terraria" + (++i), satelliteAssemblyFinder, options.CreateDecompilerOutput);
					projects.Add(project);
					project.CreateProjectFiles(decompileContext);
				}
			}

			var jobItems = new List<WorkItem>();
			jobItems.AddRange(GetJobs(projects).Select((job) =>
			{
				return new WorkItem("Setting jobs up", () => job.Create(decompileContext));
			}));

			var decompilingItems = new List<WorkItem>();
			decompilingItems.AddRange(projects.Select((project) =>
			{
				return new WorkItem("Decompiling: " + project.Filename, () =>
				{
					new ProjectWriter(project, project.Options.ProjectVersion ?? options.ProjectVersion, projects, options.UserGACPaths).Write();
				});
			}));

			taskInterface.SetMaxProgress(jobItems.Count + decompilingItems.Count);
			progress = 0;
			ExecuteParallel(jobItems, false, options.NumberOfThreads);
			ExecuteParallel(decompilingItems, false, options.NumberOfThreads);

			taskInterface.SetStatus("Creating .sln file");
			new SolutionWriter(options.ProjectVersion, projects, Path.Combine(options.Directory, options.SolutionFilename)).Write();
		}

		private List<IJob> GetJobs(List<Project> projects)
		{
			var jobs = new List<IJob>();
			projects.ForEach((p) =>
			{
				jobs.AddRange(p.GetJobs());
			});
			return jobs;
		}
	}

	//TODO: Should it throw all of the errors?
	internal class NoLogger : IMSBuildProjectWriterLogger
	{
		void IMSBuildProjectWriterLogger.Error(string message)
		{
			throw new Exception(message);
		}
	}
}
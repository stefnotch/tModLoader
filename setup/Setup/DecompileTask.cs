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
			//TODO: Use .net 4.6.1 https://stackoverflow.com/questions/31747992/garbage-collection-and-parallel-foreach-issue-after-vs2015-upgrade
			//https://stackoverflow.com/questions/13671053/nets-multi-threading-vs-multi-processing-awful-parallel-foreach-performance
			//TODO: TerrariaServer & Terraria decompilation separate
			taskInterface.SetStatus("Setting up everything");

			var filesToDecompile = new List<string> { TerrariaServerPath };
			if (!_serverOnly) filesToDecompile.Add(TerrariaPath);

			ProjectCreatorOptions options = new ProjectOptionsCreator(taskInterface, filesToDecompile, _outputDir)
			{
				NumThreads = Settings.Default.SingleDecompileThread ? 1 : 0
			}.Run();


			taskInterface.SetStatus("Deleting Old Sources");
			if (Directory.Exists(options.Directory)) Directory.Delete(options.Directory, true);


			taskInterface.SetStatus("Setting projects up");
			var decompileContext = new DecompileContext(options.CancellationToken, new NoLogger());
			var projects = new List<Project>();

			using (SatelliteAssemblyFinder satelliteAssemblyFinder = new SatelliteAssemblyFinder())
			{
				//TODO: Use .net 4.6.1
				foreach (var projectModuleOptions in options.ProjectModules)
				{
					Project project = new Project(projectModuleOptions, options.Directory, satelliteAssemblyFinder, options.CreateDecompilerOutput);
					

					//Fixing the filename
					project.Filename = Path.Combine(project.Directory, projectModuleOptions.Module.Assembly.Name + project.Options.Decompiler.ProjectFileExtension);
					//Now, Terraria & TerrariaServer have different "DefaultNamespaces"...fixing that as well
					project.DefaultNamespace = "";

					project.CreateProjectFiles(decompileContext);

					projects.Add(project);
				}
			}

			//.csproj files
			foreach (Project p in projects)
			{
				taskInterface.SetStatus("Creating " + p.AssemblyName + ".csproj");
				new ProjectWriter(p, p.Options.ProjectVersion ?? options.ProjectVersion, projects, options.UserGACPaths).Write();
			}


			var decompilationItems = new List<WorkItem>();
			decompilationItems.AddRange(GetDecompilationItems(projects).Select((decompilationItem) =>
			{
				return new WorkItem(decompilationItem.Description, () => decompilationItem.Create(decompileContext));
			}));


			taskInterface.SetMaxProgress(decompilationItems.Count);
			progress = 0;


			//Apparently order independent
			
			//.sln file
			//Still has one bug...
			taskInterface.SetStatus("Creating .sln file");
			new SolutionWriter(options.ProjectVersion, projects, Path.Combine(options.Directory, "..", options.SolutionFilename)).Write();

			//Source code decompilation
			ExecuteParallel(decompilationItems, false, options.NumberOfThreads);
		}


		/// <summary>
		/// Takes care of the duplicate stuff
		/// </summary>
		private List<IJob> GetDecompilationItems(List<Project> projects)
		{
			var jobNames = new HashSet<string>();
			var jobs = new List<IJob>();
			projects.ForEach((p) =>
			{
				foreach (IJob job in p.GetJobs())
				{
					var fileToDecompile = job as ProjectFile;
					if (fileToDecompile != null)
					{
						string filename = fileToDecompile.Filename;
						if (!jobNames.Contains(filename))
						{
							jobNames.Add(filename);
							jobs.Add(job);
						}
					}
					else
					{
						jobs.Add(job);
					}
				}
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
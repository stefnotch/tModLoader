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
			/* TODO: Improve performance even more:
			 * Test those things:
			https://stackoverflow.com/questions/31747992/garbage-collection-and-parallel-foreach-issue-after-vs2015-upgrade
			https://stackoverflow.com/questions/13671053/nets-multi-threading-vs-multi-processing-awful-parallel-foreach-performance
			*/
			taskInterface.SetStatus("Setting up everything...");

			var filesToDecompile = new List<string> { TerrariaServerPath };
			if (!_serverOnly) filesToDecompile.Add(TerrariaPath);

            //A lot of setup stuff...
			ProjectCreatorOptions options = (new ProjectOptionsCreator(taskInterface, filesToDecompile, _outputDir)).Run();

			taskInterface.SetStatus("Deleting old sources");
			if (Directory.Exists(_outputDir)) Directory.Delete(_outputDir, true);

			taskInterface.SetStatus("Setting projects up");

            var projects = new List<Project>();
            var decompileContext = new DecompileContext(taskInterface.CancellationToken(), new NoLogger());

			using (var satelliteAssemblyFinder = new SatelliteAssemblyFinder())
			{
				foreach (var projectModuleOptions in options.ProjectModules)
				{
                    string projectName = projectModuleOptions.Module.Assembly.Name;
                    taskInterface.SetStatus("Setting " + projectName + "up");

                    //Create a Terraria and a TerrariaServer project
                    var project = new Project(projectModuleOptions, _outputDir, satelliteAssemblyFinder, options.CreateDecompilerOutput);

                    //Some hacks that I have to use to get dnSpy to cooperate & to make the duplication removal work:
					//Fixing the filename, which ends up fixing the path 
					project.Filename = Path.Combine(project.Directory, projectName + project.Options.Decompiler.ProjectFileExtension);

					//Now, Terraria & TerrariaServer have different "DefaultNamespaces"...fixing that as well
                    //If not fixed, Terraria and TerrariaServer will end up in different directories
					project.DefaultNamespace = "";

                    //Creates all the folders
					project.CreateProjectFiles(decompileContext);

                    //Add the project to the list of projects
					projects.Add(project);
				}
			}

			//.csproj files
			foreach (var p in projects)
			{
				taskInterface.SetStatus("Creating " + p.AssemblyName + ".csproj");
				new ProjectWriter(p, p.Options.ProjectVersion ?? options.ProjectVersion, projects, options.UserGACPaths).Write();
			}
            
			DecompileFiles(projects, decompileContext);
		}

        /// <summary>
        /// Decompiles all the .cs files, libraries, etc.
        /// </summary>
		private void DecompileFiles(List<Project> projects, DecompileContext context)
		{
			var decompilationItems = new List<WorkItem>();
			decompilationItems.AddRange(GetDecompilationItems(projects).Select((decompilationItem) =>
			{
				return new WorkItem(decompilationItem.Description, () => decompilationItem.Create(context));
			}));
            
			ExecuteParallel(decompilationItems, true, Settings.Default.SingleDecompileThread ? 1 : 0);
		}

		/// <summary>
		/// Takes care of the duplicate stuff
		/// </summary>
		private List<IJob> GetDecompilationItems(List<Project> projects)
		{
			var jobNames = new HashSet<string>();
			var jobs = new List<IJob>();
            //For each possible job
			projects.ForEach((p) =>
			{
				foreach (var job in p.GetJobs())
				{
					var fileToDecompile = job as ProjectFile;
					if (fileToDecompile != null)
					{
						string filename = fileToDecompile.Filename;
                        //Check if it is a duplicate
						if (!jobNames.Contains(filename))
						{
                            //If not, add the job
							jobNames.Add(filename);
							jobs.Add(job);
						}
					}
					else
					{
                        //Non ProjectFile jobs
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
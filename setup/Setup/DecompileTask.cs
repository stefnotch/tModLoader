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
			var filesToDecompile = new List<string> { TerrariaServerPath };

			//TODO: Make this better
			if (!_serverOnly)
			{
				filesToDecompile.Add(TerrariaPath);
			}
			
			ProjectCreatorOptions options = new ProjectOptionsCreator(taskInterface, filesToDecompile, _outputDir)
			{
				NumThreads = Settings.Default.SingleDecompileThread ? 1 : 0,
				Merge = true
			}.Run();

			
			TaskInterface.SetStatus("Deleting Old Src");
			if (Directory.Exists(_options.Directory)) Directory.Delete(_options.Directory, true);

			TaskInterface.SetStatus("Decompiling");


			using (SatelliteAssemblyFinder satelliteAssemblyFinder = new SatelliteAssemblyFinder())
			{

			}

			var items = new List<WorkItem>();

			var serverModule = ReadModule(TerrariaServerPath, serverVersion);
			var serverSources = GetCodeFiles(serverModule, options).ToList();
			var serverResources = GetResourceFiles(serverModule, options).ToList();

			var sources = serverSources;
			var resources = serverResources;
			var infoModule = serverModule;
			if (!serverOnly)
			{
				var clientModule = !serverOnly ? ReadModule(TerrariaPath, clientVersion) : null;
				var clientSources = GetCodeFiles(clientModule, options).ToList();
				var clientResources = GetResourceFiles(clientModule, options).ToList();

				sources = CombineFiles(clientSources, sources, src => src.Key);
				resources = CombineFiles(clientResources, resources, res => res.Item1);
				infoModule = clientModule;

				items.Add(new WorkItem("Writing Terraria" + lang.ProjectFileExtension,
					() => WriteProjectFile(clientModule, clientGuid, clientSources, clientResources, options)));

				items.Add(new WorkItem("Writing Terraria" + lang.ProjectFileExtension + ".user",
					() => WriteProjectUserFile(clientModule, SteamDir, options)));
			}

			items.Add(new WorkItem("Writing TerrariaServer" + lang.ProjectFileExtension,
				() => WriteProjectFile(serverModule, serverGuid, serverSources, serverResources, options)));

			items.Add(new WorkItem("Writing TerrariaServer" + lang.ProjectFileExtension + ".user",
				() => WriteProjectUserFile(serverModule, SteamDir, options)));

			items.Add(new WorkItem("Writing Assembly Info",
				() => WriteAssemblyInfo(infoModule, options)));

			items.AddRange(sources.Select(src => new WorkItem(
				"Decompiling: " + src.Key, () => DecompileSourceFile(src, options))));

			items.AddRange(resources.Select(res => new WorkItem(
				"Extracting: " + res.Item1, () => ExtractResource(res, options))));

			ExecuteParallel(items, maxDegree: Settings.Default.SingleDecompileThread ? 1 : 0);
		}

		/////////////////////
		//    COPYRIGHT    //
		/////////////////////

		readonly ProjectCreatorOptions options;
		readonly List<Project> projects = new List<Project>();
		readonly IMSBuildProgressListener progressListener;
		int totalProgress;

		public string SolutionFilename => Path.Combine(options.Directory, options.SolutionFilename);

		public void Create()
		{
			SatelliteAssemblyFinder satelliteAssemblyFinder = null;

			var opts = new ParallelOptions
			{
				CancellationToken = options.CancellationToken,
				MaxDegreeOfParallelism = options.NumberOfThreads <= 0 ? Environment.ProcessorCount : options.NumberOfThreads,
			};
			var filenameCreator = new FilenameCreator(options.Directory);
			var ctx = new DecompileContext(options.CancellationToken, logger);
			satelliteAssemblyFinder = new SatelliteAssemblyFinder();
			Parallel.ForEach(options.ProjectModules, opts, modOpts => {
				options.CancellationToken.ThrowIfCancellationRequested();
				string name;
				lock (filenameCreator)
					name = filenameCreator.Create(modOpts.Module);
				var p = new Project(modOpts, name, satelliteAssemblyFinder, options.CreateDecompilerOutput);
				lock (projects)
					projects.Add(p);
				p.CreateProjectFiles(ctx);
			});

			var jobs = GetJobs().ToArray();
			bool writeSolutionFile = !string.IsNullOrEmpty(options.SolutionFilename);
			int maxProgress = jobs.Length + projects.Count;
			if (writeSolutionFile)
				maxProgress++;
			progressListener.SetMaxProgress(maxProgress);

			Parallel.ForEach(GetJobs(), opts, job => {
				options.CancellationToken.ThrowIfCancellationRequested();
				try
				{
					job.Create(ctx);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex)
				{
					var fjob = job as IFileJob;
					throw new Exception($"{fjob?.Filename}, {job.Description}", ex);
				}
				progressListener.SetProgress(Interlocked.Increment(ref totalProgress));
			});
			Parallel.ForEach(projects, opts, p => {
				options.CancellationToken.ThrowIfCancellationRequested();
				try
				{
					var writer = new ProjectWriter(p, p.Options.ProjectVersion ?? options.ProjectVersion, projects, options.UserGACPaths);
					writer.Write();
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex)
				{
					throw new Exception($"{p.Filename}", ex);
				}
				progressListener.SetProgress(Interlocked.Increment(ref totalProgress));
			});
			if (writeSolutionFile)
			{
				options.CancellationToken.ThrowIfCancellationRequested();
				try
				{
					var writer = new SolutionWriter(options.ProjectVersion, projects, SolutionFilename);
					writer.Write();
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex)
				{
					throw new Exception($"{SolutionFilename}", ex);
				}
				progressListener.SetProgress(Interlocked.Increment(ref totalProgress));
			}
			Debug.Assert(totalProgress == maxProgress);
			progressListener.SetProgress(maxProgress);

		}

		IEnumerable<IJob> GetJobs()
		{
			foreach (var p in projects)
			{
				foreach (var j in p.GetJobs())
					yield return j;
			}
		}
		/////////////////////
		//    COPYRIGHT    //
		/////////////////////
	}
}
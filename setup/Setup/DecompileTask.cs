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
		private readonly string _srcDir;
		private readonly bool _serverOnly;
		public DecompileTask(ITaskInterface taskInterface, string srcDir, bool serverOnly = false) : base(taskInterface)
		{
			_srcDir = Path.Combine(baseDir, srcDir);
			_serverOnly = serverOnly;
			//TODO: ServerOnly doesn't do anything
			//throw new NotImplementedException("serverOnly");
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
			taskInterface.SetStatus("Deleting Old Src");

			if (Directory.Exists(_srcDir)) Directory.Delete(_srcDir, true);

			taskInterface.SetStatus("Decompiling");


			var filesToDecompile = new List<string> { TerrariaServerPath };

			//TODO: Make this better
			if (!_serverOnly)
			{
				filesToDecompile.Add(TerrariaPath);
			}
			
			new DnSpyDecompiler(taskInterface, filesToDecompile, _srcDir)
			{
				NumThreads = Settings.Default.SingleDecompileThread ? 1 : 0,
				Merge = true
			}.Run();

		}
	}

	class ProgressListener : IMSBuildProgressListener
	{
		private ITaskInterface _taskInterface;
		public ProgressListener(ITaskInterface taskInterface)
		{
			_taskInterface = taskInterface;
		}
		public void SetMaxProgress(int maxProgress)
		{
			_taskInterface.SetMaxProgress(maxProgress);
		}
		public void SetProgress(int progress)
		{
			bool start;
			lock (newProgressLock)
			{
				start = newProgress == null;
				if (newProgress == null || progress > newProgress.Value)
					newProgress = progress;
			}
			if (start)
			{
				int? newValue;
				lock (newProgressLock)
				{
					newValue = newProgress;
					newProgress = null;
				}
				Debug.Assert(newValue != null);
				if (newValue != null)
					_taskInterface.SetProgress(newValue.Value);
			}
		}
		readonly object newProgressLock = new object();
		int? newProgress;

	}
}
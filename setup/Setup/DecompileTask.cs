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
				NumThreads = Settings.Default.SingleDecompileThread ? 1 : 0
			}.Run();

		}
	}



	/*
	Copyright (C) 2014-2016 de4dot@gmail.com

	This file is part of dnSpy

	dnSpy is free software: you can redistribute it and/or modify
	it under the terms of the GNU General Public License as published by
	the Free Software Foundation, either version 3 of the License, or
	(at your option) any later version.

	dnSpy is distributed in the hope that it will be useful,
	but WITHOUT ANY WARRANTY; without even the implied warranty of
	MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
	GNU General Public License for more details.

	You should have received a copy of the GNU General Public License
	along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/
	public sealed class DnSpyDecompiler
	{
		string language = DecompilerConstants.LANGUAGE_CSHARP.ToString();

		ProjectVersion projectVersion = ProjectVersion.VS2015;
		string outputDir;
		string slnName = "solution.sln";

		int numThreads = 0; //Default value --> 1 thread per core

		const int spaces = 4;

		const bool useGac = true;
		const bool unpackResources = true;
		const bool createResX = true;

		readonly DecompilationContext decompilationContext;
		readonly ModuleContext moduleContext;
		readonly AssemblyResolver assemblyResolver;
		readonly IBamlDecompiler bamlDecompiler;

		List<string> files;
		static readonly char PATHS_SEP = Path.PathSeparator;


		private ITaskInterface _taskInterface;

		
		//Unchecked:


		bool decompileBaml = true;
		Guid projectGuid = Guid.NewGuid();


		public DnSpyDecompiler(ITaskInterface taskInterface, List<string> filesToDecompile, string outputDirectory)
		{
			files = filesToDecompile;
			outputDir = outputDirectory;
			_taskInterface = taskInterface;
			
			decompilationContext = new DecompilationContext();
			decompilationContext.CancellationToken = taskInterface.CancellationToken();
			moduleContext = ModuleDef.CreateModuleContext(false); // Same as dnSpy.exe
			assemblyResolver = (AssemblyResolver)moduleContext.AssemblyResolver;
			assemblyResolver.EnableFrameworkRedirect = false; // Same as dnSpy.exe
			assemblyResolver.FindExactMatch = true; // Same as dnSpy.exe
			assemblyResolver.EnableTypeDefCache = true;
			bamlDecompiler = TryLoadBamlDecompiler();
			decompileBaml = bamlDecompiler != null;

			var langs = new List<IDecompiler>();
			langs.AddRange(GetAllLanguages());
			langs.Sort((a, b) => a.OrderUI.CompareTo(b.OrderUI));
			allLanguages = langs.ToArray();
		}


		static IEnumerable<IDecompiler> GetAllLanguages()
		{
			var asmNames = new string[] {
				"dnSpy.Decompiler.ILSpy.Core",
			};
			foreach (var asmName in asmNames)
			{
				foreach (var l in GetLanguagesInAssembly(asmName))
					yield return l;
			}
		}

		static IEnumerable<IDecompiler> GetLanguagesInAssembly(string asmName)
		{
			var asm = TryLoad(asmName);
			if (asm != null)
			{
				foreach (var type in asm.GetTypes())
				{
					if (!type.IsAbstract && !type.IsInterface && typeof(IDecompilerProvider).IsAssignableFrom(type))
					{
						var p = (IDecompilerProvider)Activator.CreateInstance(type);
						foreach (var l in p.Create())
							yield return l;
					}
				}
			}
		}

		static IBamlDecompiler TryLoadBamlDecompiler() => TryCreateType<IBamlDecompiler>("dnSpy.BamlDecompiler.x", "dnSpy.BamlDecompiler.BamlDecompiler");

		static Assembly TryLoad(string asmName)
		{
			try
			{
				return Assembly.Load(asmName);
			}
			catch
			{
			}
			return null;
		}

		static T TryCreateType<T>(string asmName, string typeFullName)
		{
			var asm = TryLoad(asmName);
			var type = asm?.GetType(typeFullName);
			return type == null ? default(T) : (T)Activator.CreateInstance(type);
		}

		public int Run()
		{
			try
			{
				RemoveTokenComments();
				
				if (allLanguages.Length == 0)
					throw new Exception("No languages were found. Make sure that the language dll files exist in the same folder as this program.");
				if (GetLanguage() == null)
					throw new Exception(string.Format("Language {0} does not exist", language));
				Decompile();
			}
			catch (Exception ex)
			{
				Console.WriteLine();
				Console.WriteLine("ERROR: {0}", ex.Message);
				return 1;
			}
			return 0;
		}

		/// <summary>
		/// Removes some useless comments in the output
		/// </summary>
		void RemoveTokenComments()
		{
			const string NO_TOKENS_COMMENT = "--no-tokens";
			IDecompiler lang = GetLanguage();
			Dictionary<string, Tuple<IDecompilerOption, Action<string>>> langDict = CreateDecompilerOptionsDictionary(lang);

			if (langDict.ContainsKey(NO_TOKENS_COMMENT))
			{
				langDict[NO_TOKENS_COMMENT].Item1.Value = false;
			}
		}
		
		string GetOptionName(IDecompilerOption opt, string extraPrefix = null)
		{
			var prefix = "--" + extraPrefix;
			var o = prefix + FixInvalidSwitchChars((opt.Name != null ? opt.Name : opt.Guid.ToString()));
			return o;
		}

		static string FixInvalidSwitchChars(string s) => s.Replace(' ', '-');
		

		const string BOOLEAN_NO_PREFIX = "no-";
		const string BOOLEAN_DONT_PREFIX = "dont-";


		static int ParseInt32(string s)
		{
			string error;
			var v = SimpleTypeConverter.ParseInt32(s, int.MinValue, int.MaxValue, out error);
			if (!string.IsNullOrEmpty(error))
				throw new Exception(error);
			return v;
		}

		static string ParseString(string s) => s;

		Dictionary<string, Tuple<IDecompilerOption, Action<string>>> CreateDecompilerOptionsDictionary(IDecompiler decompiler)
		{
			var dict = new Dictionary<string, Tuple<IDecompilerOption, Action<string>>>();

			if (decompiler == null)
				return dict;

			foreach (var tmp in decompiler.Settings.Options)
			{
				var opt = tmp;
				if (opt.Type == typeof(bool))
				{
					dict[GetOptionName(opt)] = Tuple.Create(opt, new Action<string>(a => opt.Value = true));
					dict[GetOptionName(opt, BOOLEAN_NO_PREFIX)] = Tuple.Create(opt, new Action<string>(a => opt.Value = false));
					dict[GetOptionName(opt, BOOLEAN_DONT_PREFIX)] = Tuple.Create(opt, new Action<string>(a => opt.Value = false));
				}
				else if (opt.Type == typeof(int))
					dict[GetOptionName(opt)] = Tuple.Create(opt, new Action<string>(a => opt.Value = ParseInt32(a)));
				else if (opt.Type == typeof(string))
					dict[GetOptionName(opt)] = Tuple.Create(opt, new Action<string>(a => opt.Value = ParseString(a)));
				else
					Debug.Fail($"Unsupported type: {opt.Type}");
			}

			return dict;
		}

		void AddSearchPath(string dir)
		{
			if (Directory.Exists(dir) && !addedPaths.Contains(dir))
			{
				addedPaths.Add(dir);
				assemblyResolver.PreSearchPaths.Add(dir);
			}
		}
		readonly HashSet<string> addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		void Decompile()
		{
			assemblyResolver.UseGAC = useGac;

			var files = new List<ProjectModuleOptions>(GetDotNetFiles());
			string guidStr = projectGuid.ToString();
			int guidNum = int.Parse(guidStr.Substring(36 - 8, 8), NumberStyles.HexNumber);
			string guidFormat = guidStr.Substring(0, 36 - 8) + "{0:X8}";
			foreach (var file in files.OrderBy(a => a.Module.Location, StringComparer.InvariantCultureIgnoreCase))
				file.ProjectGuid = new Guid(string.Format(guidFormat, guidNum++));

			if (string.IsNullOrEmpty(outputDir))
				throw new Exception("Missing output directory");
			if (GetLanguage().ProjectFileExtension == null)
				throw new Exception(string.Format("Language {0} doesn't support creating project files", GetLanguage().UniqueNameUI));

			var options = new ProjectCreatorOptions(outputDir, decompilationContext.CancellationToken);
			options.ProjectVersion = projectVersion;
			options.NumberOfThreads = numThreads;
			options.ProjectModules.AddRange(files);
			options.CreateDecompilerOutput = textWriter => new TextWriterDecompilerOutput(textWriter, GetIndenter());
			options.ProgressListener = new ProgressListener(_taskInterface);
			if (!string.IsNullOrEmpty(slnName))
				options.SolutionFilename = slnName;
			var creator = new MSBuildProjectCreator(options);
			creator.Create();

		}

		Indenter GetIndenter()
		{
			if (spaces <= 0)
				return new Indenter(4, 4, true);
			return new Indenter(spaces, spaces, false);
		}

		IEnumerable<ProjectModuleOptions> GetDotNetFiles()
		{
			foreach (var file in files)
			{
				if (File.Exists(file))
				{
					var info = OpenNetFile(file);
					if (info == null)
						throw new Exception(string.Format("{0} is not a .NET file", file));
					yield return info;
				}
				else if (Directory.Exists(file))
				{
					foreach (var info in DumpDir(file, null))
						yield return info;
				}
				else
				{
					var path = Path.GetDirectoryName(file);
					var name = Path.GetFileName(file);
					if (Directory.Exists(path))
					{
						foreach (var info in DumpDir(path, name))
							yield return info;
					}
					else
						throw new Exception(string.Format("File/directory '{0}' doesn't exist", file));
				}
			}
		}

		IEnumerable<ProjectModuleOptions> DumpDir(string path, string pattern)
		{
			pattern = pattern ?? "*";
			Stack<string> stack = new Stack<string>();
			stack.Push(path);
			while (stack.Count > 0)
			{
				path = stack.Pop();
				foreach (var info in DumpDir2(path, pattern))
					yield return info;
			}
		}
		
		IEnumerable<ProjectModuleOptions> DumpDir2(string path, string pattern)
		{
			pattern = pattern ?? "*";
			foreach (var fi in GetFiles(path, pattern))
			{
				var info = OpenNetFile(fi.FullName);
				if (info != null)
					yield return info;
			}
		}

		IEnumerable<FileInfo> GetFiles(string path, string pattern)
		{
			IEnumerable<FileSystemInfo> fsysIter = null;
			try
			{
				fsysIter = new DirectoryInfo(path).EnumerateFileSystemInfos(pattern, SearchOption.TopDirectoryOnly);
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
			catch (SecurityException)
			{
			}
			if (fsysIter == null)
				yield break;

			foreach (var info in fsysIter)
			{
				if ((info.Attributes & System.IO.FileAttributes.Directory) != 0)
					continue;
				FileInfo fi = null;
				try
				{
					fi = new FileInfo(info.FullName);
				}
				catch (IOException)
				{
				}
				catch (UnauthorizedAccessException)
				{
				}
				catch (SecurityException)
				{
				}
				if (fi != null)
					yield return fi;
			}
		}

		ProjectModuleOptions OpenNetFile(string file)
		{
			try
			{
				file = Path.GetFullPath(file);
				if (!File.Exists(file))
					return null;
				return CreateProjectModuleOptions(ModuleDefMD.Load(file, moduleContext));
			}
			catch
			{
			}
			return null;
		}

		ProjectModuleOptions CreateProjectModuleOptions(ModuleDef mod)
		{
			mod.EnableTypeDefFindCache = true;
			moduleContext.AssemblyResolver.AddToCache(mod);
			AddSearchPath(Path.GetDirectoryName(mod.Location));
			var proj = new ProjectModuleOptions(mod, GetLanguage(), decompilationContext);
			proj.DontReferenceStdLib = false;
			proj.UnpackResources = unpackResources;
			proj.CreateResX = createResX;
			proj.DecompileXaml = decompileBaml && bamlDecompiler != null;
			var o = BamlDecompilerOptions.Create(GetLanguage());
			var outputOptions = new XamlOutputOptions
			{
				IndentChars = "\t",
				NewLineChars = Environment.NewLine,
				NewLineOnAttributes = true,
			};
			if (bamlDecompiler != null)
				proj.DecompileBaml = (a, b, c, d) => bamlDecompiler.Decompile(a, b, c, o, d, outputOptions);
			return proj;
		}

		IDecompiler GetLanguage()
		{
			Guid guid;
			bool hasGuid = Guid.TryParse(language, out guid);
			return AllLanguages.FirstOrDefault(a =>
			{
				if (StringComparer.OrdinalIgnoreCase.Equals(language, a.UniqueNameUI))
					return true;
				if (hasGuid && (guid.Equals(a.UniqueGuid) || guid.Equals(a.GenericGuid)))
					return true;
				return false;
			});
		}

		IDecompiler[] AllLanguages => allLanguages;

		public int NumThreads
		{
			get
			{
				return numThreads;
			}

			set
			{
				numThreads = value;
			}
		}

		readonly IDecompiler[] allLanguages;
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
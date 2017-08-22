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


		public override void Run()
		{
			//TODO: Remove args
			string[] args = { };
			new DnSpyDecompiler(new List<string> { TerrariaPath }, _srcDir)
			{
				NumThreads = Settings.Default.SingleDecompileThread ? 1 : 0
			}.Run(args);
		}
	}




	public sealed class DnSpyDecompiler
	{
		string language = DecompilerConstants.LANGUAGE_CSHARP.ToString();

		ProjectVersion projectVersion = ProjectVersion.VS2015;
		string outputDir;
		string slnName = "solution.sln";

		int numThreads = 0; //Default value --> 1 thread per core

		int spaces = 4;


		const bool useGac = true;


		readonly DecompilationContext decompilationContext;
		readonly ModuleContext moduleContext;
		readonly AssemblyResolver assemblyResolver;
		readonly IBamlDecompiler bamlDecompiler;

		List<string> files;
		static readonly char PATHS_SEP = Path.PathSeparator;


		//Unchecked:


		bool isRecursive = false;

		bool addCorlibRef = true;
		bool unpackResources = true;
		bool createResX = true;
		bool decompileBaml = true;
		Guid projectGuid = Guid.NewGuid();




		readonly List<string> asmPaths;
		readonly List<string> userGacPaths;
		readonly List<string> gacFiles;





		public DnSpyDecompiler(List<string> filesToDecompile, string outputDirectory)
		{
			files = filesToDecompile;
			outputDir = outputDirectory;



			asmPaths = new List<string>();
			userGacPaths = new List<string>();
			gacFiles = new List<string>();
			decompilationContext = new DecompilationContext();
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

		public int Run(string[] args)
		{
			try
			{
				//Messing around with some internal stuff...
				const string NO_TOKENS_COMMENT = "--no-tokens";
				IDecompiler lang = GetLanguage();
				Dictionary<string, Tuple<IDecompilerOption, Action<string>>> langDict = CreateDecompilerOptionsDictionary(lang);

				if(langDict.ContainsKey(NO_TOKENS_COMMENT))
				{
					langDict[NO_TOKENS_COMMENT].Item1.Value=false;
				}


				ParseCommandLine(args);
				if (allLanguages.Length == 0)
					throw new Exception("No languages were found. Make sure that the language dll files exist in the same folder as this program.");
				if (GetLanguage() == null)
					throw new Exception(string.Format("Language {0} does not exist", language));
				Decompile();
			}
			catch (Exception ex)
			{
				PrintHelp();
				Console.WriteLine();
				Console.WriteLine("ERROR: {0}", ex.Message);
				return 1;
			}
			return errors == 0 ? 0 : 1;
		}

		void PrintHelp()
		{
			var progName = GetProgramBaseName();
			Console.WriteLine(progName + " " + "[options] [fileOrDir1] [fileOrDir2] [...]", progName);
			Console.WriteLine();
			foreach (var info in usageInfos)
			{
				var arg = info.Option;
				if (info.OptionArgument != null)
					arg = arg + " " + info.OptionArgument;
				Console.WriteLine("  {0,-12}   {1}", arg, string.Format(info.Description, PATHS_SEP));
			}
			Console.WriteLine();
			Console.WriteLine("Languages:");
			foreach (var lang in AllLanguages)
				Console.WriteLine("  {0} ({1})", lang.UniqueNameUI, lang.UniqueGuid.ToString("B"));

			var langLists = GetLanguageOptions().Where(a => a[0].Settings.Options.Any()).ToArray();
			if (langLists.Length > 0)
			{
				Console.WriteLine();
				Console.WriteLine("Language options:");
				Console.WriteLine("All boolean options can be disabled by using 'no-' or 'dont-', eg. --dont-sort-members");
				foreach (var langList in langLists)
				{
					Console.WriteLine();
					foreach (var lang in langList)
						Console.WriteLine("  {0} ({1})", lang.UniqueNameUI, lang.UniqueGuid.ToString("B"));
					foreach (var opt in langList[0].Settings.Options)
						Console.WriteLine("    {0}\t({1} = {2}) {3}", GetOptionName(opt), opt.Type.Name, opt.Value, opt.Description);
				}
			}
			Console.WriteLine();
			Console.WriteLine("Examples:");
			foreach (var info in helpInfos)
			{
				Console.WriteLine("  " + progName + " " + info.CommandLine);
				Console.WriteLine("      " + info.Description);
			}
		}

		struct UsageInfo
		{
			public string Option { get; }
			public string OptionArgument { get; }
			public string Description { get; }
			public UsageInfo(string option, string optionArgument, string description)
			{
				Option = option;
				OptionArgument = optionArgument;
				Description = description;
			}
		}
		static readonly UsageInfo[] usageInfos = new UsageInfo[] {
			new UsageInfo("--asm-path", "path", "assembly search path. Paths can be separated with '{0}' or you can use multiple --asm-path's"),
			new UsageInfo("--user-gac", "path", "user GAC path. Paths can be separated with '{0}' or you can use multiple --user-gac's"),
			new UsageInfo("--no-gac", null, "don't use the GAC to look up assemblies. Useful with --no-stdlib"),
			new UsageInfo("--no-stdlib", null, "projects don't reference mscorlib"),
			new UsageInfo("--no-resources", null, "don't unpack resources"),
			new UsageInfo("--no-resx", null, "don't create .resx files"),
			new UsageInfo("--no-baml", null, "don't decompile baml to xaml"),
			new UsageInfo("--project-guid", "N", "project guid"),
			new UsageInfo("-t", "name", "decompile the type with the specified name to stdout. Either Namespace.Name or Name, case insensitive"),
			new UsageInfo("--type", "name", "same as -t"),
			new UsageInfo("--gac-file", "assembly", "decompile an assembly from the GAC. Use full assembly name to use an exact version."),
			new UsageInfo("-r", null, "recursive search for .NET files to decompile"),
			new UsageInfo("-o", "outdir", "output directory"),
			new UsageInfo("-l", "lang", "set language, default is C#. Guids can be used."),
		};

		struct HelpInfo
		{
			public string CommandLine { get; }
			public string Description { get; }
			public HelpInfo(string description, string commandLine)
			{
				CommandLine = commandLine;
				Description = description;
			}
		}
		static readonly HelpInfo[] helpInfos = new HelpInfo[] {
			new HelpInfo(@"Decompiles all .NET files in the above directory and saves files to C:\out\path", @"-o C:\out\path C:\some\path"),
			new HelpInfo(@"Decompiles all .NET files in the above directory and all sub directories", @"-o C:\out\path -r C:\some\path"),
			new HelpInfo(@"Decompiles all *.dll .NET files in the above directory and saves files to C:\out\path", @"-o C:\out\path C:\some\path\*.dll"),
			new HelpInfo(@"Decompiles System.Int32 from mscorlib", @"-t system.int32 --gac-file ""mscorlib, Version=4.0.0.0"""),
		};

		string GetOptionName(IDecompilerOption opt, string extraPrefix = null)
		{
			var prefix = "--" + extraPrefix;
			var o = prefix + FixInvalidSwitchChars((opt.Name != null ? opt.Name : opt.Guid.ToString()));
			return o;
		}

		static string FixInvalidSwitchChars(string s) => s.Replace(' ', '-');

		List<List<IDecompiler>> GetLanguageOptions()
		{
			var list = new List<List<IDecompiler>>();
			var dict = new Dictionary<object, List<IDecompiler>>();
			foreach (var lang in AllLanguages)
			{
				List<IDecompiler> opts;
				if (!dict.TryGetValue(lang.Settings, out opts))
				{
					dict.Add(lang.Settings, opts = new List<IDecompiler>());
					list.Add(opts);
				}
				opts.Add(lang);
			}
			return list;
		}

		string GetProgramBaseName() => GetBaseName(Environment.GetCommandLineArgs()[0]);

		string GetBaseName(string name)
		{
			int index = name.LastIndexOf(Path.DirectorySeparatorChar);
			if (index < 0)
				return name;
			return name.Substring(index + 1);
		}

		const string BOOLEAN_NO_PREFIX = "no-";
		const string BOOLEAN_DONT_PREFIX = "dont-";

		void ParseCommandLine(string[] args)
		{
			bool canParseCommands = true;
			IDecompiler lang = null;
			Dictionary<string, Tuple<IDecompilerOption, Action<string>>> langDict = null;
			for (int i = 0; i < args.Length; i++)
			{
				if (lang == null)
				{
					lang = GetLanguage();
					langDict = CreateDecompilerOptionsDictionary(lang);
				}
				var arg = args[i];
				var next = i + 1 < args.Length ? args[i + 1] : null;
				if (arg.Length == 0)
					continue;

				// **********************************************************************
				// If you add more '--' options here, also update 'string[] ourOptions'
				// **********************************************************************

				if (canParseCommands && arg[0] == '-')
				{
					string error;
					switch (arg)
					{
						case "--":
							canParseCommands = false;
							break;

						case "-r":
						case "--recursive":
							isRecursive = true;
							break;

						case "--asm-path":
							if (next == null)
								throw new Exception("Missing assembly search path");
							asmPaths.AddRange(next.Split(new char[] { PATHS_SEP }, StringSplitOptions.RemoveEmptyEntries));
							i++;
							break;

						case "--user-gac":
							if (next == null)
								throw new Exception("Missing user GAC path");
							userGacPaths.AddRange(next.Split(new char[] { PATHS_SEP }, StringSplitOptions.RemoveEmptyEntries));
							i++;
							break;

						case "--no-stdlib":
							addCorlibRef = false;
							break;

						case "--no-resources":
							unpackResources = false;
							break;

						case "--no-resx":
							createResX = false;
							break;

						case "--no-baml":
							decompileBaml = false;
							break;

						case "--gac-file":
							if (next == null)
								throw new Exception("Missing GAC assembly name");
							i++;
							gacFiles.Add(next);
							break;

						case "--project-guid":
							if (next == null || !Guid.TryParse(next, out projectGuid))
								throw new Exception("Invalid GUID");
							i++;
							break;

						default:
							Tuple<IDecompilerOption, Action<string>> tuple;
							if (langDict.TryGetValue(arg, out tuple))
							{
								bool hasArg = tuple.Item1.Type != typeof(bool);
								if (hasArg && next == null)
									throw new Exception("Missing option argument");
								if (hasArg)
									i++;
								tuple.Item2(next);
								break;
							}

							throw new Exception(string.Format("Invalid option: {0}", arg));
					}
				}
				else
					files.Add(arg);
			}
		}

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
			foreach (var dir in asmPaths)
				AddSearchPath(dir);
			foreach (var dir in userGacPaths)
				AddSearchPath(dir);
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
			options.UserGACPaths.AddRange(userGacPaths);
			options.CreateDecompilerOutput = textWriter => new TextWriterDecompilerOutput(textWriter, GetIndenter());
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

		static TypeDef FindType(ModuleDef module, string name)
		{
			return FindTypeFullName(module, name, StringComparer.Ordinal) ??
				FindTypeFullName(module, name, StringComparer.OrdinalIgnoreCase) ??
				FindTypeName(module, name, StringComparer.Ordinal) ??
				FindTypeName(module, name, StringComparer.OrdinalIgnoreCase);
		}

		static TypeDef FindTypeFullName(ModuleDef module, string name, StringComparer comparer)
		{
			var sb = new StringBuilder();
			return module.GetTypes().FirstOrDefault(a =>
			{
				sb.Clear();
				string s1, s2;
				if (comparer.Equals(s1 = FullNameCreator.FullName(a, false, null, sb), name))
					return true;
				sb.Clear();
				if (comparer.Equals(s2 = FullNameCreator.FullName(a, true, null, sb), name))
					return true;
				sb.Clear();
				if (comparer.Equals(CleanTypeName(s1), name))
					return true;
				sb.Clear();
				return comparer.Equals(CleanTypeName(s2), name);
			});
		}

		static TypeDef FindTypeName(ModuleDef module, string name, StringComparer comparer)
		{
			var sb = new StringBuilder();
			return module.GetTypes().FirstOrDefault(a =>
			{
				sb.Clear();
				string s1, s2;
				if (comparer.Equals(s1 = FullNameCreator.Name(a, false, sb), name))
					return true;
				sb.Clear();
				if (comparer.Equals(s2 = FullNameCreator.Name(a, true, sb), name))
					return true;
				sb.Clear();
				if (comparer.Equals(CleanTypeName(s1), name))
					return true;
				sb.Clear();
				return comparer.Equals(CleanTypeName(s2), name);
			});
		}

		static string CleanTypeName(string s)
		{
			int i = s.LastIndexOf('`');
			if (i < 0)
				return s;
			return s.Substring(0, i);
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

			// Don't use exact matching here so the user can tell us to load eg. "mscorlib, Version=4.0.0.0" which
			// is easier to type than the full assembly name
			var oldFindExactMatch = assemblyResolver.FindExactMatch;
			assemblyResolver.FindExactMatch = false;
			foreach (var asmName in gacFiles)
			{
				var asm = assemblyResolver.Resolve(new AssemblyNameInfo(asmName), null);
				if (asm == null)
					throw new Exception(string.Format("Couldn't resolve GAC assembly '{0}'", asmName));
				yield return CreateProjectModuleOptions(asm.ManifestModule);
			}
			assemblyResolver.FindExactMatch = oldFindExactMatch;
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
				if (isRecursive)
				{
					foreach (var di in GetDirs(path))
						stack.Push(di.FullName);
				}
			}
		}

		IEnumerable<DirectoryInfo> GetDirs(string path)
		{
			IEnumerable<FileSystemInfo> fsysIter = null;
			try
			{
				fsysIter = new DirectoryInfo(path).EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly);
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
				if ((info.Attributes & System.IO.FileAttributes.Directory) == 0)
					continue;
				DirectoryInfo di = null;
				try
				{
					di = new DirectoryInfo(info.FullName);
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
				if (di != null)
					yield return di;
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
			proj.DontReferenceStdLib = !addCorlibRef;
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


		int errors;
	}
}
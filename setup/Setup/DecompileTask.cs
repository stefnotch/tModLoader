using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Xml;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Ast;
using ICSharpCode.ILSpy;
using ICSharpCode.ILSpy.TextView;
using Mono.Cecil;
using Terraria.ModLoader.Properties;
using static Terraria.ModLoader.Setup.Program;



using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
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
		private class EmbeddedAssemblyResolver : BaseAssemblyResolver
		{
			private Dictionary<string, AssemblyDefinition> cache = new Dictionary<string, AssemblyDefinition>();
			public ModuleDefinition baseModule;

			public EmbeddedAssemblyResolver() {
				AddSearchDirectory(SteamDir);
			}

			public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters) {
				lock (this) {
					AssemblyDefinition assemblyDefinition;
					if (cache.TryGetValue(name.FullName, out assemblyDefinition))
						return assemblyDefinition;

					//ignore references to other mscorlib versions, they are unneeded and produce namespace conflicts
					if (name.Name == "mscorlib" && name.Version.Major != 4)
						goto skip;

					//look in the base module's embedded resources
					if (baseModule != null) {
						var resName = name.Name + ".dll";
						var res =
							baseModule.Resources.OfType<Mono.Cecil.EmbeddedResource>()
								.SingleOrDefault(r => r.Name.EndsWith(resName));
						if (res != null)
							assemblyDefinition = AssemblyDefinition.ReadAssembly(res.GetResourceStream(), parameters);
					}

					if (assemblyDefinition == null)
						assemblyDefinition = base.Resolve(name, parameters);

				skip:
					cache[name.FullName] = assemblyDefinition;
					return assemblyDefinition;
				}
			}
		}

		private static readonly CSharpLanguage lang = new CSharpLanguage();
		private static readonly Guid clientGuid = new Guid("3996D5FA-6E59-4FE4-9F2B-40EEEF9645D5");
		private static readonly Guid serverGuid = new Guid("85BF1171-A0DC-4696-BFA4-D6E9DC4E0830");
		public static readonly Version clientVersion = new Version(Settings.Default.ClientVersion);
		public static readonly Version serverVersion = new Version(Settings.Default.ServerVersion);

		public readonly string srcDir;
		public readonly bool serverOnly;

		public string FullSrcDir => Path.Combine(baseDir, srcDir);

		public DecompileTask(ITaskInterface taskInterface, string srcDir, bool serverOnly = false) : base(taskInterface) {
			this.srcDir = srcDir;
			this.serverOnly = serverOnly;
		}

		public override bool ConfigurationDialog() {
			if (File.Exists(TerrariaPath) && File.Exists(TerrariaServerPath))
				return true;

			return (bool)taskInterface.Invoke(new Func<bool>(SelectTerrariaDialog));
		}

		public override bool StartupWarning() {
			return MessageBox.Show(
					"Decompilation may take a long time (1-3 hours) and consume a lot of RAM (2GB will not be enough)",
					"Ready to Decompile", MessageBoxButtons.OKCancel, MessageBoxIcon.Information)
				== DialogResult.OK;
		}

		public override void Run() {
			taskInterface.SetStatus("Deleting Old Src");

			if (Directory.Exists(FullSrcDir))
				Directory.Delete(FullSrcDir, true);

			var options = new DecompilationOptions {
				FullDecompilation = true,
				CancellationToken = taskInterface.CancellationToken(),
				SaveAsProjectDirectory = FullSrcDir
			};

			var items = new List<WorkItem>();

			var serverModule = ReadModule(TerrariaServerPath, serverVersion);
			var serverSources = GetCodeFiles(serverModule, options).ToList();
			var serverResources = GetResourceFiles(serverModule, options).ToList();

			var sources = serverSources;
			var resources = serverResources;
			var infoModule = serverModule;
			if (!serverOnly) {
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

			items.Add(new WorkItem("Writing TerrariaServer"+lang.ProjectFileExtension,
				() => WriteProjectFile(serverModule, serverGuid, serverSources, serverResources, options)));

			items.Add(new WorkItem("Writing TerrariaServer"+lang.ProjectFileExtension+".user",
				() => WriteProjectUserFile(serverModule, SteamDir, options)));
			
			items.Add(new WorkItem("Writing Assembly Info",
				() => WriteAssemblyInfo(infoModule, options)));
			
			items.AddRange(sources.Select(src => new WorkItem(
				"Decompiling: "+src.Key, () => DecompileSourceFile(src, options))));

			items.AddRange(resources.Select(res => new WorkItem(
				"Extracting: " + res.Item1, () => ExtractResource(res, options))));
			
			ExecuteParallel(items, maxDegree: Settings.Default.SingleDecompileThread ? 1 : 0);
		}

		protected ModuleDefinition ReadModule(string modulePath, Version version) {
			taskInterface.SetStatus("Loading "+Path.GetFileName(modulePath));
			var resolver = new EmbeddedAssemblyResolver();
			var module = ModuleDefinition.ReadModule(modulePath, 
				new ReaderParameters { AssemblyResolver = resolver});
			resolver.baseModule = module;
			
			if (module.Assembly.Name.Version != version)
				throw new Exception($"{module.Assembly.Name.Name} version {module.Assembly.Name.Version}. Expected {version}");

			return module;
		}

#region ReflectedMethods
		private static readonly MethodInfo _IncludeTypeWhenDecompilingProject = typeof(CSharpLanguage)
			.GetMethod("IncludeTypeWhenDecompilingProject", BindingFlags.NonPublic | BindingFlags.Instance);

		public static bool IncludeTypeWhenDecompilingProject(TypeDefinition type, DecompilationOptions options) {
			return (bool)_IncludeTypeWhenDecompilingProject.Invoke(lang, new object[] { type, options });
		}

		private static readonly MethodInfo _WriteProjectFile = typeof(CSharpLanguage)
			.GetMethod("WriteProjectFile", BindingFlags.NonPublic | BindingFlags.Instance);

		public static void WriteProjectFile(TextWriter writer, IEnumerable<Tuple<string, string>> files, ModuleDefinition module) {
			_WriteProjectFile.Invoke(lang, new object[] { writer, files, module });
		}

		private static readonly MethodInfo _CleanUpName = typeof(DecompilerTextView)
			.GetMethod("CleanUpName", BindingFlags.NonPublic | BindingFlags.Static);

		public static string CleanUpName(string name) {
			return (string)_CleanUpName.Invoke(null, new object[] { name });
		}
#endregion

		//from ICSharpCode.ILSpy.CSharpLanguage
		private static IEnumerable<IGrouping<string, TypeDefinition>> GetCodeFiles(ModuleDefinition module, DecompilationOptions options) {
			return module.Types.Where(t => IncludeTypeWhenDecompilingProject(t, options))
				.GroupBy(type => {
					var file = CleanUpName(type.Name) + lang.FileExtension;
					return string.IsNullOrEmpty(type.Namespace) ? file : Path.Combine(CleanUpName(type.Namespace), file);
				}, StringComparer.OrdinalIgnoreCase);
		}

		private static IEnumerable<Tuple<string, Mono.Cecil.EmbeddedResource>> GetResourceFiles(ModuleDefinition module, DecompilationOptions options) {
			return module.Resources.OfType<Mono.Cecil.EmbeddedResource>().Select(res => {
				var path = res.Name;
				path = path.Replace("Terraria.Libraries.", "Terraria.Libraries\\");
				if (path.EndsWith(".dll")) {
					var asmRef = module.AssemblyReferences.SingleOrDefault(r => path.EndsWith(r.Name + ".dll"));
					if (asmRef != null)
						path = path.Substring(0, path.Length - asmRef.Name.Length - 5) +
						Path.DirectorySeparatorChar + asmRef.Name + ".dll";
				}
				return Tuple.Create(path, res);
			});
		}

		private static List<T> CombineFiles<T, K>(IEnumerable<T> client, IEnumerable<T> server, Func<T, K> key) {
			var list = client.ToList();
			var set = new HashSet<K>(list.Select(key));
			list.AddRange(server.Where(src => !set.Contains(key(src))));
			return list;
		}

		private static void ExtractResource(Tuple<string, Mono.Cecil.EmbeddedResource> res, DecompilationOptions options) {
			var path = Path.Combine(options.SaveAsProjectDirectory, res.Item1);
			CreateParentDirectory(path);

			var s = res.Item2.GetResourceStream();
			s.Position = 0;
			using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
				s.CopyTo(fs);
		}

		private static void DecompileSourceFile(IGrouping<string, TypeDefinition> src, DecompilationOptions options) {
			var path = Path.Combine(options.SaveAsProjectDirectory, src.Key);
			CreateParentDirectory(path);

			using (var w = new StreamWriter(path)) {
				var builder = new AstBuilder(
					new DecompilerContext(src.First().Module) {
						CancellationToken = options.CancellationToken,
						Settings = options.DecompilerSettings
					});

				foreach (var type in src)
					builder.AddType(type);

				builder.GenerateCode(new PlainTextOutput(w));
			}
		}

		private static void WriteAssemblyInfo(ModuleDefinition module, DecompilationOptions options) {
			var path = Path.Combine(options.SaveAsProjectDirectory, Path.Combine("Properties", "AssemblyInfo" + lang.FileExtension));
			CreateParentDirectory(path);

			using (var w = new StreamWriter(path)) {
				var builder = new AstBuilder(
					new DecompilerContext(module) {
						CancellationToken = options.CancellationToken,
						Settings = options.DecompilerSettings
					});

				builder.AddAssembly(module, true);
				builder.GenerateCode(new PlainTextOutput(w));
			}
		}

		private static void WriteProjectFile(ModuleDefinition module, Guid guid,
				IEnumerable<IGrouping<string, TypeDefinition>> sources, 
				IEnumerable<Tuple<string, Mono.Cecil.EmbeddedResource>> resources,
				DecompilationOptions options) {

			//flatten the file list
			var files = sources.Select(src => Tuple.Create("Compile", src.Key))
				.Concat(resources.Select(res => Tuple.Create("EmbeddedResource", res.Item1)))
				.Concat(new[] { Tuple.Create("Compile", Path.Combine("Properties", "AssemblyInfo" + lang.FileExtension)) });

			//fix the guid and add a value to the CommandLineArguments field so the method doesn't crash
			var claField = typeof(App).GetField("CommandLineArguments", BindingFlags.Static | BindingFlags.NonPublic);
			var claType = typeof(App).Assembly.GetType("ICSharpCode.ILSpy.CommandLineArguments");
			var claConstructor = claType.GetConstructors()[0];
			var claInst = claConstructor.Invoke(new object[] {Enumerable.Empty<string>()});
			var guidField = claType.GetField("FixedGuid");
			guidField.SetValue(claInst, guid);
			claField.SetValue(null, claInst);

			var path = Path.Combine(options.SaveAsProjectDirectory,
				Path.GetFileNameWithoutExtension(module.Name) + lang.ProjectFileExtension);
			CreateParentDirectory(path);

			using (var w = new StreamWriter(path))
				WriteProjectFile(w, files, module);
			using (var w = new StreamWriter(path, true))
				w.Write(Environment.NewLine);
		}

		private static void WriteProjectUserFile(ModuleDefinition module, string debugWorkingDir, DecompilationOptions options) {
			var path = Path.Combine(options.SaveAsProjectDirectory,
				Path.GetFileNameWithoutExtension(module.Name) + lang.ProjectFileExtension + ".user");
			CreateParentDirectory(path);

			using (var w = new StreamWriter(path))
				using (var xml = new XmlTextWriter(w)) {
					xml.Formatting = Formatting.Indented;
					xml.WriteStartDocument();
					xml.WriteStartElement("Project", "http://schemas.microsoft.com/developer/msbuild/2003");
					xml.WriteAttributeString("ToolsVersion", "4.0");
					xml.WriteStartElement("PropertyGroup");
					xml.WriteAttributeString("Condition", "'$(Configuration)' == 'Debug'");
					xml.WriteStartElement("StartWorkingDirectory");
					xml.WriteValue(debugWorkingDir);
					xml.WriteEndElement();
					xml.WriteEndElement();
					xml.WriteEndDocument();
				}
		}
	}







	[Serializable]
	sealed class ErrorException : Exception
	{
		public ErrorException(string s)
			: base(s)
		{
		}
	}

	static class Program
	{
		static int Main(string[] args)
		{
			if (!dnlib.Settings.IsThreadSafe)
			{
				Console.WriteLine("dnlib wasn't compiled with THREAD_SAFE defined");
				return 1;
			}

			var oldEncoding = Console.OutputEncoding;
			try
			{
				// Make sure russian and chinese characters are shown correctly
				Console.OutputEncoding = Encoding.UTF8;

				return new DnSpyDecompiler().Run(args);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine(string.Format("{0}", ex));
				return 1;
			}
			finally
			{
				Console.OutputEncoding = oldEncoding;
			}
		}
	}

	struct ConsoleColorPair
	{
		public ConsoleColor? Foreground { get; }
		public ConsoleColor? Background { get; }
		public ConsoleColorPair(ConsoleColor? foreground, ConsoleColor? background)
		{
			Foreground = foreground;
			Background = background;
		}
	}

	sealed class ColorProvider
	{
		readonly Dictionary<TextColor, ConsoleColorPair> colors = new Dictionary<TextColor, ConsoleColorPair>();

		public void Add(TextColor color, ConsoleColor? foreground, ConsoleColor? background = null)
		{
			if (foreground != null || background != null)
				colors[color] = new ConsoleColorPair(foreground, background);
		}

		public ConsoleColorPair? GetColor(TextColor? color)
		{
			if (color == null)
				return null;
			ConsoleColorPair ccPair;
			return colors.TryGetValue(color.Value, out ccPair) ? ccPair : (ConsoleColorPair?)null;
		}
	}

	sealed class ConsoleColorizerOutput : IDecompilerOutput
	{
		readonly ColorProvider colorProvider;
		readonly TextWriter writer;
		readonly Indenter indenter;
		bool addIndent = true;
		int position;

		public int Length => position;
		public int NextPosition => position + (addIndent ? indenter.String.Length : 0);

		bool IDecompilerOutput.UsesCustomData => false;

		public ConsoleColorizerOutput(TextWriter writer, ColorProvider colorProvider, Indenter indenter)
		{
			if (writer == null)
				throw new ArgumentNullException(nameof(writer));
			if (colorProvider == null)
				throw new ArgumentNullException(nameof(colorProvider));
			if (indenter == null)
				throw new ArgumentNullException(nameof(indenter));
			this.writer = writer;
			this.colorProvider = colorProvider;
			this.indenter = indenter;
		}

		void IDecompilerOutput.AddCustomData<TData>(string id, TData data) { }
		public void IncreaseIndent() => indenter.IncreaseIndent();
		public void DecreaseIndent() => indenter.DecreaseIndent();

		public void WriteLine()
		{
			var nlArray = newLineArray;
			writer.Write(nlArray);
			position += nlArray.Length;
			addIndent = true;
		}
		static readonly char[] newLineArray = Environment.NewLine.ToCharArray();

		void AddIndent()
		{
			if (!addIndent)
				return;
			addIndent = false;
			var s = indenter.String;
			writer.Write(s);
			position += s.Length;
		}

		void AddText(string text, object color)
		{
			if (addIndent)
				AddIndent();
			var colorPair = colorProvider.GetColor(color as TextColor?);
			if (colorPair != null)
			{
				if (colorPair.Value.Foreground != null)
					Console.ForegroundColor = colorPair.Value.Foreground.Value;
				if (colorPair.Value.Background != null)
					Console.BackgroundColor = colorPair.Value.Background.Value;
				writer.Write(text);
				Console.ResetColor();
			}
			else
				writer.Write(text);

			position += text.Length;
		}

		void AddText(string text, int index, int length, object color)
		{
			if (index == 0 && length == text.Length)
				AddText(text, color);
			else
				AddText(text.Substring(index, length), color);
		}

		public void Write(string text, object color) => AddText(text, color);
		public void Write(string text, int index, int length, object color) => AddText(text, index, length, color);
		public void Write(string text, object reference, DecompilerReferenceFlags flags, object color) => AddText(text, color);
		public void Write(string text, int index, int length, object reference, DecompilerReferenceFlags flags, object color) => AddText(text, index, length, color);
		public override string ToString() => writer.ToString();
		public void Dispose() => writer.Dispose();
	}

	public sealed class DnSpyDecompiler : IMSBuildProjectWriterLogger
	{
		bool isRecursive = false;
		bool useGac = true;
		bool addCorlibRef = true;
		bool createSlnFile = true;
		bool unpackResources = true;
		bool createResX = true;
		bool decompileBaml = true;
		bool colorizeOutput;
		Guid projectGuid = Guid.NewGuid();
		int numThreads;
		int mdToken;
		int spaces;
		string typeName;
		ProjectVersion projectVersion = ProjectVersion.VS2010;
		string outputDir;
		string slnName = "solution.sln";
		readonly List<string> files;
		readonly List<string> asmPaths;
		readonly List<string> userGacPaths;
		readonly List<string> gacFiles;
		string language = DecompilerConstants.LANGUAGE_CSHARP.ToString();
		readonly DecompilationContext decompilationContext;
		readonly ModuleContext moduleContext;
		readonly AssemblyResolver assemblyResolver;
		readonly IBamlDecompiler bamlDecompiler;
		readonly HashSet<string> reservedOptions;

		static readonly char PATHS_SEP = Path.PathSeparator;

		public DnSpyDecompiler()
		{
			files = new List<string>();
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
			reservedOptions = GetReservedOptions();
			colorizeOutput = !Console.IsOutputRedirected;

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
				ParseCommandLine(args);
				if (allLanguages.Length == 0)
					throw new ErrorException("No languages were found. Make sure that the language dll files exist in the same folder as this program.");
				if (GetLanguage() == null)
					throw new ErrorException(string.Format("Language {0} does not exist", language));
				Decompile();
			}
			catch (ErrorException ex)
			{
				PrintHelp();
				Console.WriteLine();
				Console.WriteLine("ERROR: {0}", ex.Message);
				return 1;
			}
			catch (Exception ex)
			{
				Dump(ex);
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
			new UsageInfo("--no-sln", null, "don't create a .sln file"),
			new UsageInfo("--sln-name", "name", "name of the .sln file"),
			new UsageInfo("--threads", "N", "number of worker threads. Default is to use one thread per CPU core"),
			new UsageInfo("--no-resources", null, "don't unpack resources"),
			new UsageInfo("--no-resx", null, "don't create .resx files"),
			new UsageInfo("--no-baml", null, "don't decompile baml to xaml"),
			new UsageInfo("--no-color", null, "don't colorize the text"),
			new UsageInfo("--spaces", "N", "Size of a tab in spaces or 0 to use one tab"),
			new UsageInfo("--vs", "N", string.Format("Visual Studio version, 2005, 2008, ..., {0}", 2015)),
			new UsageInfo("--project-guid", "N", "project guid"),
			new UsageInfo("-t", "name", "decompile the type with the specified name to stdout. Either Namespace.Name or Name, case insensitive"),
			new UsageInfo("--type", "name", "same as -t"),
			new UsageInfo("--md", "N", "decompile the member with metadata token N to stdout"),
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
			new HelpInfo(@"Decompiles the member with token 0x06000123", @"--md 0x06000123 file.dll"),
			new HelpInfo(@"Decompiles System.Int32 from mscorlib", @"-t system.int32 --gac-file ""mscorlib, Version=4.0.0.0"""),
		};

		string GetOptionName(IDecompilerOption opt, string extraPrefix = null)
		{
			var prefix = "--" + extraPrefix;
			var o = prefix + FixInvalidSwitchChars((opt.Name != null ? opt.Name : opt.Guid.ToString()));
			if (reservedOptions.Contains(o))
				o = prefix + FixInvalidSwitchChars(opt.Guid.ToString());
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

		void Dump(Exception ex)
		{
			while (ex != null)
			{
				Console.WriteLine("ERROR: {0}", ex.GetType());
				Console.WriteLine("  {0}", ex.Message);
				Console.WriteLine("  {0}", ex.StackTrace);
				ex = ex.InnerException;
			}
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
		HashSet<string> GetReservedOptions()
		{
			var hash = new HashSet<string>(StringComparer.Ordinal);
			foreach (var a in ourOptions)
			{
				hash.Add("--" + a);
				hash.Add("--" + BOOLEAN_NO_PREFIX + a);
				hash.Add("--" + BOOLEAN_DONT_PREFIX + a);
			}
			return hash;
		}
		static readonly string[] ourOptions = new string[] {
			// Don't include 'no-' and 'dont-'
			"recursive",
			"output-dir",
			"lang",
			"asm-path",
			"user-gac",
			"gac",
			"stdlib",
			"sln",
			"sln-name",
			"threads",
			"vs",
			"resources",
			"resx",
			"baml",
			"color",
			"spaces",
			"type",
			"md",
			"gac-file",
			"project-guid",
		};

		void ParseCommandLine(string[] args)
		{
			if (args.Length == 0)
				throw new ErrorException("No options specified");

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

						case "-o":
						case "--output-dir":
							if (next == null)
								throw new ErrorException("Missing output directory");
							outputDir = Path.GetFullPath(next);
							i++;
							break;

						case "-l":
						case "--lang":
							if (next == null)
								throw new ErrorException("Missing language name");
							language = next;
							i++;
							if (GetLanguage() == null)
								throw new ErrorException(string.Format("Language '{0}' doesn't exist", language));
							lang = null;
							langDict = null;
							break;

						case "--asm-path":
							if (next == null)
								throw new ErrorException("Missing assembly search path");
							asmPaths.AddRange(next.Split(new char[] { PATHS_SEP }, StringSplitOptions.RemoveEmptyEntries));
							i++;
							break;

						case "--user-gac":
							if (next == null)
								throw new ErrorException("Missing user GAC path");
							userGacPaths.AddRange(next.Split(new char[] { PATHS_SEP }, StringSplitOptions.RemoveEmptyEntries));
							i++;
							break;

						case "--no-gac":
							useGac = false;
							break;

						case "--no-stdlib":
							addCorlibRef = false;
							break;

						case "--no-sln":
							createSlnFile = false;
							break;

						case "--sln-name":
							if (next == null)
								throw new ErrorException("Missing .sln name");
							slnName = next;
							i++;
							if (Path.IsPathRooted(slnName))
								throw new ErrorException(string.Format(".sln name ({0}) must be relative to project directory", slnName));
							break;

						case "--threads":
							if (next == null)
								throw new ErrorException("Missing number of threads");
							i++;
							numThreads = SimpleTypeConverter.ParseInt32(next, int.MinValue, int.MaxValue, out error);
							if (!string.IsNullOrEmpty(error))
								throw new ErrorException(error);
							break;

						case "--vs":
							if (next == null)
								throw new ErrorException("Missing Visual Studio version");
							i++;
							int vsVer;
							vsVer = SimpleTypeConverter.ParseInt32(next, int.MinValue, int.MaxValue, out error);
							if (!string.IsNullOrEmpty(error))
								throw new ErrorException(error);
							switch (vsVer)
							{
								case 2005: projectVersion = ProjectVersion.VS2005; break;
								case 2008: projectVersion = ProjectVersion.VS2008; break;
								case 2010: projectVersion = ProjectVersion.VS2010; break;
								case 2012: projectVersion = ProjectVersion.VS2012; break;
								case 2013: projectVersion = ProjectVersion.VS2013; break;
								case 2015: projectVersion = ProjectVersion.VS2015; break;
								default: throw new ErrorException(string.Format("Invalid Visual Studio version: {0}", vsVer));
							}
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

						case "--no-color":
							colorizeOutput = false;
							break;

						case "--spaces":
							if (next == null)
								throw new ErrorException("Missing argument");
							const int MIN_SPACES = 0, MAX_SPACES = 100;
							if (!int.TryParse(next, out spaces) || spaces < MIN_SPACES || spaces > MAX_SPACES)
								throw new ErrorException(string.Format("Number of spaces must be between {0} and {1}", MIN_SPACES, MAX_SPACES));
							i++;
							break;

						case "-t":
						case "--type":
							if (next == null)
								throw new ErrorException("Missing full name of type");
							i++;
							typeName = next;
							break;

						case "--md":
							if (next == null)
								throw new ErrorException("Missing metadata token");
							i++;
							mdToken = SimpleTypeConverter.ParseInt32(next, int.MinValue, int.MaxValue, out error);
							if (!string.IsNullOrEmpty(error))
								throw new ErrorException(error);
							break;

						case "--gac-file":
							if (next == null)
								throw new ErrorException("Missing GAC assembly name");
							i++;
							gacFiles.Add(next);
							break;

						case "--project-guid":
							if (next == null || !Guid.TryParse(next, out projectGuid))
								throw new ErrorException("Invalid GUID");
							i++;
							break;

						default:
							Tuple<IDecompilerOption, Action<string>> tuple;
							if (langDict.TryGetValue(arg, out tuple))
							{
								bool hasArg = tuple.Item1.Type != typeof(bool);
								if (hasArg && next == null)
									throw new ErrorException("Missing option argument");
								if (hasArg)
									i++;
								tuple.Item2(next);
								break;
							}

							throw new ErrorException(string.Format("Invalid option: {0}", arg));
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
				throw new ErrorException(error);
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

			if (mdToken != 0 || typeName != null)
			{
				if (files.Count == 0)
					throw new ErrorException("Missing .NET filename");
				if (files.Count != 1)
					throw new ErrorException("Only one file can be decompiled when using --md");

				IMemberDef member;
				if (typeName != null)
					member = FindType(files[0].Module, typeName);
				else
					member = files[0].Module.ResolveToken(mdToken) as IMemberDef;
				if (member == null)
				{
					if (typeName != null)
						throw new ErrorException(string.Format("Type {0} couldn't be found", typeName));
					throw new ErrorException("Invalid metadata token");
				}

				var writer = Console.Out;
				IDecompilerOutput output;
				if (colorizeOutput)
					output = new ConsoleColorizerOutput(writer, CreateColorProvider(), GetIndenter());
				else
					output = new TextWriterDecompilerOutput(writer, GetIndenter());

				var lang = GetLanguage();
				if (member is MethodDef)
					lang.Decompile((MethodDef)member, output, decompilationContext);
				else if (member is FieldDef)
					lang.Decompile((FieldDef)member, output, decompilationContext);
				else if (member is PropertyDef)
					lang.Decompile((PropertyDef)member, output, decompilationContext);
				else if (member is EventDef)
					lang.Decompile((EventDef)member, output, decompilationContext);
				else if (member is TypeDef)
					lang.Decompile((TypeDef)member, output, decompilationContext);
				else
					throw new ErrorException("Only types, methods, fields, events and properties can be decompiled");
			}
			else
			{
				if (string.IsNullOrEmpty(outputDir))
					throw new ErrorException("Missing output directory");
				if (GetLanguage().ProjectFileExtension == null)
					throw new ErrorException(string.Format("Language {0} doesn't support creating project files", GetLanguage().UniqueNameUI));

				var options = new ProjectCreatorOptions(outputDir, decompilationContext.CancellationToken);
				options.Logger = this;
				options.ProjectVersion = projectVersion;
				options.NumberOfThreads = numThreads;
				options.ProjectModules.AddRange(files);
				options.UserGACPaths.AddRange(userGacPaths);
				options.CreateDecompilerOutput = textWriter => new TextWriterDecompilerOutput(textWriter, GetIndenter());
				if (createSlnFile && !string.IsNullOrEmpty(slnName))
					options.SolutionFilename = slnName;
				var creator = new MSBuildProjectCreator(options);
				creator.Create();
			}
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
			return module.GetTypes().FirstOrDefault(a => {
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
			return module.GetTypes().FirstOrDefault(a => {
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
						throw new ErrorException(string.Format("File/directory '{0}' doesn't exist", file));
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
					throw new ErrorException(string.Format("Couldn't resolve GAC assembly '{0}'", asmName));
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
			return AllLanguages.FirstOrDefault(a => {
				if (StringComparer.OrdinalIgnoreCase.Equals(language, a.UniqueNameUI))
					return true;
				if (hasGuid && (guid.Equals(a.UniqueGuid) || guid.Equals(a.GenericGuid)))
					return true;
				return false;
			});
		}

		IDecompiler[] AllLanguages => allLanguages;
		readonly IDecompiler[] allLanguages;

		public void Error(string message)
		{
			errors++;
			Console.Error.WriteLine(string.Format("ERROR: {0}", message));
		}
		int errors;

		ColorProvider CreateColorProvider()
		{
			var provider = new ColorProvider();
			provider.Add(TextColor.Operator, null, null);
			provider.Add(TextColor.Punctuation, null, null);
			provider.Add(TextColor.Number, null, null);
			provider.Add(TextColor.Comment, ConsoleColor.Green, null);
			provider.Add(TextColor.Keyword, ConsoleColor.Cyan, null);
			provider.Add(TextColor.String, ConsoleColor.DarkYellow, null);
			provider.Add(TextColor.VerbatimString, ConsoleColor.DarkYellow, null);
			provider.Add(TextColor.Char, ConsoleColor.DarkYellow, null);
			provider.Add(TextColor.Namespace, ConsoleColor.Yellow, null);
			provider.Add(TextColor.Type, ConsoleColor.Magenta, null);
			provider.Add(TextColor.SealedType, ConsoleColor.Magenta, null);
			provider.Add(TextColor.StaticType, ConsoleColor.Magenta, null);
			provider.Add(TextColor.Delegate, ConsoleColor.Magenta, null);
			provider.Add(TextColor.Enum, ConsoleColor.Magenta, null);
			provider.Add(TextColor.Interface, ConsoleColor.Magenta, null);
			provider.Add(TextColor.ValueType, ConsoleColor.Green, null);
			provider.Add(TextColor.Module, ConsoleColor.DarkMagenta, null);
			provider.Add(TextColor.TypeGenericParameter, ConsoleColor.Magenta, null);
			provider.Add(TextColor.MethodGenericParameter, ConsoleColor.Magenta, null);
			provider.Add(TextColor.InstanceMethod, ConsoleColor.DarkYellow, null);
			provider.Add(TextColor.StaticMethod, ConsoleColor.DarkYellow, null);
			provider.Add(TextColor.ExtensionMethod, ConsoleColor.DarkYellow, null);
			provider.Add(TextColor.InstanceField, ConsoleColor.Magenta, null);
			provider.Add(TextColor.EnumField, ConsoleColor.Magenta, null);
			provider.Add(TextColor.LiteralField, ConsoleColor.Magenta, null);
			provider.Add(TextColor.StaticField, ConsoleColor.Magenta, null);
			provider.Add(TextColor.InstanceEvent, ConsoleColor.Magenta, null);
			provider.Add(TextColor.StaticEvent, ConsoleColor.Magenta, null);
			provider.Add(TextColor.InstanceProperty, ConsoleColor.Magenta, null);
			provider.Add(TextColor.StaticProperty, ConsoleColor.Magenta, null);
			provider.Add(TextColor.Local, ConsoleColor.White, null);
			provider.Add(TextColor.Parameter, ConsoleColor.White, null);
			provider.Add(TextColor.PreprocessorKeyword, ConsoleColor.Blue, null);
			provider.Add(TextColor.PreprocessorText, null, null);
			provider.Add(TextColor.Label, ConsoleColor.DarkRed, null);
			provider.Add(TextColor.OpCode, ConsoleColor.Cyan, null);
			provider.Add(TextColor.ILDirective, ConsoleColor.Cyan, null);
			provider.Add(TextColor.ILModule, ConsoleColor.DarkMagenta, null);
			provider.Add(TextColor.ExcludedCode, null, null);
			provider.Add(TextColor.XmlDocCommentAttributeName, ConsoleColor.DarkGreen, null);
			provider.Add(TextColor.XmlDocCommentAttributeQuotes, ConsoleColor.DarkGreen, null);
			provider.Add(TextColor.XmlDocCommentAttributeValue, ConsoleColor.DarkGreen, null);
			provider.Add(TextColor.XmlDocCommentCDataSection, ConsoleColor.DarkGreen, null);
			provider.Add(TextColor.XmlDocCommentComment, ConsoleColor.DarkGreen, null);
			provider.Add(TextColor.XmlDocCommentDelimiter, ConsoleColor.DarkGreen, null);
			provider.Add(TextColor.XmlDocCommentEntityReference, ConsoleColor.DarkGreen, null);
			provider.Add(TextColor.XmlDocCommentName, ConsoleColor.DarkGreen, null);
			provider.Add(TextColor.XmlDocCommentProcessingInstruction, ConsoleColor.DarkGreen, null);
			provider.Add(TextColor.XmlDocCommentText, ConsoleColor.DarkGreen, null);
			provider.Add(TextColor.Error, ConsoleColor.Red, null);
			return provider;
		}
	}
}
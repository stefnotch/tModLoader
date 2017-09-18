using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;
using dnSpy.Decompiler.MSBuild;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Threading.Tasks;

namespace Terraria.ModLoader.Setup
{

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

		public void Run()
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
			}
		}

		/// <summary>
		/// Removes some useless comments in the output
		/// </summary>
		void RemoveTokenComments()
		{

			const string TOKENS_OPTION = "tokens";
			var tokenComments = GetLanguage().Settings.TryGetOption("tokens");
			if (tokenComments != null)
			{
				tokenComments.Value = false;
			}
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
			//files[1].Module.Resources --> Merge!
			//files[1].Module.resources --> Merge?
			//files[1].Module.Types
			//files[1].Module.types
			var files = new List<ProjectModuleOptions>(GetDotNetFiles());
			if(Merge == true) RemoveDuplicates(files);
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
			var creator = new MSBuildProjectCreator(options); //TODO: Change the creator
			creator.Create();

		}
		/// <summary>
		/// Removes all duplicate resources and all duplicate types
		/// </summary>
		private void RemoveDuplicates(List<ProjectModuleOptions> files)
		{
			List<string> resourceCache = new List<string>();
			List<string> typeCache = new List<string>();
			foreach (ProjectModuleOptions project in files)
			{
				foreach(Resource resource in project.Module.Resources)
				{
					if (resourceCache.Contains(resource.Name))
					{
						project.Module.Resources.Remove(resource);
						//TODO: Invalidate the resource
					}
					else
					{
						resourceCache.Add(resource.Name);
					}
				}
				
				foreach(TypeDef type in project.Module.Types)
				{
					if (typeCache.Contains(type.FullName))
					{
						project.Module.Types.Remove(type);
						//TODO: Invalidate the type
					}
					else
					{
						typeCache.Add(type.FullName);
					}
				}
				project.Module.ResetTypeDefFindCache();
			}			
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

		public bool Merge { get; set; }

		readonly IDecompiler[] allLanguages;
	}
}
